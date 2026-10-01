//! Stall detection: a header reading 0 RPM for several consecutive polls while being driven.
//!
//! Consecutive, not instantaneous: a fan spinning up from rest, or a tach that misses a
//! reading, must not be reported as stalled.

use super::{FanStatus, PwmMode};
use std::collections::HashMap;

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

#[cfg(test)]
mod tests {
    use super::*;

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
}
