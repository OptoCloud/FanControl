//! Fan headers: the sysfs PWM controller, stall detection, and the safety guard that
//! guarantees no channel is ever left stuck on manual.

use crate::log;
use crate::sysfs::{self, SysFs};
use serde::Serialize;
use std::collections::HashMap;
use std::io;
use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

/// Values accepted by nct6775/nct6798's pwmN_enable sysfs attribute.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum PwmMode {
    /// Fans jump to full speed. Never written by this daemon.
    Disabled = 0,
    Manual = 1,
    ThermalCruise = 2,
    SpeedCruise = 3,
    /// NCT6775F only; listed so a read-back of it isn't reported as unknown.
    SmartFanIII = 4,
    /// BIOS "Smart Fan IV": the multi-slope curve mode the board ships in.
    SmartFanIV = 5,
}

impl PwmMode {
    fn from_raw(raw: &str) -> Option<Self> {
        match raw.parse::<u8>().ok()? {
            0 => Some(Self::Disabled),
            1 => Some(Self::Manual),
            2 => Some(Self::ThermalCruise),
            3 => Some(Self::SpeedCruise),
            4 => Some(Self::SmartFanIII),
            5 => Some(Self::SmartFanIV),
            _ => None,
        }
    }

    /// True for the modes where the chip itself regulates the fan.
    fn is_automatic(self) -> bool {
        !matches!(self, Self::Disabled | Self::Manual)
    }
}

/// One physical fan header, addressed by its sysfs pwmN attribute set. The mapping from
/// pwmN to a silkscreen header is board-specific and NOT guessable: it has to be verified
/// by walking each channel in manual mode and watching which tach responds.
#[derive(Debug, Clone, PartialEq)]
pub struct FanChannel {
    pub id: String,
    /// hwmon chip directory containing this pwmN/fanN pair.
    pub chip_dir: String,
    pub index: u32,
    /// Floor below which this fan stalls; the curve result is never allowed under it.
    pub minimum_duty_percent: u8,
}

impl FanChannel {
    pub fn pwm_path(&self) -> String {
        sysfs::join(&[&self.chip_dir, &format!("pwm{}", self.index)])
    }

    pub fn enable_path(&self) -> String {
        sysfs::join(&[&self.chip_dir, &format!("pwm{}_enable", self.index)])
    }

    pub fn tach_path(&self) -> String {
        sysfs::join(&[&self.chip_dir, &format!("fan{}_input", self.index)])
    }
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FanStatus {
    pub id: String,
    pub duty_percent: u8,
    pub rpm: Option<u32>,
    /// None if pwmN_enable couldn't be read or held a value this daemon doesn't know.
    pub mode: Option<PwmMode>,
    /// See StallDetector.
    pub stalled: bool,
}

pub trait FanController: Send + Sync {
    /// Takes manual control of the channel (pwmN_enable=1). Idempotent.
    fn take_manual_control(&self, channel: &FanChannel) -> io::Result<()>;

    /// Writes a duty cycle of 0-100%. The channel must already be under manual control.
    fn set_duty_percent(&self, channel: &FanChannel, duty_percent: u8) -> io::Result<()>;

    /// Hands the channel back to whichever automatic mode it was in before it was first
    /// taken (Smart Fan IV if that isn't known, or wasn't an automatic mode). Always safe
    /// to call, including on channels never taken.
    fn release_to_auto(&self, channel: &FanChannel) -> io::Result<()>;

    fn read_status(&self, channel: &FanChannel) -> FanStatus;
}

pub struct SysfsFanController {
    sysfs: Arc<dyn SysFs>,
    /// Whatever mode each channel was in before this daemon first touched it.
    original_modes: Mutex<HashMap<String, PwmMode>>,
}

impl SysfsFanController {
    pub fn new(sysfs: Arc<dyn SysFs>) -> Self {
        Self { sysfs, original_modes: Mutex::new(HashMap::new()) }
    }

    fn read_mode(&self, channel: &FanChannel) -> Option<PwmMode> {
        PwmMode::from_raw(&self.sysfs.read(&channel.enable_path())?)
    }
}

impl FanController for SysfsFanController {
    fn take_manual_control(&self, channel: &FanChannel) -> io::Result<()> {
        let enable_path = channel.enable_path();
        {
            // Only the first sighting counts: this is re-asserted every poll, and later
            // reads would just see our own "1".
            let mut original_modes = lock(&self.original_modes);
            if !original_modes.contains_key(&enable_path)
                && let Some(mode) = self.read_mode(channel)
            {
                original_modes.insert(enable_path.clone(), mode);
            }
        }

        self.sysfs.write(&enable_path, &(PwmMode::Manual as u8).to_string())
    }

