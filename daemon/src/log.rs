//! Minimal logging to stderr. Under systemd each line carries an sd-daemon "<N>" priority
//! prefix, which journald strips and turns into the entry's real priority (so
//! `journalctl -p warning` works); run by hand, lines get a readable tag instead.

use std::collections::HashSet;
use std::sync::OnceLock;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Level {
    Critical = 2,
    Error = 3,
    Warning = 4,
    Info = 6,
    Debug = 7,
}

pub fn write(level: Level, message: std::fmt::Arguments) {
    static UNDER_JOURNALD: OnceLock<bool> = OnceLock::new();
    let under_journald = *UNDER_JOURNALD.get_or_init(|| std::env::var_os("JOURNAL_STREAM").is_some());

    if under_journald {
        // A newline would start a new, unprefixed journal entry.
        eprintln!("<{}>{}", level as u8, message.to_string().replace('\n', " | "));
    } else if level != Level::Debug {
        eprintln!("[{level:?}] {message}");
    }
}

#[macro_export]
macro_rules! log {
    ($level:ident, $($arg:tt)*) => {
        $crate::log::write($crate::log::Level::$level, format_args!($($arg)*))
    };
}

/// Remembers which named conditions (a channel failing, a fan stalled, ...) are currently
/// active, so one that persists is logged when it starts and when it clears rather than on
/// every 2-second poll for as long as it lasts.
#[derive(Default)]
pub struct ConditionTracker {
    active: HashSet<String>,
}

impl ConditionTracker {
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
        let mut conditions = ConditionTracker::default();

        assert!(!conditions.changed("stalled:cpu", false));
        assert!(conditions.changed("stalled:cpu", true));
        assert!(!conditions.changed("stalled:cpu", true));
        assert!(conditions.is_active("stalled:cpu"));
        assert!(conditions.changed("stalled:cpu", false));
        assert!(!conditions.is_active("stalled:cpu"));
    }
}
