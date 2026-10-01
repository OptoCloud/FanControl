//! systemd readiness and watchdog notifications, via the sd-notify crate (ADR-015).
//!
//! The watchdog covers the one failure nothing in-process can: the daemon freezing as a whole
//! (the in-process deadman is a thread, so it freezes too). If the control loop stops sending
//! WATCHDOG=1, systemd kills the process, ExecStopPost releases the fans, and Restart= brings
//! the daemon back.
//!
//! Every call is best-effort and silent. A lost notification must never take fan control down
//! with it, and not being started by systemd at all (no $NOTIFY_SOCKET, which the crate
//! detects) is the normal case when running by hand.

pub use platform::{ready, stopping, watchdog};

#[cfg(target_os = "linux")]
mod platform {
    use sd_notify::NotifyState;

    /// Tells systemd startup is complete: channels are taken and the loop is about to run.
    pub fn ready() {
        let _ = sd_notify::notify(&[NotifyState::Ready]);
    }

    /// Proves the control loop is still completing polls. Call once per poll.
    pub fn watchdog() {
        let _ = sd_notify::notify(&[NotifyState::Watchdog]);
    }

    pub fn stopping() {
        let _ = sd_notify::notify(&[NotifyState::Stopping]);
    }
}

/// The daemon is unit-tested on Windows, where there is no systemd to notify and the crate is
/// not even a dependency (see Cargo.toml's target table).
#[cfg(not(target_os = "linux"))]
mod platform {
    pub fn ready() {}
    pub fn watchdog() {}
    pub fn stopping() {}
}
