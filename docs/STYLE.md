# Style guide

Every rule here names an **Enforcer**: a tool, a test, or a line in the review checklist.
A rule with nothing checking it is deleted rather than left to rot. `.gitattributes` pointed
at a `dashboard/` directory for five commits after it was renamed, which is what an unchecked
rule looks like.

Run `scripts/check.sh` before every commit. CI runs the same script and nothing else.

## 1. Shared code

### 1.1 Logic that crosses a process boundary is written once

Two copies of a protocol diverge silently. They did here: vigild and the Rust vigil-core each
grew their own request parsing, response writing and client cap, and the two `respond`s ended
up with different signatures. Both are now framework code (axum in vigild, ASP.NET Core in
vigil-core), which is the usual answer: the part that would be shared is a library (ADR-015).

What is genuinely ours and needed in two places goes in a module named for what it does. **No
crate, namespace, module or file named `common`, `util`, `utils`, `helpers`, `shared` or
`misc`**: a name that cannot be wrong cannot guide anything, and such a module accretes until
nothing can be moved out of it.

*Enforcer:* review checklist: "does this duplicate something that already exists, here or in
a library?"

### 1.2 A value that crosses a language boundary has one declaration

vigild sends a keepalive every 15s; vigil-core treats its stream as dead after 50s of silence.
That relationship used to live in a comment. Now vigild publishes its keepalive in
`daemon/contract.json`, and a C# test asserts that the silence timeout exceeds twice it, so
changing either side alone fails CI.

Such values are declared once, by the side that owns them, and checked by the other side's
tests, never copied by hand. A value only one program uses is that program's own constant
(`core/Vigil.Core/Configuration/Limits.cs`), not part of a contract.

*Enforcer:* `DaemonContractTests.TheSilenceTimeoutOutlastsTwoOfVigildsKeepalives`.

### 1.3 Wire types are one definition with a contract test

Each process boundary is described by the side that produces it:

- `daemon/src/protocol/` defines vigild's snapshot; `cargo test` writes `daemon/contract.json`.
- `core/Vigil.Core/Protocol/` defines the live stream and the event log; the C# tests write
  `core/contract.json`, and fail once when it was stale so the change cannot go unnoticed.

vigil-core round-trips every sample in `daemon/contract.json` through its own types, and
`web/src/lib/types.contract.test.ts` checks `web/src/lib/types.ts` against both files. Rename
a field on one side and CI fails. Add a field and the contract file must be regenerated in the
same commit.

