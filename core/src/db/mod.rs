//! Postgres for vigil-core: connection, schema, and every write. vigild keeps no history at
//! all, so this database is the only place any of it lives. vigil-core owns the schema;
//! vigil-web only reads it, as a role that holds SELECT and nothing else.
//!
//! Every statement lives in `core/sql/` and is embedded with `include_str!` (STYLE.md §3.2):
//! SQL in a .sql file can be read, syntax-highlighted and run by hand against a real database,
//! none of which is true of SQL quoted inside Rust.
//!
//! What happens to the samples afterwards — rollups, compression, retention — is TimescaleDB's
//! job; see `timescale.rs` and `core/sql/aggregates/`.
//!
//! Timestamps cross the boundary as RFC 3339 text (`$n::text::timestamptz` going in, epoch
//! milliseconds coming out), which keeps date handling in one place: vigil-protocol.

mod drives;
mod events;
mod samples;

pub use drives::{load_drives, save_drive};
pub use events::insert_event;
pub use samples::{insert_samples, insert_ups_sample, touch_inventory};

use postgres::{Client, NoTls};
use std::str::FromStr;
use std::time::Duration;
use vigil_protocol::limits;

/// A hung database must not freeze vigil-core's one state thread for long.
const CONNECT_TIMEOUT_SECONDS: u64 = 10;

const SCHEMA: &str = include_str!("../../sql/schema.sql");

pub type Result<T> = std::result::Result<T, postgres::Error>;

/// The error with its causes. `postgres::Error`'s own text is only the kind ("db error",
/// "invalid configuration"); what the server or the parser actually said is in its source.
pub fn describe(error: &postgres::Error) -> String {
    let mut text = error.to_string();
    let mut source = std::error::Error::source(error);
    while let Some(cause) = source {
        text.push_str(": ");
        text.push_str(&cause.to_string());
        source = cause.source();
    }
    text
}

pub fn connect(url: &str) -> std::result::Result<Client, String> {
    let mut config = postgres::Config::from_str(url).map_err(|error| format!("DATABASE_URL: {error}"))?;
    config
        .connect_timeout(Duration::from_secs(CONNECT_TIMEOUT_SECONDS))
        .options(&format!("-c statement_timeout={}", limits::DATABASE_STATEMENT_TIMEOUT_MILLIS));
    config.connect(NoTls).map_err(|error| describe(&error))
}

/// The base tables, then TimescaleDB's hypertables, aggregates and policies over them.
pub fn migrate(client: &mut Client, raw_retention: Duration) -> std::result::Result<(), String> {
    client.batch_execute(SCHEMA).map_err(|error| format!("creating tables: {}", describe(&error)))?;
    crate::timescale::setup(client, raw_retention)
}