    fn set_duty_percent(&self, channel: &FanChannel, duty_percent: u8) -> io::Result<()> {
        if duty_percent > 100 {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, format!("duty must be 0-100 (is {duty_percent})")));
        }

        let raw = (f64::from(duty_percent) / 100.0 * 255.0).round() as u8;
        self.sysfs.write(&channel.pwm_path(), &raw.to_string())
    }

    fn release_to_auto(&self, channel: &FanChannel) -> io::Result<()> {
        // Only ever restore a mode where the chip regulates the fan. If the channel was
        // found already on Manual (a previous instance died without releasing it) or
        // Disabled, restoring that would recreate the stuck-fan situation release exists
        // to prevent, so fall back to the board's shipping default instead.
        let mode = lock(&self.original_modes)
            .get(&channel.enable_path())
            .copied()
            .filter(|mode| mode.is_automatic())
            .unwrap_or(PwmMode::SmartFanIV);

        self.sysfs.write(&channel.enable_path(), &(mode as u8).to_string())
    }

    fn read_status(&self, channel: &FanChannel) -> FanStatus {
        let raw_pwm = self.sysfs.read(&channel.pwm_path()).and_then(|raw| raw.parse::<u8>().ok());

        FanStatus {
            id: channel.id.clone(),
            duty_percent: raw_pwm.map_or(0, |pwm| (f64::from(pwm) / 255.0 * 100.0).round() as u8),
            rpm: self.sysfs.read(&channel.tach_path()).and_then(|raw| raw.parse().ok()),
            mode: self.read_mode(channel),
            stalled: false,
        }
    }
}

/// Flags a fan as stalled once its tach has read 0 RPM for several consecutive polls while
/// being driven under manual control. Duty is always held at or above the channel's
/// minimum, so a sustained 0 RPM means a dead, jammed or unplugged fan. Consecutive rather
/// than instantaneous because a fan legitimately reads 0 for a moment while spinning up
/// from rest. A missing tach reading is "unknown", never "stalled".
pub struct StallDetector {
    polls_required: u32,
    zero_rpm_polls: HashMap<String, u32>,
}

impl StallDetector {
    pub fn new(polls_required: u32) -> Self {
        Self { polls_required, zero_rpm_polls: HashMap::new() }
    }

    pub fn apply(&mut self, mut status: FanStatus) -> FanStatus {
        let zero_while_driven = status.mode == Some(PwmMode::Manual) && status.rpm == Some(0) && status.duty_percent > 0;
        let count = self.zero_rpm_polls.entry(status.id.clone()).or_insert(0);
        *count = if zero_while_driven { (*count + 1).min(self.polls_required) } else { 0 };

        status.stalled = *count >= self.polls_required;
        status
    }
}

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
                log!(
                    Critical,
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
                log!(
                    Critical,
                    "Failed to release fan channel '{}' back to automatic control; it may be stuck on manual: {error}",
                    channel.id
                );
            }
        }
    }
}

