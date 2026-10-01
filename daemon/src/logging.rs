//! How vigild sets up `tracing`.
//!
//! Not a logger: `tracing` and `tracing-journald` are the logger (ADR-015). This is the one
//! piece that is genuinely ours: deciding, at startup, whether we are talking to journald or to
//! a terminal, and configuring the subscriber accordingly.
//!
//! Under systemd, `tracing-journald` sends structured records straight to the journal with a
//! real priority, so `journalctl -p warning` works. Run by hand, lines go to stderr with a
//! timestamp.

use tracing_subscriber::layer::SubscriberExt;
use tracing_subscriber::util::SubscriberInitExt;
use tracing_subscriber::{EnvFilter, fmt};

/// Installs the global subscriber. Call once, first thing in `main`.
///
/// `default_filter` is used when `RUST_LOG` says nothing, e.g. `"info"`.
pub fn init(default_filter: &str) {
    let filter = EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new(default_filter));

    let journald = stderr_is_the_journal().then(tracing_journald::layer).and_then(Result::ok);

    match journald {
        Some(journald) => tracing_subscriber::registry().with(filter).with(journald).init(),
        // No journal: a timestamp in the line is the only way to know when something happened.
        None => tracing_subscriber::registry().with(filter).with(fmt::layer().with_target(false).with_writer(std::io::stderr)).init(),
    }
}

/// Whether this process's stderr really is the journal.
///
/// systemd sets $JOURNAL_STREAM to the `device:inode` of the stream it captured, and the
/// documented test is to compare that against stderr's own device and inode, NOT to check
/// that the variable exists. It is inherited by every child, so a shell started from a unit
/// (or from a terminal under one) has it set while its stderr is an ordinary tty. Treating
/// presence as proof sends the log to the journal of a process that is not being journalled,
/// which looks exactly like logging being broken.
#[cfg(target_os = "linux")]
fn stderr_is_the_journal() -> bool {
    use std::os::unix::fs::MetadataExt;

    let Ok(expected) = std::env::var("JOURNAL_STREAM") else { return false };
    // /proc/self/fd/2 resolves to whatever stderr actually is, so this needs no unsafe fd
    // handling to stat it.
    let Ok(stderr) = std::fs::metadata("/proc/self/fd/2") else { return false };

    expected.trim() == format!("{}:{}", stderr.dev(), stderr.ino())
}

/// Nothing to compare against: there is no journald anywhere but Linux.
#[cfg(not(target_os = "linux"))]
fn stderr_is_the_journal() -> bool {
    false
}
