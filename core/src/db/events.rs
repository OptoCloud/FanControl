//! The event log: one row per condition raised or cleared.

use super::Result;
use postgres::Client;
use vigil_protocol::{EventRecord, Severity, rfc3339_from_millis};

const INSERT_EVENT: &str = include_str!("../../sql/insert_event.sql");

pub fn insert_event(client: &mut Client, severity: Severity, kind: &str, message: &str) -> Result<EventRecord> {
    let row = client.query_one(INSERT_EVENT, &[&severity_name(severity), &kind, &message])?;
    Ok(EventRecord { id: row.get(0), ts: rfc3339_from_millis(row.get(1)), severity, kind: kind.to_owned(), message: message.to_owned() })
}

/// Spelled out rather than derived, for the same reason as a sensor's category: this value is
/// stored and queried, so a rename in Rust must not split the event log in two.
fn severity_name(severity: Severity) -> &'static str {
    match severity {
        Severity::Info => "info",
        Severity::Warning => "warning",
        Severity::Critical => "critical",
    }
}
