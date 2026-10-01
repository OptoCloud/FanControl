//! Values that both sides of a process boundary have to agree on.
//!
//! Each of these used to be declared twice, once per language, with the relationship between
//! the copies living in a comment. vigil-core kept its stream alive every 20 seconds and
//! vigil-web gave up after 40, and nothing but prose said those two numbers were related.
//!
//! They are declared here and mirrored into `protocol/contract.json` by the same test that
//! pins the wire types, so `web/src/lib/limits.ts` reads them rather than repeating them.

/// How long an event stream may stay silent before it sends a keepalive comment.
///
/// It is also how a dead consumer is noticed: the write fails. Any reader's silence timeout
/// must be more than twice this (see [`STREAM_SILENCE_TIMEOUT_SECONDS`]), so that losing a
/// single keepalive to a slow hop does not look like a disconnection.
pub const STREAM_KEEPALIVE_SECONDS: u64 = 20;

/// How long a reader waits without a single byte before it treats the stream as dead.
///
/// Deliberately derived rather than written out: the invariant is the ratio, not the number.
pub const STREAM_SILENCE_TIMEOUT_SECONDS: u64 = STREAM_KEEPALIVE_SECONDS * 2 + STREAM_KEEPALIVE_SECONDS / 2;

/// vigil-core's live stream, on loopback. vigil-web's default `CORE_URL` points here.
pub const CORE_DEFAULT_PORT: u16 = 3001;

/// The database both of them fall back to when `DATABASE_URL` is unset, which is only ever
/// the case in development.
pub const DATABASE_URL_DEFAULT: &str = "postgres://vigil@localhost/vigil";

/// How long a query may run. vigil-core sets it on its connection and vigil-web on its pool:
/// an unbounded query against the shared Postgres is a denial of service against the host's
/// own monitoring (docs/SECURITY.md §6).
pub const DATABASE_STATEMENT_TIMEOUT_MILLIS: u64 = 15_000;

/// Checked when the crate compiles, not when a test runs: getting this ratio wrong is a
/// change that should not build at all.
const _: () = assert!(
    STREAM_SILENCE_TIMEOUT_SECONDS > STREAM_KEEPALIVE_SECONDS * 2,
    "a silence timeout at or below twice the keepalive interval turns one lost keepalive into a reconnect"
);
