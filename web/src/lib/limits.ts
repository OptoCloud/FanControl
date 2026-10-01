// The TypeScript half of protocol/src/limits.rs: values both sides of a process boundary have
// to agree on. Each of these used to be declared twice with the relationship between the
// copies living in a comment — vigil-core kept its stream alive every 20s and vigil-web gave
// up after 40, and nothing but prose said those numbers were related.
//
// Declared here as plain constants rather than imported from protocol/contract.json, so
// nothing at runtime depends on a file outside the web root. Drift is caught instead by
// types.contract.test.ts, which checks every value below against the generated contract.

/** How long an event stream may stay silent before it sends a keepalive comment. */
export const STREAM_KEEPALIVE_MS = 20_000;

/**
 * How long to wait without a single byte before treating a stream as dead. More than twice
 * the keepalive interval, so one keepalive lost to a slow hop is not read as a disconnection.
 */
export const STREAM_SILENCE_TIMEOUT_MS = 50_000;

/** vigil-core's live stream, on loopback. */
export const CORE_DEFAULT_PORT = 3001;

/** Only ever used in development; production sets DATABASE_URL. */
export const DATABASE_URL_DEFAULT = 'postgres://vigil@localhost/vigil';

/** No query in vigil-web should outlive a page load (docs/SECURITY.md §6). */
export const DATABASE_STATEMENT_TIMEOUT_MS = 15_000;
