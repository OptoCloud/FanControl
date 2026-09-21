//! The read-only status API: a deliberately tiny HTTP/1.1 server on a unix socket.
//!
//!   GET /status   the latest snapshot as JSON (503 before the first poll)
//!   GET /events   the same snapshots as a Server-Sent Events stream, one per poll
//!
//! SSE rather than WebSocket because the data only flows one way: it is plain HTTP, any
//! number of consumers can subscribe at once, and clients reconnect on their own. Each
//! connection gets its own thread (capped), so a slow consumer only ever delays itself.
//!
//! There is no TCP listener on purpose. This daemon runs as root with raw PWM and ioctl
//! access; a unix socket can't be reached from the network at all, and a containerised
//! consumer gets the socket's directory bind-mounted in instead.
//!
//!   curl --unix-socket /run/fancontrol/fancontrol.sock http://localhost/status

use crate::status::StatusHub;
use std::io::{self, Read, Write};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

/// How long an event stream stays silent before a keepalive comment is sent, which is
/// also what notices that a consumer has gone away.
const KEEPALIVE_INTERVAL: Duration = Duration::from_secs(15);

const MAX_REQUEST_BYTES: usize = 8 * 1024;

/// Serves one connection to completion. Generic over the stream so it is testable
/// without a socket.
pub fn handle_connection(mut stream: impl Read + Write, hub: &StatusHub, stale_after: Duration, shutdown: &AtomicBool) -> io::Result<()> {
    let Some((method, path)) = read_request_line(&mut stream)? else {
        return respond(&mut stream, "400 Bad Request", "text/plain", "bad request\n");
    };

    if method != "GET" {
        return respond(&mut stream, "405 Method Not Allowed", "text/plain", "read-only API\n");
    }

    match path.split('?').next().unwrap_or_default() {
        "/status" => match hub.latest(stale_after) {
            Some((_, json)) => respond(&mut stream, "200 OK", "application/json", &json),
            None => respond(&mut stream, "503 Service Unavailable", "text/plain", "no poll has completed yet\n"),
        },
        "/events" => stream_events(&mut stream, hub, stale_after, shutdown),
        _ => respond(&mut stream, "404 Not Found", "text/plain", "try /status or /events\n"),
    }
}

fn stream_events(stream: &mut impl Write, hub: &StatusHub, stale_after: Duration, shutdown: &AtomicBool) -> io::Result<()> {
    // No Content-Length and no chunking: the body simply runs until the connection closes.
    stream.write_all(b"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n")?;

    let mut generation = 0;
    let mut reported_stale = false;

    // A new subscriber gets the current state immediately rather than waiting out a poll interval.
    if let Some((current, json)) = hub.latest(stale_after) {
        write_event(stream, &json)?;
        generation = current;
    }

    while !shutdown.load(Ordering::Relaxed) {
        match hub.wait_for_newer(generation, KEEPALIVE_INTERVAL) {
            Some((newer, json)) => {
                write_event(stream, &json)?;
                generation = newer;
                reported_stale = false;
            }
            None => {
                // Nothing new. If that is because the loop has hung, say so once: the
                // subscriber would otherwise keep showing its last healthy snapshot.
                match hub.latest(stale_after) {
                    Some((_, json)) if !reported_stale && json.contains(r#""controlLoopHealthy":false"#) => {
                        write_event(stream, &json)?;
                        reported_stale = true;
                    }
                    _ => stream.write_all(b": keepalive\n\n")?,
                }
                stream.flush()?;
            }
        }
    }

    Ok(())
}

fn write_event(stream: &mut impl Write, json: &str) -> io::Result<()> {
    // The JSON is single-line (serde_json's compact form), so one data: field carries it.
    stream.write_all(b"event: status\ndata: ")?;
    stream.write_all(json.as_bytes())?;
    stream.write_all(b"\n\n")?;
    stream.flush()
}

fn respond(stream: &mut impl Write, status: &str, content_type: &str, body: &str) -> io::Result<()> {
    write!(
        stream,
        "HTTP/1.1 {status}\r\nContent-Type: {content_type}\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
        body.len()
    )?;
    stream.flush()
}

