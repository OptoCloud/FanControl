//! Push notifications through ntfy (https://ntfy.sh or self-hosted). Optional, and never
//! allowed to break or stall anything: each send runs curl on its own thread with a hard
//! time limit. curl rather than an HTTP library because HTTPS in Rust needs a TLS stack with
//! C in it, which would end the static build from any machine with no cross toolchain.

use crate::alerts::NewEvent;
use std::process::{Command, Stdio};
use tracing::warn;
use vigil_protocol::Severity;

pub fn send(url: &str, event: &NewEvent) {
    let (title, priority, tags) = match event.severity {
        Severity::Critical => ("vigil: CRITICAL", "urgent", "rotating_light"),
        _ => ("vigil: warning", "default", "warning"),
    };
    let args = [
        "--silent".to_owned(),
        "--show-error".to_owned(),
        "--fail".to_owned(),
        "--max-time".to_owned(),
        "10".to_owned(),
        "-H".to_owned(),
        format!("Title: {title}"),
        "-H".to_owned(),
        format!("Priority: {priority}"),
        "-H".to_owned(),
        format!("Tags: {tags}"),
        "--data-binary".to_owned(),
        event.message.clone(),
        url.to_owned(),
    ];

    std::thread::spawn(move || {
        let result = Command::new("curl").args(&args).stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::piped()).output();
        match result {
            Ok(output) if output.status.success() => {}
            Ok(output) => warn!("could not send a notification: {}", String::from_utf8_lossy(&output.stderr).trim()),
            Err(error) => warn!("could not send a notification (is curl installed?): {error}"),
        }
    });
}
