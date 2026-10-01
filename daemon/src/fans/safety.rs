//! The guarantee that no fan channel is ever left stuck on manual PWM.
//!
//! Manual PWM is sticky in the chip: a header left on manual holds its last duty forever, so
//! a daemon that exits without releasing one leaves that fan at whatever it happened to be.
//! Two of the four layers that prevent it live here — the guard itself, which releases every
//! channel from `Drop` (which is why release builds use `panic = "unwind"`), and an
//! independent re-arming deadman thread that does the same if the control loop stalls while
//! the process stays alive. The other two are systemd's watchdog and `ExecStopPost`.

use super::{FanChannel, FanController, lock};
use std::io;
use std::sync::{Arc, Condvar, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};
use tracing::error;

/// Owns the "never leave fans stuck on manual" guarantee. Manual PWM is sticky in the
/// chip: if the process dies without releasing a channel it holds its last duty forever,
/// which is a real thermal incident if that duty was low during e.g. a resilver.
///
/// Two independent in-process backstops:
///  - Drop releases every channel. Covers normal shutdown, an early return, and a panic
///    unwinding through the owner. Unconditional: it releases even if the deadman already
///    did, because the control loop re-asserts manual mode every poll and may well have
///    taken the channels back since.
///  - The deadman thread releases everything if `kick` isn't called often enough. Covers a
///    control loop that hangs while the process stays alive. It is not a one-shot: the next
///    kick re-arms it, so a loop that stalls, recovers and stalls again is protected the
///    second time too.
///
/// Neither covers the process dying outright (SIGKILL, SIGBUS, OOM kill) or freezing as a
/// whole. Those need something external: deploy/release-fans.sh runs as the unit's
/// ExecStopPost, and the unit's WatchdogSec kills a daemon that stops reporting in.
///
/// Releasing is always best-effort per channel: one channel's sysfs write failing must not
/// stop the remaining channels from being released.
pub struct FanSafetyGuard {
    shared: Arc<GuardShared>,
    deadman: Option<JoinHandle<()>>,
}

struct GuardShared {
    controller: Arc<dyn FanController>,
    channels: Vec<FanChannel>,
    state: Mutex<DeadmanState>,
    wake: Condvar,
}

struct DeadmanState {
    deadline: Instant,
    tripped: bool,
    stopping: bool,
}

impl FanSafetyGuard {
    pub fn new(controller: Arc<dyn FanController>, channels: Vec<FanChannel>, deadman_timeout: Duration) -> io::Result<Self> {
        let shared = Arc::new(GuardShared {
            controller,
            channels,
            state: Mutex::new(DeadmanState { deadline: Instant::now() + deadman_timeout, tripped: false, stopping: false }),
            wake: Condvar::new(),
        });

        for channel in &shared.channels {
            if let Err(error) = shared.controller.take_manual_control(channel) {
                // No guard will exist for the caller to drop, so any channel already taken
                // has to be handed back right here or it never will be.
                shared.release_all();
                return Err(io::Error::new(error.kind(), format!("taking manual control of fan channel '{}': {error}", channel.id)));
            }
        }

        let deadman = std::thread::Builder::new().name("deadman".to_owned()).spawn({
            let shared = Arc::clone(&shared);
            move || shared.run_deadman()
        })?;

        Ok(Self { shared, deadman: Some(deadman) })
    }

    /// Call once per completed control-loop iteration to prove the loop is still alive.
    pub fn kick(&self, deadman_timeout: Duration) {
        let mut state = lock(&self.shared.state);
        state.deadline = Instant::now() + deadman_timeout;
        state.tripped = false;
        self.shared.wake.notify_all();
    }
}

impl Drop for FanSafetyGuard {
    fn drop(&mut self) {
        lock(&self.shared.state).stopping = true;
        self.shared.wake.notify_all();
        if let Some(deadman) = self.deadman.take() {
            let _ = deadman.join();
        }

        self.shared.release_all();
    }
}

impl GuardShared {
    fn run_deadman(&self) {
        let mut state = lock(&self.state);
        while !state.stopping {
            let now = Instant::now();
            if !state.tripped && now >= state.deadline {
                state.tripped = true;
                error!(
                    "Deadman expired: the control loop has not completed a poll in time. Releasing every fan channel back to automatic control."
                );
                // Held across the release on purpose: a kick arriving now waits, so it
                // can never be followed by this release undoing the loop's fresh takeover.
                self.release_all();
            }

            // Once tripped there is nothing to time until the next kick re-arms it.
            let wait = if state.tripped { Duration::from_secs(3600) } else { state.deadline.saturating_duration_since(now) };
            state = self.wake.wait_timeout(state, wait).map_or_else(|e| e.into_inner().0, |(guard, _)| guard);
        }
    }