*Enforcer:* `contract_json_is_current` (Rust), `DaemonContractTests` and `CoreContractTests`
(C#), `types.contract.test.ts` (web), and `scripts/check.sh` failing when
`daemon/contract.json` differs from what is committed.

### 1.4 One name means one thing

`notify.rs` once meant sd_notify in vigild and ntfy push notifications in vigil-core, and
`ConditionTracker` existed twice with different semantics. Both were renamed, not merged
(`NtfyClient`, `ConditionLog` and `ConditionDebouncer`): if two things genuinely differ, their
names must differ.

*Enforcer:* review checklist.

## 2. Modules and files

### 2.1 Soft ceiling: 400 lines per file, 60 per function, 150 per type or `impl` block

Not a hard limit: `mpt3.rs` is one wire format, and splitting it would hurt. The ceiling is a
prompt to ask whether the file is doing two jobs. `daemon/src/config.rs` held shape, defaults
and validation in 633 lines, which was stacking layers rather than elaborating one; it is now
`config/mod.rs` and `config/validate.rs`.

When a file crosses the ceiling, either split it or write one line near the top citing this
section and saying why it stays whole.

*Enforcer:* `scripts/check.sh` lists files over the ceiling that do not cite §2.1 in their
first 40 lines, as a warning, not an error.

### 2.2 A module is a job, not a layer of one

Split along the seam already visible in the file's own structure: `daemon/src/fans/` is the
sysfs controller, stall detection and the safety guard, because those are three jobs with three
failure modes, and vigil-core's alerting is snapshot conditions, UPS conditions, the debouncer
and the drive diff. Do not split into `types` / `logic` / `impl`: that scatters one job across
three files, which is worse than one long file.

*Enforcer:* review checklist.

### 2.3 Platform plumbing is separated from the logic it serves

`mpt3.rs` is the pure wire format, `hba.rs` is the `ioctl` around it; `control.rs` is one poll
with no timing or signals, `main.rs` owns the loop. In vigil-core, `NutProtocol` is the parsing
and `NutClient` the socket, and the runtime reaches the database only through `IRuntimeStore`.
This is what makes both testable with no hardware and no database.

*Enforcer:* review checklist: "can this be tested without the hardware?"

### 2.4 The dependency rule

vigild, vigil-core and the dashboard never import each other. They share a JSON contract, not
code: vigild's types live in `daemon/`, vigil-core's in `core/`, and the browser's mirror in
`web/src/lib/types.ts`. Inside vigil-core, namespaces depend downward as
`docs/ARCHITECTURE.md` draws them.

*Enforcer:* the build. There is one Rust crate and one C# application, so a cross-import has
nowhere to come from; the contract tests (§1.3) hold the boundary instead.

## 3. Protocol bodies are data, never inline literals

### 3.1 No raw wire bytes at a call site

```rust
// No:
stream.write_all(b"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\n\r\n")?;

// Yes:
Sse::new(stream).keep_alive(KeepAlive::new().interval(KEEPALIVE_INTERVAL))
```

The same applies to `: keepalive\n\n` and to `data: ${JSON.stringify(m)}\n\n` built by hand.
One writer, one place, tested once: axum's SSE in vigild, `TypedResults.ServerSentEvents` in
vigil-core. This is why vigil-core's keepalive is a named `keepalive` event rather than an SSE
comment: the framework's formatter writes events only, and a comment would mean hand-writing
frames. Reading SSE is `System.Net.ServerSentEvents.SseParser`, also not ours.

*Enforcer:* review checklist.

### 3.2 SQL lives in `.sql` files

SQL in a `.sql` file gets syntax highlighting, is reviewable by itself, and can be run against
a real database by hand. Every statement vigil-core runs, the schema and the continuous
aggregates included, is in `core/sql/`, embedded in the assembly and loaded by name.

```csharp
private static readonly string InsertEvent = SqlText.Load("insert_event");
```

Short, single-purpose statements may stay inline (`select 1 from pg_extension where extname = $1`).
The line is whether a reader needs to parse SQL out of C# quoting to understand it.

*Enforcer:* review checklist; `DataLayerTests.EveryEmbeddedQueryLoads` fails on a missing file.

### 3.3 SQL values are always bound, never formatted

```csharp
// No: a value read from the database, formatted into SQL text:
await database.ExecuteAsync($"call refresh_continuous_aggregate('{name}', '{oldestText}'::timestamptz, null)");

// Yes:
command.Parameters.Add(new NpgsqlParameter { Value = kind, NpgsqlDbType = NpgsqlDbType.Text });
```

Identifiers cannot be bound. An identifier may be interpolated **only** when it comes from a
compile-time constant or an exhaustive switch over a closed set: `HistoryQueries` derives the
rollup table from a validated `RangeKey`, and `SchemaSetup` takes every table name from its
`AllSeries` array. Never from a request parameter, a config value or a database read. The one
value `SchemaSetup` does interpolate, a `CALL`'s start time, is formatted by us from an integer,
because `CALL` cannot run in the transaction a bound parameter would need; that function is
unit-tested on its own.

Every such site carries a `sql-literal-ok:` comment saying why.

*Enforcer:* `scripts/check.sh` fails on an interpolated SQL string in `core/Vigil.Core/Data/`
with no `sql-literal-ok:` reason within six lines above it.

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

*Enforcer:* review checklist.

### 4.2 A named constant carries the reason, not a restatement

```rust
// Useless: says what the name already says.
/// The keepalive interval.
const KEEPALIVE_INTERVAL: Duration = Duration::from_secs(15);

// Useful: says what breaks if you change it.
/// How long an event stream stays silent before a keepalive comment is sent, which is
/// also what notices that a consumer has gone away.
const KEEPALIVE_INTERVAL: Duration = Duration::from_secs(15);
```

*Enforcer:* review checklist.

### 4.3 Names

`snake_case` in Rust, `PascalCase`/`camelCase` in C#, `camelCase` in TypeScript. Full words:
`request`, not `req`; `temperature`, not `temp`. Established domain abbreviations stay (`pwm`,
`rpm`, `wwn`, `hba`, `ups`, `sse`). Booleans read as assertions (`is_available`,
`IsReady`, `controlLoopHealthy`). Functions that can fail say what `None`/`Err`/`null` means in
their doc comment.

*Enforcer:* `.editorconfig` naming rules for C# constants; review checklist for the rest.

## 5. Comments

Comments say **why**, because the code already says what. The existing codebase is unusually
good at this and it is a standard to hold, not a luxury: `panic = "unwind"` in `Cargo.toml`
explains that an abort would skip `FanSafetyGuard`'s `Drop`; the vigil-core unit explains why
`MemoryDenyWriteExecute` is not set. Both would otherwise look like arbitrary choices and get
"simplified" by someone later.

Required:

- every module or type: a header (`//!` in Rust, `<summary>`/`<remarks>` in C#) saying what it
  owns and what it deliberately does not;
- every non-obvious constant: what breaks if it changes;
- every `unsafe` block: a `// SAFETY:` line stating the invariant that makes it sound;
- every deliberate omission: say it is deliberate, or it reads as an oversight.

Delete comments that restate the code. Never leave a comment describing code that has moved or
been deleted: a stale comment is worse than none. A port names where it came from ("ported from
the Rust core's runtime.rs") rather than a path that no longer exists.

*Enforcer:* review checklist.

## 6. Errors

- A failure that must not take the process down is logged and retried, never `unwrap`ped or
  thrown out of a loop. The fan safety path is the extreme case: a lost systemd notification
  must not stop fan control. In vigil-core, `DatabaseGate` is the pattern: a database failure is
  logged once per distinct error and retried, and the live view carries on.
- `unwrap`/`expect` outside tests needs a comment proving it cannot fire. Prefer
  `let ... else`, which this codebase already uses well. In C#, `!` on a nullable needs the same.
- Error text says what was being attempted, not just what failed:
  `database error while preparing the database: ...`.
- Messages shown to an operator name the operator action: "the timescaledb extension is not
  installed in this database. CT 300's recipe creates it" over "setup failed".
- Never put a secret, a credential or a connection URL in an error or a log line. `DATABASE_URL`
  contains a password, and a private ntfy topic's name is its only secret.

*Enforcer:* `clippy::unwrap_used` and `clippy::expect_used` warn outside tests; nullable
reference types as errors in C#; review checklist.

## 7. Tests

- Pure logic is unit-tested exhaustively, and new logic matches the standard of what is here.
- Test names are sentences about behaviour: `reassembles_an_event_split_anywhere_even_inside_a_character`,
  `AQueryErrorKeepsTheDatabaseButALostConnectionClosesItUntilTheRetry`. They describe the
  guarantee, so a failure name alone tells you what broke.
- Every bug fix lands with the test that would have caught it.
- Tests never need hardware, root or the network. Fake the boundary (the in-memory sysfs, the
  fake upsd, `FakeRuntimeStore`, a Kestrel on loopback) rather than skipping the test. The one
  exception is the database tests, which skip unless `VIGIL_TEST_DATABASE_URL` points at a
  TimescaleDB, because what they check (hypertables, aggregates, `unnest`) a fake cannot.
- Boundary-crossing behaviour gets a contract test rather than a comment (§1.3).

*Enforcer:* review checklist; `scripts/check.sh` runs every suite.

## 8. Rust (vigild)

- `cargo fmt` decides formatting; `rustfmt.toml` is the only word on it.
- Clippy is denied, not warned. Lints live in the root `Cargo.toml`'s `[workspace.lints]`.
- `unsafe_code` is denied. The two sites that need it (`hba.rs`'s `ioctl`, `main.rs`'s signal
  handlers) each carry an `#[allow]` with a reason and a `// SAFETY:` line.
- New dependencies need a line in `docs/DECISIONS.md`. Standing constraint: **no TLS stack** in
  vigild (ADR-003), which `cargo-deny` enforces, because it would need C and end the static
  cross-build from Windows.
- The control loop and the fan-safety machinery are plain threads; the only async code is axum's
  socket API on its own small tokio runtime (ADR-004, ADR-015).

*Enforcer:* `cargo fmt --check`, `cargo clippy`, `cargo deny` in `scripts/check.sh`.

## 9. TypeScript and Svelte

- `strict` TypeScript. No `any`; no non-null `!` without a comment. `unknown` plus a narrowing
  check at every boundary: anything from the network is untrusted and parsed, never asserted.
- Svelte 5 runes (`$state`, `$derived`, `$props`, `$effect`). No legacy stores in new code.
- The dashboard is a static build: no server code in `web/` at all. Everything it needs comes
  from vigil-core's `/api/*`.
- Components take typed props via an explicit `interface Props`. No implicit `any` props.
- A component owns presentation. Data shaping goes in `$lib/`. The rule `+page.svelte` currently
  breaks is that it also owns the live-stream wiring, theme persistence and history fetching.
- Never `innerHTML` / `{@html}` with anything that did not come from a literal in our source.
- Prettier decides formatting, with `prettier-plugin-tailwindcss` for class order.

*Enforcer:* `prettier`, `svelte-check` and `vitest` in `scripts/check.sh`.

## 10. C# (vigil-core)

- `dotnet format` decides formatting; `core/.editorconfig` is the only word on it.
- Warnings are errors, at `AnalysisLevel` `latest-recommended` (`core/Directory.Build.props`
  says why not `latest-all`). Nullable reference types are on.
- Private fields are `_camelCase`; constants are `PascalCase`.
- One owner per piece of state. `VigilRuntime` is the pattern: one loop reads one channel, so
  there are no locks and events are handled in the order they happened. Everything else only
  writes to that channel.
- Database access is Npgsql with bound parameters, SQL from `core/sql/` via `SqlText`. No ORM
  for queries (DECISIONS.md has why).
- Tests run as `dotnet run --project core/Vigil.Core.Tests`, not `dotnet test`: on this SDK
  `dotnet test`'s bridge to xunit.v3 discovers nothing.
- New packages need a line in `docs/DECISIONS.md`, as crates do.

*Enforcer:* `dotnet format --verify-no-changes` and `dotnet build` in `scripts/check.sh`;
review checklist for field naming.

## 11. CSS

From Phase 5, `web/` is Tailwind CSS v4 plus shadcn-svelte.

- **Utilities in markup.** `@apply` is not a way to make a component: a repeated class list
  becomes a component or a `tailwind-variants` variant set.
- **Pure CSS only where a utility genuinely cannot reach**: SVG paint internals, `@keyframes`,
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

*Enforcer:* review checklist.

## 12. Commits

- Subject in the imperative, naming the behaviour, not the file: `vigil-core: finish an
  interrupted rollup fill, and never fill past the source`. The existing log is the standard.
- Body says why, and what would break without it.
- One concern per commit. A rename and a behaviour change are two commits.
- `scripts/check.sh` passes before every commit.

*Enforcer:* review checklist.
