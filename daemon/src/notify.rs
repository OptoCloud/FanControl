//! The sd_notify protocol, by hand: one datagram to the socket systemd names in
//! $NOTIFY_SOCKET. Used for Type=notify readiness and for the unit's WatchdogSec.
//!
//! The watchdog covers the one failure nothing in-process can: the daemon freezing as a
//! whole (the in-process deadman is a thread, so it freezes too). If the control loop stops
//! sending WATCHDOG=1, systemd kills the process, ExecStopPost releases the fans, and
//! Restart= brings the daemon back.

/// Tells systemd startup is complete: channels are taken and the loop is about to run.
pub fn ready() {
    send("READY=1");
}

/// Proves the control loop is still completing polls. Call once per poll.
pub fn watchdog() {
    send("WATCHDOG=1");
}

pub fn stopping() {
    send("STOPPING=1");
}

#[cfg(not(target_os = "linux"))]
fn send(_message: &str) {}

/// Silently does nothing when not started by systemd (no $NOTIFY_SOCKET), and never fails
/// the caller: a lost notification must not take fan control down with it.
#[cfg(target_os = "linux")]
fn send(message: &str) {
    use std::os::linux::net::SocketAddrExt;
    use std::os::unix::net::{SocketAddr, UnixDatagram};

    let Some(target) = std::env::var_os("NOTIFY_SOCKET") else { return };
    let target = target.to_string_lossy();

    // A leading '@' means a Linux abstract-namespace socket rather than a filesystem path.
    let address = match target.strip_prefix('@') {
        Some(name) => SocketAddr::from_abstract_name(name),
        None => SocketAddr::from_pathname(&*target),
    };

    if let (Ok(address), Ok(socket)) = (address, UnixDatagram::unbound()) {
        let _ = socket.send_to_addr(message.as_bytes(), &address);
    }
}
