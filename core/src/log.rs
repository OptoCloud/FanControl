//! One line per message to stderr, which OpenRC's supervise-daemon writes to
//! /var/log/vigil-core.log.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Level {
    Error,
    Warning,
    Info,
}

pub fn write(level: Level, message: std::fmt::Arguments) {
    eprintln!("{} [{level:?}] {message}", vigil_protocol::now_rfc3339());
}

#[macro_export]
macro_rules! log {
    ($level:ident, $($arg:tt)*) => {
        $crate::log::write($crate::log::Level::$level, format_args!($($arg)*))
    };
}
