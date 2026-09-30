//! vigil-core's HTTP side, for vigil-web on the same machine:
//!
//!   GET /live     Server-Sent Events: the current state on connect, then every change
//!                 (snapshots, vigild reachability, drive health, the UPS, new events).
//!   GET /health   200 once running.
//!
//! It listens on loopback only. Actions will arrive here too, which is why nothing but
//! vigil-web beside it may reach it. Each connection gets its own thread (capped).

use crate::runtime::Input;
use std::io::{self, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError, Sender};
use std::time::Duration;

const KEEPALIVE_INTERVAL: Duration = Duration::from_secs(20);
const MAX_CLIENTS: usize = 16;
/// Messages a subscriber may fall behind by before it is dropped.
const SUBSCRIBER_BACKLOG: usize = 256;
const MAX_REQUEST_BYTES: usize = 8 * 1024;

pub fn serve(listener: TcpListener, inputs: Sender<Input>) {
    let clients = Arc::new(AtomicUsize::new(0));
    for stream in listener.incoming() {
        let Ok(mut stream) = stream else { continue };
        if clients.fetch_add(1, Ordering::SeqCst) >= MAX_CLIENTS {
            clients.fetch_sub(1, Ordering::SeqCst);
            let _ = respond(&mut stream, "503 Service Unavailable", "too many clients\n");
            continue;
        }

        let (inputs, clients) = (inputs.clone(), Arc::clone(&clients));
        std::thread::spawn(move || {
            let _ = handle(stream, &inputs);
            clients.fetch_sub(1, Ordering::SeqCst);
        });
    }
}

fn handle(mut stream: TcpStream, inputs: &Sender<Input>) -> io::Result<()> {
    stream.set_read_timeout(Some(Duration::from_secs(10)))?;
    let Some((method, path)) = read_request_line(&mut stream)? else {
        return respond(&mut stream, "400 Bad Request", "bad request\n");
    };
    if method != "GET" {
        return respond(&mut stream, "405 Method Not Allowed", "GET only\n");
    }

    match path.split('?').next().unwrap_or_default() {
        "/health" => respond(&mut stream, "200 OK", "ok\n"),
        "/live" => {
            let (sender, receiver) = mpsc::sync_channel(SUBSCRIBER_BACKLOG);
            if inputs.send(Input::Subscribe(sender)).is_err() {
                return respond(&mut stream, "503 Service Unavailable", "shutting down\n");
            }
            stream
                .write_all(b"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n")?;
            loop {
                match receiver.recv_timeout(KEEPALIVE_INTERVAL) {
                    Ok(json) => {
                        stream.write_all(b"data: ")?;
                        stream.write_all(json.as_bytes())?;
                        stream.write_all(b"\n\n")?;
                    }
                    // The keepalive is also what notices that vigil-web has gone away.
                    Err(RecvTimeoutError::Timeout) => stream.write_all(b": keepalive\n\n")?,
                    Err(RecvTimeoutError::Disconnected) => return Ok(()),
                }
                stream.flush()?;
            }
        }
        _ => respond(&mut stream, "404 Not Found", "try /live or /health\n"),
    }
}

fn respond(stream: &mut impl Write, status: &str, body: &str) -> io::Result<()> {
    write!(stream, "HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}", body.len())?;
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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::sse::SseParser;

    fn start() -> (String, mpsc::Receiver<Input>) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap().to_string();
        let (sender, receiver) = mpsc::channel();
        std::thread::spawn(move || serve(listener, sender));
        (address, receiver)
    }

    fn get(address: &str, path: &str) -> TcpStream {
        let mut stream = TcpStream::connect(address).unwrap();
        write!(stream, "GET {path} HTTP/1.1\r\nHost: core\r\n\r\n").unwrap();
        stream
    }

    #[test]
    fn health_answers_and_unknown_paths_are_404() {
        let (address, _inputs) = start();
        let mut body = String::new();
        get(&address, "/health").read_to_string(&mut body).unwrap();
        assert!(body.starts_with("HTTP/1.1 200 OK") && body.ends_with("ok\n"));

        body.clear();
        get(&address, "/nope").read_to_string(&mut body).unwrap();
        assert!(body.starts_with("HTTP/1.1 404"));
    }

    #[test]
    fn live_relays_what_the_runtime_sends_as_server_sent_events() {
        let (address, inputs) = start();
        let mut stream = get(&address, "/live");

        let Ok(Input::Subscribe(subscriber)) = inputs.recv_timeout(Duration::from_secs(5)) else { panic!("no subscription") };
        subscriber.send(r#"{"type":"daemon","connected":true}"#.into()).unwrap();
        subscriber.send(r#"{"type":"daemon","connected":false}"#.into()).unwrap();
        drop(subscriber);

        let mut body = Vec::new();
        stream.read_to_end(&mut body).unwrap();
        let header_end = body.windows(4).position(|w| w == b"\r\n\r\n").unwrap() + 4;
        assert!(String::from_utf8_lossy(&body[..header_end]).contains("text/event-stream"));

        let events = SseParser::default().push(&body[header_end..]);
        assert_eq!(events.len(), 2);
        assert_eq!(events[1].data, r#"{"type":"daemon","connected":false}"#);
    }
}