/// A poisoned mutex only means another thread panicked while holding it. None of the data
/// guarded here can be left half-updated in a way that matters, and refusing to proceed
/// would block the release path, which must always run.
fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::sysfs::fake::FakeSysFs;

    fn channel(id: &str, index: u32) -> FanChannel {
        FanChannel { id: id.to_owned(), chip_dir: "/sys/class/hwmon/hwmon3".to_owned(), index, minimum_duty_percent: 20 }
    }

    fn setup(channels: &[&FanChannel]) -> (Arc<FakeSysFs>, Arc<SysfsFanController>) {
        let sysfs = Arc::new(FakeSysFs::new());
        for channel in channels {
            sysfs.set(&channel.enable_path(), "5");
            sysfs.set(&channel.pwm_path(), "0");
        }
        let controller = Arc::new(SysfsFanController::new(sysfs.clone()));
        (sysfs, controller)
    }

    fn pause(millis: u64) {
        std::thread::sleep(Duration::from_millis(millis));
    }

    const LONG: Duration = Duration::from_secs(300);
    const SHORT: Duration = Duration::from_millis(50);

    // ---- controller ----

    #[test]
    fn take_manual_control_writes_mode_one_and_release_writes_five() {
        let cpu = channel("cpu", 1);
        let (sysfs, controller) = setup(&[&cpu]);

        controller.take_manual_control(&cpu).unwrap();
        assert_eq!(sysfs.get(&cpu.enable_path()), "1");

        controller.release_to_auto(&cpu).unwrap();
        assert_eq!(sysfs.get(&cpu.enable_path()), "5");
    }

    #[test]
    fn set_duty_percent_converts_to_raw_pwm_scale_and_rejects_out_of_range() {
        let cpu = channel("cpu", 1);
        let (sysfs, controller) = setup(&[&cpu]);

        for (duty, raw) in [(0, "0"), (50, "128"), (100, "255")] {
            controller.set_duty_percent(&cpu, duty).unwrap();
            assert_eq!(sysfs.get(&cpu.pwm_path()), raw);
        }

        assert!(controller.set_duty_percent(&cpu, 101).is_err());
    }

    #[test]
    fn release_restores_the_automatic_mode_the_channel_was_originally_in() {
        for original in ["2", "3", "5"] {
            let cpu = channel("cpu", 1);
            let (sysfs, controller) = setup(&[&cpu]);
            sysfs.set(&cpu.enable_path(), original);

            controller.take_manual_control(&cpu).unwrap();
            controller.take_manual_control(&cpu).unwrap(); // re-asserted every poll; must not overwrite the remembered original with "1"
            controller.release_to_auto(&cpu).unwrap();

            assert_eq!(sysfs.get(&cpu.enable_path()), original);
        }
    }

    #[test]
    fn release_never_restores_a_non_automatic_original_mode() {
        // "1": left on manual by a previous instance that died without releasing.
        for original in ["0", "1", "garbage"] {
            let cpu = channel("cpu", 1);
            let (sysfs, controller) = setup(&[&cpu]);
            sysfs.set(&cpu.enable_path(), original);

            controller.take_manual_control(&cpu).unwrap();
            controller.release_to_auto(&cpu).unwrap();

            assert_eq!(sysfs.get(&cpu.enable_path()), "5");
        }
    }

    #[test]
    fn read_status_reports_duty_rpm_and_mode() {
        let cpu = channel("cpu", 1);
        let (sysfs, controller) = setup(&[&cpu]);
        sysfs.set(&cpu.pwm_path(), "51"); // ~20%
        sysfs.set(&cpu.tach_path(), "0");
        sysfs.set(&cpu.enable_path(), "1");

        let status = controller.read_status(&cpu);

        assert_eq!((status.duty_percent, status.rpm, status.mode), (20, Some(0), Some(PwmMode::Manual)));
    }

    #[test]
    fn read_status_reports_unreadable_mode_and_tach_as_none_rather_than_guessing() {
        let cpu = channel("cpu", 1);
        let (sysfs, controller) = setup(&[&cpu]);
        sysfs.remove(&cpu.enable_path());

        let status = controller.read_status(&cpu);

        assert_eq!((status.mode, status.rpm), (None, None));
    }

    // ---- stall detector ----

    fn status(id: &str, duty: u8, rpm: Option<u32>, mode: PwmMode) -> FanStatus {
        FanStatus { id: id.to_owned(), duty_percent: duty, rpm, mode: Some(mode), stalled: false }
    }

    #[test]
    fn flags_stall_only_after_enough_consecutive_zero_rpm_polls() {
        let mut detector = StallDetector::new(3);
        let zero = status("cpu", 40, Some(0), PwmMode::Manual);

        assert!(!detector.apply(zero.clone()).stalled);
        assert!(!detector.apply(zero.clone()).stalled);
        assert!(detector.apply(zero.clone()).stalled);
        assert!(detector.apply(zero).stalled);
    }

    #[test]
    fn a_single_spinning_poll_resets_the_count_and_channels_are_independent() {
        let mut detector = StallDetector::new(2);

        detector.apply(status("a", 40, Some(0), PwmMode::Manual));
        assert!(!detector.apply(status("b", 40, Some(0), PwmMode::Manual)).stalled);
        assert!(!detector.apply(status("a", 40, Some(800), PwmMode::Manual)).stalled);
        assert!(!detector.apply(status("a", 40, Some(0), PwmMode::Manual)).stalled);
        assert!(detector.apply(status("b", 40, Some(0), PwmMode::Manual)).stalled);
    }

    #[test]
    fn never_flags_when_zero_rpm_is_not_evidence_of_a_fault() {
        let mut detector = StallDetector::new(1);

        assert!(!detector.apply(status("cpu", 40, None, PwmMode::Manual)).stalled); // no tach: unknown
        assert!(!detector.apply(status("cpu", 0, Some(0), PwmMode::Manual)).stalled); // commanded off
        assert!(!detector.apply(status("cpu", 40, Some(0), PwmMode::SmartFanIV)).stalled); // BIOS may legitimately stop it
    }

    // ---- safety guard ----

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