/// (method, path) from the request line, once the full header block has arrived. None if
/// the request is malformed or oversized.
fn read_request_line(stream: &mut impl Read) -> io::Result<Option<(String, String)>> {
    let mut request = Vec::new();
    let mut chunk = [0u8; 1024];

    while !request.windows(4).any(|window| window == b"\r\n\r\n") {
        let read = stream.read(&mut chunk)?;
        if read == 0 || request.len() + read > MAX_REQUEST_BYTES {
            return Ok(None);
        }
        request.extend_from_slice(&chunk[..read]);
    }

    let text = String::from_utf8_lossy(&request);
    let mut parts = text.lines().next().unwrap_or_default().split_whitespace();
    Ok(match (parts.next(), parts.next(), parts.next()) {
        (Some(method), Some(path), Some(version)) if version.starts_with("HTTP/1.") => Some((method.to_owned(), path.to_owned())),
        _ => None,
    })
}

#[cfg(unix)]
pub use listener::spawn;

#[cfg(unix)]
mod listener {
    use super::handle_connection;
    use crate::config::ApiConfig;
    use crate::log;
    use crate::status::StatusHub;
    use std::io::{self, Write};
    use std::os::unix::fs::PermissionsExt;
    use std::os::unix::net::{UnixListener, UnixStream};
    use std::path::Path;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
    use std::time::Duration;

    /// Binds the socket and serves it from background threads for the life of the process.
    pub fn spawn(config: &ApiConfig, hub: Arc<StatusHub>, stale_after: Duration, shutdown: &'static AtomicBool) -> io::Result<()> {
        let path = Path::new(&config.socket_path);
        if let Some(directory) = path.parent() {
            std::fs::create_dir_all(directory)?;
        }

        // A socket file left behind by a previous instance would make bind fail.
        match std::fs::remove_file(path) {
            Err(error) if error.kind() != io::ErrorKind::NotFound => return Err(error),
            _ => {}
        }

        let listener = UnixListener::bind(path)?;
        std::fs::set_permissions(path, std::fs::Permissions::from_mode(config.socket_mode_bits().unwrap_or(0o660)))?;
        if config.socket_uid.is_some() || config.socket_gid.is_some() {
            std::os::unix::fs::chown(path, config.socket_uid, config.socket_gid)?;
        }

        log!(Info, "Status API listening on unix socket {} (mode {}).", config.socket_path, config.socket_mode);

        let max_clients = config.max_clients;
        let active = Arc::new(AtomicUsize::new(0));

        std::thread::Builder::new().name("api-accept".to_owned()).spawn(move || {
            for stream in listener.incoming() {
                let Ok(stream) = stream else { continue };

                if active.fetch_add(1, Ordering::SeqCst) >= max_clients {
                    active.fetch_sub(1, Ordering::SeqCst);
                    reject(stream);
                    continue;
                }

                let (hub, client_active) = (Arc::clone(&hub), Arc::clone(&active));
                let spawned = std::thread::Builder::new().name("api-client".to_owned()).spawn(move || {
                    // Bounded, so a consumer that stops reading (or never sends a request)
                    // gets dropped instead of pinning this thread forever.
                    let _ = stream.set_read_timeout(Some(Duration::from_secs(5)));
                    let _ = stream.set_write_timeout(Some(Duration::from_secs(10)));

                    // An error here just means the consumer went away, which is routine.
                    let _ = handle_connection(&stream, &hub, stale_after, shutdown);
                    client_active.fetch_sub(1, Ordering::SeqCst);
                });

                if spawned.is_err() {
                    active.fetch_sub(1, Ordering::SeqCst);
                }
            }
        })?;

        Ok(())
    }

