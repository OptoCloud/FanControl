//! The one connection vigil-core keeps to vigild: its `/events` stream of snapshots, over the
//! daemon's unix socket. However many consumers vigil-core has, vigild only ever sees this one.
//! It runs on its own thread and reconnects forever, reporting each change of reachability.

use crate::config::DaemonTarget;
use std::io::{self, Read, Write};
use std::net::{TcpStream, ToSocketAddrs};
use std::time::Duration;
use tracing::warn;
use vigil_protocol::Snapshot;
use vigil_protocol::sse;

pub enum DaemonEvent {
    Snapshot(Box<Snapshot>),
    Connected,
    Disconnected(String),
}

// vigild sends a keepalive comment after 15s of silence, so going this long without a single
// byte means the connection is dead even if the socket hasn't noticed.
const SILENCE_TIMEOUT: Duration = Duration::from_secs(40);
const MIN_RETRY: Duration = Duration::from_secs(1);
const MAX_RETRY: Duration = Duration::from_secs(30);

trait Stream: Read + Write + Send {}
impl<T: Read + Write + Send> Stream for T {}

/// Streams from vigild until the process ends, handing every event to `on_event`.
pub fn run(target: DaemonTarget, on_event: impl Fn(DaemonEvent)) {
    let mut retry = MIN_RETRY;
    loop {
        let mut connected = false;
        let reason = match stream_once(&target, &mut |event| {
            if matches!(event, DaemonEvent::Connected) {
                connected = true;
            }
            on_event(event);
        }) {
            Ok(()) => "vigild closed the stream".to_owned(),
            Err(error) if error.kind() == io::ErrorKind::WouldBlock || error.kind() == io::ErrorKind::TimedOut => {
                format!("no data from vigild for {}s", SILENCE_TIMEOUT.as_secs())
            }
            Err(error) => error.to_string(),
        };

        on_event(DaemonEvent::Disconnected(reason));
        if connected {
            retry = MIN_RETRY;
        }
        std::thread::sleep(retry);
        retry = (retry * 2).min(MAX_RETRY);
    }
}

fn stream_once(target: &DaemonTarget, on_event: &mut impl FnMut(DaemonEvent)) -> io::Result<()> {
    let mut stream = connect(target)?;
    stream.write_all(b"GET /events HTTP/1.1\r\nHost: vigild\r\nAccept: text/event-stream\r\nConnection: close\r\n\r\n")?;

    let mut buffer = Vec::new();
    let mut chunk = [0u8; 16 * 1024];
    let body_start = loop {
        let read = stream.read(&mut chunk)?;
        if read == 0 {
            return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "vigild closed the connection before answering"));
        }
        buffer.extend_from_slice(&chunk[..read]);
        if let Some(end) = buffer.windows(4).position(|window| window == b"\r\n\r\n") {
            break end + 4;
        }
        if buffer.len() > 16 * 1024 {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "oversized response headers"));
        }
    };

    let status_line = String::from_utf8_lossy(&buffer[..body_start]).lines().next().unwrap_or_default().to_owned();
    let status = status_line.split_whitespace().nth(1).unwrap_or_default();
    if status != "200" {
        // 503 is vigild's answer when its max_clients cap is reached.
        return Err(io::Error::other(format!("vigild answered HTTP {status}")));
    }

    on_event(DaemonEvent::Connected);
    let mut parser = sse::Parser::default();
    let mut handle = |bytes: &[u8]| {
        for event in parser.push(bytes) {
            if event.event != "status" {
                continue;
            }
            match serde_json::from_str::<Snapshot>(&event.data) {
                Ok(snapshot) => on_event(DaemonEvent::Snapshot(Box::new(snapshot))),
                Err(error) => warn!("discarding a snapshot that could not be parsed: {error}"),
            }
        }
    };
    handle(&buffer[body_start..]);

    loop {
        let read = stream.read(&mut chunk)?;
        if read == 0 {
            return Ok(());
        }
        handle(&chunk[..read]);
    }
}

fn connect(target: &DaemonTarget) -> io::Result<Box<dyn Stream>> {
    match target {
        DaemonTarget::Tcp(address) => {
            let address = address.to_socket_addrs()?.next().ok_or_else(|| io::Error::other(format!("cannot resolve {address}")))?;
            let stream = TcpStream::connect_timeout(&address, Duration::from_secs(10))?;
            stream.set_read_timeout(Some(SILENCE_TIMEOUT))?;
            Ok(Box::new(stream))
        }
        DaemonTarget::Socket(path) => connect_socket(path),
    }
}

#[cfg(unix)]
fn connect_socket(path: &str) -> io::Result<Box<dyn Stream>> {
    let stream = std::os::unix::net::UnixStream::connect(path)?;
    stream.set_read_timeout(Some(SILENCE_TIMEOUT))?;
    Ok(Box::new(stream))
}

#[cfg(not(unix))]
fn connect_socket(path: &str) -> io::Result<Box<dyn Stream>> {
    Err(io::Error::other(format!("{path}: unix sockets need Linux; use VIGILD_URL with the mock daemon here")))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::TcpListener;
    use std::sync::mpsc;

    const SNAPSHOT: &str = r#"{"timestampUtc":"t","sensors":[],"fans":[],"driveHealth":[],"controlLoopHealthy":true}"#;

    /// A vigild stand-in that answers one connection with `response`, then holds it open briefly.
    fn fake_vigild(response: String) -> String {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap().to_string();
        std::thread::spawn(move || {
            if let Ok((mut stream, _)) = listener.accept() {
                let mut request = [0u8; 1024];
                let _ = stream.read(&mut request);
                let _ = stream.write_all(response.as_bytes());
                std::thread::sleep(Duration::from_millis(100));
            }
        });
        address
    }

    fn collect(address: String, count: usize) -> Vec<String> {
        let (sender, receiver) = mpsc::channel();
        std::thread::spawn(move || {
            run(DaemonTarget::Tcp(address), move |event| {
                let _ = sender.send(match event {
                    DaemonEvent::Snapshot(snapshot) => format!("snapshot {}", snapshot.control_loop_healthy),
                    DaemonEvent::Connected => "connected".to_owned(),
                    DaemonEvent::Disconnected(reason) => format!("disconnected: {reason}"),
                });
            })
        });
        receiver.iter().take(count).collect()
    }

    #[test]
    fn streams_snapshots_and_reports_the_stream_ending() {
        let body = format!(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n\r\n: keepalive\n\nevent: status\ndata: {SNAPSHOT}\n\nevent: status\ndata: {SNAPSHOT}\n\n"
        );
        let events = collect(fake_vigild(body), 4);

        assert_eq!(events[..3], ["connected", "snapshot true", "snapshot true"]);
        assert_eq!(events[3], "disconnected: vigild closed the stream");
    }

    #[test]
    fn a_refusal_is_a_disconnect_with_the_status() {
        let events = collect(fake_vigild("HTTP/1.1 503 Service Unavailable\r\n\r\n".to_owned()), 1);
        assert_eq!(events[0], "disconnected: vigild answered HTTP 503");
    }
}
