# Style guide

Every rule here names an **Enforcer**: a tool, a test, or a line in the review checklist.
A rule with nothing checking it is deleted rather than left to rot — `.gitattributes` pointed
at a `dashboard/` directory for five commits after it was renamed, which is what an unchecked
rule looks like.

Run `scripts/check.sh` before every commit. CI runs the same script and nothing else.

## 1. Shared code

### 1.1 Logic that crosses a process boundary is written once

Two copies of a protocol diverge silently. They already did here: `daemon/src/server.rs` and
`core/src/server.rs` each grew their own `read_request_line`, `respond`, `MAX_REQUEST_BYTES`
and client cap, and the two `respond`s no longer have the same signature.

Shared code goes in a workspace crate named for what it does. **No crate, module or file named
`common`, `util`, `utils`, `helpers`, `shared` or `misc`** — a name that cannot be wrong cannot
guide anything, and such a module accretes until nothing can be moved out of it.

*Enforcer:* review checklist — "does this duplicate something in `protocol/`, `httpd/` or
`logging/`?"

### 1.2 A value that crosses a language boundary has one declaration

vigil-core keepalives every 20s; vigil-web decides the stream is dead after 40s. The constant
is declared twice in two languages and the relationship between them lives in a comment. The
same goes for port 3001 and the `postgres://vigil@localhost/vigil` fallback.

Such values are declared once and mirrored by generation, never by hand. Where generation is
not yet in place, the second copy must name the first in a comment **and** cite the invariant it
depends on (here: silence timeout > 2 x keepalive interval).

*Enforcer:* the generated-constants check in `scripts/check.sh` (Phase 2).

### 1.3 Wire types are one definition with a contract test

`protocol/src/lib.rs` defines the JSON; `web/src/lib/types.ts` mirrors it. That mirror used to
be held together by a comment. It is now held together by `protocol/contract.json`, which the
Rust tests write and the web tests read.

Rename a field on one side and CI fails. Add a field and the contract file must be regenerated
in the same commit.

*Enforcer:* `protocol` test `contract_json_is_current` + `web` test `contract.test.ts`.

### 1.4 One name means one thing

`notify.rs` currently means sd_notify in `daemon/` and ntfy push notifications in `core/`.
`ConditionTracker` exists twice with different semantics. Both are renames, not merges: if two
things genuinely differ, their names must differ.

*Enforcer:* review checklist.

## 2. Modules and files

### 2.1 Soft ceiling: 400 lines per file, 60 per function, 150 per `impl` block

Not a hard limit — `protocol/src/lib.rs` is one coherent set of wire types and `mpt3.rs` is one
wire format, and splitting either would hurt. The ceiling is a prompt to ask whether the file
is doing two jobs. `core/src/runtime.rs` holds a single 300-line `impl`; `daemon/src/config.rs`
holds shape, defaults and validation in 633 lines. Both are stacking layers, not elaborating one.

When a file crosses the ceiling, either split it or write one line at the top saying why it
stays whole.

*Enforcer:* `scripts/check.sh` reports files over the ceiling as a warning, not an error.

### 2.2 A module is a job, not a layer of one

Split along the seam that is already visible in the file's own structure: `fans.rs` separates
into the sysfs controller, stall detection and the safety guard because those are three jobs
with three failure modes. Do not split into `types.rs` / `logic.rs` / `impl.rs` — that scatters
one job across three files, which is worse than one long file.

### 2.3 Platform plumbing is separated from the logic it serves

Already the pattern here and it stays: `mpt3.rs` is the pure wire format, `hba.rs` is the
`ioctl` around it; `control.rs` is one poll with no timing or signals, `main.rs` owns the loop.
This is what makes the daemon unit-testable against an in-memory sysfs with no hardware.

*Enforcer:* review checklist — "can this be tested without the hardware?"

### 2.4 The dependency rule

`protocol` depends on nothing of ours. `httpd` and `logging` depend on nothing of ours but
`protocol`. `daemon`, `core` and `web` depend on the shared crates and never on each other.
Dependencies point inward; nothing shared ever imports a binary crate.

*Enforcer:* `cargo tree` check in `scripts/check.sh`.

## 3. Protocol bodies are data, never inline literals

### 3.1 No raw wire bytes at a call site

