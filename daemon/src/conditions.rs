//! Remembering which conditions are currently active, so one that persists is reported when it
//! starts and when it clears rather than on every 2-second poll for as long as it lasts.
//!
//! This lived beside the old hand-rolled logger and was called `ConditionTracker`, which was
//! also the name of something different in vigil-core: a debouncer that waits for a condition
//! to hold for several polls before raising an event. Two jobs, two names now (STYLE.md §1.4).

use std::collections::HashSet;

#[derive(Default)]
pub struct ConditionLog {
    active: HashSet<String>,
}

impl ConditionLog {
    /// Records the condition's current state. True only if that differs from the last recorded state.
    pub fn changed(&mut self, condition: &str, active: bool) -> bool {
        if active { self.active.insert(condition.to_owned()) } else { self.active.remove(condition) }
    }

    pub fn is_active(&self, condition: &str) -> bool {
        self.active.contains(condition)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reports_only_transitions() {
        let mut conditions = ConditionLog::default();

        assert!(!conditions.changed("stalled:cpu", false));
        assert!(conditions.changed("stalled:cpu", true));
        assert!(!conditions.changed("stalled:cpu", true));
        assert!(conditions.is_active("stalled:cpu"));
        assert!(conditions.changed("stalled:cpu", false));
        assert!(!conditions.is_active("stalled:cpu"));
    }
}
