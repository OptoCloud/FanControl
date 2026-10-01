//! Fan headers: what a channel is, and the contract for driving one.
//!
//! The three jobs around that are separate modules, because they have three different failure
//! modes: `controller` writes sysfs and a write can fail, `stall` notices a header that reads
//! 0 RPM while being driven, and `safety` guarantees no channel is ever left stuck on manual
//! PWM however the process ends.

mod controller;
mod safety;
mod stall;

pub use crate::protocol::{FanStatus, PwmMode};
pub use controller::SysfsFanController;
pub use safety::FanSafetyGuard;
pub use stall::StallDetector;

use crate::sysfs;
use std::io;
use std::sync::{Mutex, MutexGuard};

/// One physical fan header, addressed by its sysfs pwmN attribute set. The mapping from
/// pwmN to a silkscreen header is board-specific and NOT guessable: it has to be verified
/// by walking each channel in manual mode and watching which tach responds.
#[derive(Debug, Clone, PartialEq)]
pub struct FanChannel {
    pub id: String,
    /// hwmon chip directory containing this pwmN/fanN pair.
    pub chip_dir: String,
    pub index: u32,
    /// Duty below which this fan stalls; the curve result is never allowed under it.
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

/// A poisoned mutex only means another thread panicked while holding it. None of the data
/// guarded here can be left half-updated in a way that matters, and refusing to proceed
/// would block the release path, which must always run.
pub(super) fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// Helpers the tests of more than one of these modules need: a channel, a controller over a
/// fake sysfs, and a sleep long enough for the deadman thread to act.
#[cfg(test)]
pub(super) mod test_support {
    use super::{FanChannel, SysfsFanController};
    use crate::sysfs::fake::FakeSysFs;
    use std::sync::Arc;
    use std::time::Duration;

    pub(super) fn channel(id: &str, index: u32) -> FanChannel {
        FanChannel { id: id.to_owned(), chip_dir: "/sys/class/hwmon/hwmon3".to_owned(), index, minimum_duty_percent: 20 }
    }

    pub(super) fn setup(channels: &[&FanChannel]) -> (Arc<FakeSysFs>, Arc<SysfsFanController>) {
        let sysfs = Arc::new(FakeSysFs::new());
        for channel in channels {
            sysfs.set(&channel.enable_path(), "5");
            sysfs.set(&channel.pwm_path(), "0");
        }
        let controller = Arc::new(SysfsFanController::new(sysfs.clone()));
        (sysfs, controller)
    }

    pub(super) fn pause(millis: u64) {
        std::thread::sleep(Duration::from_millis(millis));
    }

    // ---- controller ----
}