```rust
// No:
stream.write_all(b"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n")?;

// Yes:
Response::stream(ContentType::EventStream).write_to(stream)?;
```

The same applies to `b": keepalive\n\n"`, to `data: ${JSON.stringify(m)}\n\n` built by hand in
`web/src/routes/api/live/+server.ts`, and to SSE event framing. One writer, one place, tested
once. A hand-written framing is also where a protocol bug hides: it took a 160-line parser with
nine tests to read SSE correctly, and the writers got none.

### 3.2 SQL lives in `.sql` files

~340 lines of SQL are Rust string literals today. SQL in a `.sql` file gets syntax
highlighting, is reviewable by itself, and can be run against a real database by hand. Load it
with `include_str!`.

```rust
const INSERT_SENSOR_SAMPLES: &str = include_str!("../sql/insert_sensor_samples.sql");
```

Short, single-purpose statements may stay inline (`select 1 from pg_extension where extname = $1`).
The line is whether a reader needs to parse SQL out of Rust quoting to understand it.

### 3.3 SQL values are always bound, never formatted

```rust
// No — a value read from the database, formatted into SQL text:
client.batch_execute(&format!("call refresh_continuous_aggregate('{name}', '{oldest}'::timestamptz, null)"))?;

// Yes:
client.execute("call refresh_continuous_aggregate($1, $2::timestamptz, null)", &[&name, &oldest])?;
```

Identifiers cannot be bound. An identifier may be interpolated **only** when it comes from a
compile-time constant or an exhaustive match over a closed set — as `web/src/lib/server/history.ts`
does, deriving the table name from a `RangeKey` that is validated by `isRangeKey` first.
Never from a request parameter, a config value or a database read.

*Enforcer:* `scripts/check.sh` greps for `format!` within SQL-building calls; review checklist.

## 4. Numbers and names

### 4.1 Name the relationship, not just the number

The rule is not "no literals". `width = 96` in a sparkline and `rx="2"` in an SVG are fine.
A number needs a name when **it encodes a relationship another part of the system depends on**:

- a timeout that must exceed another timeout (`WatchdogSec=30` vs. the worst-case poll);
- a retention window a query assumes (raw kept 30 days, which is why the 30d chart reads `*_1h`);
- a protocol constant (`MPI2_IOCSTATUS_MASK`, `attribute 197`);
- anything a second component also has to know.

`daemon/src/mpt3.rs` is the standard to match: every offset named, with the kernel source file
it came from.

### 4.2 A named constant carries the reason, not a restatement

```rust
// Useless — says what the name already says:
/// The keepalive interval.
const KEEPALIVE_INTERVAL: Duration = Duration::from_secs(15);

// Useful — says what breaks if you change it:
/// How long an event stream stays silent before a keepalive comment is sent, which is
/// also what notices that a consumer has gone away.
const KEEPALIVE_INTERVAL: Duration = Duration::from_secs(15);
```

### 4.3 Names

`snake_case` / `camelCase` per language. Full words: `request`, not `req`; `temperature`, not
`temp`. Established domain abbreviations stay (`pwm`, `rpm`, `wwn`, `hba`, `ups`, `sse`). Booleans
read as assertions (`is_available`, `control_loop_healthy`). Functions that can fail say what
`None`/`Err` means in their doc comment.

## 5. Comments

Comments say **why**, because the code already says what. The existing codebase is unusually
good at this and it is a standard to hold, not a luxury: `panic = "unwind"` in `Cargo.toml`
explains that an abort would skip `FanSafetyGuard`'s `Drop`; `ProtectSystem=full` explains why
not `strict`. Both would otherwise look like arbitrary choices and get "simplified" by someone
later.

Required:

- every module: a `//!` header saying what it owns and what it deliberately does not;
- every non-obvious constant: what breaks if it changes;
- every `unsafe` block: a `// SAFETY:` line stating the invariant that makes it sound;
- every deliberate omission: say it is deliberate, or it reads as an oversight.

Delete comments that restate the code. Never leave a comment describing code that has moved —
a stale comment is worse than none, as `core/src/log.rs` claiming OpenRC under a systemd unit
shows.

## 6. Errors

- A failure that must not take the process down is logged and retried, never `unwrap`ped. The
  fan safety path is the extreme case: a lost systemd notification must not stop fan control.