    fn release_all(&self) {
        for channel in &self.channels {
            if let Err(error) = self.controller.release_to_auto(channel) {
                error!("Failed to release fan channel '{}' back to automatic control; it may be stuck on manual: {error}", channel.id);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::fans::test_support::{channel, pause, setup};

    /// Long enough that the deadman never fires during a test that is not about the deadman.
    const LONG: Duration = Duration::from_secs(300);
    /// Short enough that a test can wait out a deadman cycle.
    const SHORT: Duration = Duration::from_millis(50);

    #[test]
    fn guard_takes_manual_control_on_construction_and_releases_on_drop() {
        let cpu = channel("cpu", 1);
        let (sysfs, controller) = setup(&[&cpu]);

        let guard = FanSafetyGuard::new(controller, vec![cpu.clone()], LONG).unwrap();
        assert_eq!(sysfs.get(&cpu.enable_path()), "1");

        drop(guard);
        assert_eq!(sysfs.get(&cpu.enable_path()), "5");
    }

    #[test]
    fn guard_releases_when_a_panic_unwinds_through_its_owner() {
        let cpu = channel("cpu", 1);
        let (sysfs, controller) = setup(&[&cpu]);

        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let _guard = FanSafetyGuard::new(controller, vec![cpu.clone()], LONG).unwrap();
            panic!("simulated control loop bug");
        }));

        assert!(result.is_err());
        assert_eq!(sysfs.get(&cpu.enable_path()), "5");
    }

    #[test]
    fn deadman_releases_channels_when_not_kicked() {
        let cpu = channel("cpu", 1);
        let (sysfs, controller) = setup(&[&cpu]);
        let _guard = FanSafetyGuard::new(controller, vec![cpu.clone()], SHORT).unwrap();

        pause(300);

        assert_eq!(sysfs.get(&cpu.enable_path()), "5");
    }

    #[test]
    fn kick_postpones_the_deadman() {
        let cpu = channel("cpu", 1);
        let (sysfs, controller) = setup(&[&cpu]);
        let guard = FanSafetyGuard::new(controller, vec![cpu.clone()], Duration::from_millis(200)).unwrap();

        for _ in 0..6 {
            pause(50);
            guard.kick(Duration::from_millis(200));
        }

        assert_eq!(sysfs.get(&cpu.enable_path()), "1");
    }

    #[test]
    fn kick_after_the_deadman_fired_re_arms_it() {
        let cpu = channel("cpu", 1);
        let (sysfs, controller) = setup(&[&cpu]);
        let guard = FanSafetyGuard::new(controller.clone(), vec![cpu.clone()], SHORT).unwrap();

        pause(300);
        assert_eq!(sysfs.get(&cpu.enable_path()), "5");

        // The stalled loop recovers: it re-asserts manual mode and kicks, as any poll does...
        controller.take_manual_control(&cpu).unwrap();
        guard.kick(SHORT);
        assert_eq!(sysfs.get(&cpu.enable_path()), "1");

        // ...and then stalls a second time. The deadman has to catch that one too.
        pause(300);
        assert_eq!(sysfs.get(&cpu.enable_path()), "5");
    }

    #[test]
    fn drop_still_releases_channels_retaken_after_the_deadman_fired() {
        let cpu = channel("cpu", 1);
        let (sysfs, controller) = setup(&[&cpu]);
        let guard = FanSafetyGuard::new(controller.clone(), vec![cpu.clone()], SHORT).unwrap();

        pause(300);
        controller.take_manual_control(&cpu).unwrap();
        drop(guard);

        assert_eq!(sysfs.get(&cpu.enable_path()), "5");
    }

    #[test]
    fn constructor_failure_releases_channels_already_taken() {
        let (cpu, case) = (channel("cpu", 1), channel("case", 2));
        let (sysfs, controller) = setup(&[&cpu, &case]);
        sysfs.fail_writes_to(&case.enable_path());

        let result = FanSafetyGuard::new(controller, vec![cpu.clone(), case], LONG);

        assert!(result.is_err_and(|e| e.to_string().contains("'case'")));
        assert_eq!(sysfs.get(&cpu.enable_path()), "5");
    }

    #[test]
    fn one_channel_failing_to_release_does_not_stop_the_others() {
        let (cpu, case, rear) = (channel("cpu", 1), channel("case", 2), channel("rear", 3));
        let (sysfs, controller) = setup(&[&cpu, &case, &rear]);
        let guard = FanSafetyGuard::new(controller, vec![cpu.clone(), case.clone(), rear.clone()], LONG).unwrap();
        sysfs.fail_writes_to(&case.enable_path());

        drop(guard);

        assert_eq!(sysfs.get(&cpu.enable_path()), "5");
        assert_eq!(sysfs.get(&case.enable_path()), "1");
        assert_eq!(sysfs.get(&rear.enable_path()), "5");
    }

    #[test]
    fn deadman_survives_a_release_failure_and_still_releases_the_rest() {
        let (cpu, case) = (channel("cpu", 1), channel("case", 2));
        let (sysfs, controller) = setup(&[&cpu, &case]);
        let _guard = FanSafetyGuard::new(controller, vec![cpu.clone(), case.clone()], SHORT).unwrap();
        sysfs.fail_writes_to(&cpu.enable_path());

        pause(300);

        assert_eq!(sysfs.get(&case.enable_path()), "5");
    }
}
