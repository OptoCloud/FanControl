//! Incremental Server-Sent Events parser.
//!
//! Only the reading half is ours. axum frames the events vigil *sends* (ADR-015), but nothing
//! maintained by a stable org exists for the client side — `eventsource-client` is small and
//! this one has to read vigild's stream over a unix socket — so this parser stays, with the
//! nine tests that cover reads split anywhere, even mid-character.
//!
//! SSE rather than WebSocket because the data only flows one way (ADR-001).

#[derive(Debug, Clone, PartialEq)]
pub struct Event {
    pub event: String,
    pub data: String,
}

#[derive(Default)]
pub struct Parser {
    buffer: Vec<u8>,
    event: String,
    data: Vec<String>,
}

impl Parser {
    /// Feeds one read; returns every event it completed.
    pub fn push(&mut self, bytes: &[u8]) -> Vec<Event> {
        self.buffer.extend_from_slice(bytes);
        let mut completed = Vec::new();

        while let Some(newline) = self.buffer.iter().position(|&b| b == b'\n') {
            let raw: Vec<u8> = self.buffer.drain(..=newline).collect();
            let line = String::from_utf8_lossy(&raw[..raw.len() - 1]);
            let line = line.strip_suffix('\r').unwrap_or(&line);

            if line.is_empty() {
                // A blank line dispatches whatever has accumulated.
                if !self.data.is_empty() {
                    let event = if self.event.is_empty() { "message".to_owned() } else { std::mem::take(&mut self.event) };
                    completed.push(Event { event, data: self.data.join("\n") });
                }
                self.event.clear();
                self.data.clear();
            } else if line.starts_with(':') {
                // A comment, which is how keepalives are sent.
            } else {
                let (field, value) = match line.find(':') {
                    Some(colon) => (&line[..colon], line[colon + 1..].strip_prefix(' ').unwrap_or(&line[colon + 1..])),
                    None => (line, ""),
                };
                match field {
                    "event" => self.event = value.to_owned(),
                    "data" => self.data.push(value.to_owned()),
                    _ => {}
                }
            }
        }

        completed
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn event(event: &str, data: &str) -> Event {
        Event { event: event.to_owned(), data: data.to_owned() }
    }

    #[test]
    fn parses_a_complete_event() {
        assert_eq!(Parser::default().push(b"event: status\ndata: {\"a\":1}\n\n"), vec![event("status", "{\"a\":1}")]);
    }

    #[test]
    fn reassembles_an_event_split_anywhere_even_inside_a_character() {
        let bytes = "event: status\ndata: 38°C\n\n".as_bytes();
        for split in 1..bytes.len() {
            let mut parser = Parser::default();
            let mut events = parser.push(&bytes[..split]);
            events.extend(parser.push(&bytes[split..]));
            assert_eq!(events, vec![event("status", "38°C")], "split at {split}");
        }
    }

    #[test]
    fn several_events_in_one_read_and_the_unfinished_tail_kept() {
        let mut parser = Parser::default();
        let events = parser.push(b"data: 1\n\ndata: 2\n\ndata: 3");
        assert_eq!(events, vec![event("message", "1"), event("message", "2")]);
        assert_eq!(parser.push(b"\n\n"), vec![event("message", "3")]);
    }

    #[test]
    fn ignores_keepalives_and_does_not_leak_an_event_name_into_the_next_event() {
        let mut parser = Parser::default();
        assert_eq!(parser.push(b": keepalive\n\nevent: status\n\ndata: x\n\n"), vec![event("message", "x")]);
    }

    #[test]
    fn handles_crlf_and_multi_line_data() {
        assert_eq!(Parser::default().push(b"data: a\r\ndata: b\r\n\r\n"), vec![event("message", "a\nb")]);
    }
}