- `unwrap`/`expect` outside tests needs a comment proving it cannot fire. Prefer
  `let … else`, which this codebase already uses well.
- Error text says what was being attempted, not just what failed. `core/src/timescale.rs`'s
  `failed("making sensor_samples a hypertable")` adapter is the pattern.
- Messages shown to a user name the operator action: "the timescaledb extension is not installed
  in this database. CT 300's recipe creates it" over "setup failed".
- Never put a secret, a credential or a connection URL in an error or a log line. `DATABASE_URL`
  contains a password.

*Enforcer:* `clippy::unwrap_used` and `clippy::expect_used` warn outside tests.

## 7. Tests

- Pure logic is unit-tested exhaustively; the codebase's own standard is high (104 tests today)
  and new logic matches it.
- Test names are sentences about behaviour: `reassembles_an_event_split_anywhere_even_inside_a_character`.
  They describe the guarantee, so a failure name alone tells you what broke.
- Every bug fix lands with the test that would have caught it.
- Tests never need hardware, root or the network. Fake the boundary (the in-memory sysfs, the
  fake upsd, `MockStream`) rather than skipping the test.
- Boundary-crossing behaviour gets a contract test rather than a comment (§1.3).

## 8. Rust

- `cargo fmt` decides formatting; `rustfmt.toml` is the only word on it.
- Clippy is denied, not warned. Workspace lints live in the root `Cargo.toml`.
- `unsafe_code` is **forbidden** in every crate except `daemon`, where the two existing sites
  (`hba.rs`'s `ioctl`, `main.rs`'s signal handlers) each carry a `// SAFETY:` line.
- New dependencies need a line in `docs/DECISIONS.md`. Two standing constraints: **no TLS stack**
  (it would need C and end the static cross-build from Windows — ntfy goes through `curl` for
  this reason) and **no async runtime in our own code** (`postgres`'s internal tokio must not
  leak into ours; vigil is plain threads and channels).
- One owner per piece of state. `core/src/runtime.rs`'s single state thread is the pattern: one
  owner means no locks and events arrive in the order they happened.

## 9. TypeScript and Svelte

- `strict` TypeScript. No `any`; no non-null `!` without a comment. `unknown` plus a narrowing
  check at every boundary — anything from the network is untrusted and parsed, never asserted.
- Svelte 5 runes (`$state`, `$derived`, `$props`, `$effect`). No legacy stores in new code.
- Server-only code lives under `$lib/server/`. It must be impossible to import into a component.
- Components take typed props via an explicit `interface Props`. No implicit `any` props.
- A component owns presentation. Data shaping goes in `$lib/`, queries in `$lib/server/`. The
  rule `+page.svelte` currently breaks is that it also owns the live-stream wiring, theme
  persistence and history fetching.
- Never `innerHTML` / `{@html}` with anything that did not come from a literal in our source.
- Prettier decides formatting, with `prettier-plugin-tailwindcss` for class order.

## 10. CSS

From Phase 5, `web/` is Tailwind CSS v4 plus shadcn-svelte.

- **Utilities in markup.** `@apply` is not a way to make a component: a repeated class list
  becomes a component or a `tailwind-variants` variant set.
- **Pure CSS only where a utility genuinely cannot reach** — SVG paint internals, `@keyframes`,
  container queries, `:has()` selectors. Each such block carries a one-line comment saying which
  of those it is. "It was quicker" is not one of them.
- **Colour comes from a token, never a raw hex.** Already the rule in `app.css` and it stays:
  semantic roles in one `@theme` block, with `--series-*` fixed per entity so the CPU line is
  the same blue in every range and every session.
- **One definition of the dark palette.** It is currently written out three times. Resolve the
  theme to a concrete class once, at the document root.
- `$lib/components/ui/**` is vendored by the shadcn CLI: never hand-edited, upgraded through the
  CLI, extended by wrapping.
- Status colour is never also a series colour, and never the only carrier of meaning: it is
  always paired with an icon and a label.

## 11. Commits

- Subject in the imperative, naming the behaviour, not the file: `vigil-core: finish an
  interrupted rollup fill, and never fill past the source`. The existing log is the standard.
- Body says why, and what would break without it.
- One concern per commit. A rename and a behaviour change are two commits.
- `scripts/check.sh` passes before every commit.