    fn reject(mut stream: UnixStream) {
        let _ = stream.set_write_timeout(Some(Duration::from_secs(1)));
        let _ = stream.write_all(b"HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::status::Snapshot;
    use std::io::Cursor;

    /// Reads a canned request, records the response, and fails writes once `write_budget`
    /// is spent (how a disconnecting consumer looks to the server).
    struct MockStream {
        request: Cursor<Vec<u8>>,
        response: Vec<u8>,
        write_budget: usize,
    }

    impl MockStream {
        fn new(request: &str) -> Self {
            Self { request: Cursor::new(request.as_bytes().to_vec()), response: Vec::new(), write_budget: usize::MAX }
        }

        fn response(&self) -> String {
            String::from_utf8_lossy(&self.response).into_owned()
        }
    }

    impl Read for MockStream {
        fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
            self.request.read(buffer)
        }
    }

    impl Write for MockStream {
        fn write(&mut self, buffer: &[u8]) -> io::Result<usize> {
            if self.response.len() >= self.write_budget {
                return Err(io::Error::new(io::ErrorKind::BrokenPipe, "consumer went away"));
            }
            self.response.extend_from_slice(buffer);
            Ok(buffer.len())
        }

        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }

    const FRESH: Duration = Duration::from_secs(3600);

    fn hub_with_snapshot() -> StatusHub {
        let hub = StatusHub::new();
        hub.publish(Snapshot {
            timestamp_utc: "t".to_owned(),
            sensors: vec![],
            fans: vec![],
            drive_health: vec![],
            control_loop_healthy: true,
        });
        hub
    }

    fn get(path: &str, hub: &StatusHub) -> String {
        let mut stream = MockStream::new(&format!("GET {path} HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        handle_connection(&mut stream, hub, FRESH, &AtomicBool::new(false)).unwrap();
        stream.response()
    }

    #[test]
    fn status_returns_the_latest_snapshot_as_json() {
        let response = get("/status", &hub_with_snapshot());

        assert!(response.starts_with("HTTP/1.1 200 OK\r\n"));
        assert!(response.contains("Content-Type: application/json\r\n"));
        assert!(response.ends_with(r#""controlLoopHealthy":true}"#));
    }

    #[test]
    fn status_ignores_a_query_string() {
        assert!(get("/status?pretty=1", &hub_with_snapshot()).starts_with("HTTP/1.1 200 OK"));
    }

    #[test]
    fn status_is_503_before_the_first_poll() {
        assert!(get("/status", &StatusHub::new()).starts_with("HTTP/1.1 503 "));
    }

    #[test]
    fn unknown_paths_are_404_and_writes_are_405() {
        let hub = hub_with_snapshot();
        assert!(get("/nope", &hub).starts_with("HTTP/1.1 404 "));

        let mut stream = MockStream::new("POST /status HTTP/1.1\r\n\r\n");
        handle_connection(&mut stream, &hub, FRESH, &AtomicBool::new(false)).unwrap();
        assert!(stream.response().starts_with("HTTP/1.1 405 "));
    }

    #[test]
    fn malformed_and_oversized_requests_are_400() {
        let hub = hub_with_snapshot();

        for request in ["garbage\r\n\r\n".to_owned(), format!("GET /{} HTTP/1.1\r\n\r\n", "a".repeat(MAX_REQUEST_BYTES))] {
            let mut stream = MockStream::new(&request);
            handle_connection(&mut stream, &hub, FRESH, &AtomicBool::new(false)).unwrap();
            assert!(stream.response().starts_with("HTTP/1.1 400 "));
        }
    }

    #[test]
    fn events_sends_the_current_snapshot_immediately_and_ends_when_the_consumer_goes_away() {
        let hub = hub_with_snapshot();
        let mut stream = MockStream::new("GET /events HTTP/1.1\r\n\r\n");
        stream.write_budget = 150; // enough for the headers and the first event, not for a second

        let publisher = std::thread::scope(|scope| {
            scope.spawn(|| {
                std::thread::sleep(Duration::from_millis(50));
                hub.publish(Snapshot {
                    timestamp_utc: "t2".to_owned(),
                    sensors: vec![],
                    fans: vec![],
                    drive_health: vec![],
                    control_loop_healthy: true,
                });
            });
            handle_connection(&mut stream, &hub, FRESH, &AtomicBool::new(false))
        });

        assert!(publisher.is_err());
        let response = stream.response();
        assert!(response.contains("Content-Type: text/event-stream\r\n"));
        assert!(response.contains("event: status\ndata: {\"timestampUtc\":\"t\","));
    }

    #[test]
    fn events_stops_on_shutdown() {
        let mut stream = MockStream::new("GET /events HTTP/1.1\r\n\r\n");

        handle_connection(&mut stream, &hub_with_snapshot(), FRESH, &AtomicBool::new(true)).unwrap();

        assert_eq!(stream.response().matches("event: status").count(), 1);
    }
}
