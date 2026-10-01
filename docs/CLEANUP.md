# Cleanup plan

Agreed 2026-10-01. Six phases, each ending green and shippable: `scripts/check.sh` passes and
the tree is deployable. **Review at every phase boundary**; the next phase does not start
until the previous one is read.

Every item names the evidence that produced it, so nothing here is a guess about what needs
fixing. Tick an item only when its enforcer (test, lint or checklist line) is in place too.

---

## Phase 0 — Guides and enforcement (no behaviour change)

- [x] `CLAUDE.md`: build/test commands, hard invariants, pointers out. Kept short enough to read.
- [x] `docs/ARCHITECTURE.md`: module map and the dependency rule (what may import what).
- [x] `docs/STYLE.md`: shared principles, then Rust / TypeScript+Svelte / SQL / CSS sections.
- [x] `docs/SECURITY.md`: trust boundaries, rules per boundary, pre-merge checklist.
- [x] `docs/DECISIONS.md`: the ADR log for the "why" currently inline in README.
- [x] `scripts/check.sh`: one command running fmt, clippy, cargo test, eslint, svelte-check, vitest.
      (`just` was considered and dropped: it is not installed here and the project is also
      developed on Windows, where a bash script works under Git Bash/WSL and `make` does not.)
- [x] `.github/workflows/check.yml`: CI runs `scripts/check.sh`, nothing else, so local and CI cannot diverge.
- [x] `[workspace.lints]` in `Cargo.toml`: clippy denied, `unsafe_code` forbidden outside `daemon`.
- [x] `deny.toml` + `cargo-deny` in CI: licence and advisory gate.
- [x] eslint + prettier for `web/` (neither existed). Two rules needed a justified disable
      rather than a blanket off: the one allowed inline script in `+layout.svelte`, and
      `svelte/prefer-svelte-reactivity` on a Set that is reassigned, never mutated.
      `prettier-plugin-tailwindcss` waits for Phase 5, when there are classes to sort.
- [x] **Protocol contract test**, replacing the "treat a rename here as a change to both" comment
      in `protocol/src/lib.rs:10`: Rust writes `protocol/contract.json`, a vitest checks it
      against `web/src/lib/types.ts`. A one-sided rename now fails CI.
- [x] `.gitattributes` still points at `dashboard/**`, renamed to `web/` in commit ee0c540.
      `scripts/check.sh` now fails on a path pattern naming a directory that does not exist,
      so this specific drift cannot recur.
- [x] README: a `Conventions` section pointing at the four guides, and ADR-008's trust
      boundary stated where the dashboard is described.
- [x] Verified each new enforcer actually fires: a TypeScript-side rename fails
      `svelte-check`, a Rust-side field addition fails the web contract test, and the musl
      cross-build produces `static-pie linked` binaries as the CI job asserts.

## Phase 1 — Security fixes

All nine done; `scripts/check.sh` now enforces each one rather than reporting it as pending.

- [x] `core/src/config.rs` — `CORE_HOST` is validated: a non-loopback bind is refused unless
      `CORE_ALLOW_NON_LOOPBACK=1` is set with it.
- [x] `web/src/lib/server/db.ts` — `statement_timeout` of 15s, matching vigil-core's.
- [x] `web/src/lib/server/db.ts` — connects as `vigil_web`, a SELECT-only role. Grant documented
      in `docs/SECURITY.md` §3.4 and the README, and verified against TimescaleDB 2.30.2: the
      role reads all six tables and all six continuous aggregates, and every write
      (insert/update/delete/create/drop/truncate) is denied.
- [x] `web/src/routes/api/live/+server.ts` — capped at 64 subscribers, 503 past that.
- [x] `web/src/hooks.server.ts` + `vite.config.ts` — CSP in nonce mode plus
      `X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options`, COOP and
      `Permissions-Policy`. The `{@html}` inline script is gone: it moved to `app.html` under
      `%sveltekit.nonce%`, which also removed its eslint exemption.
- [x] `core/src/timescale.rs` — the refresh window is read as epoch milliseconds and formatted
      by us, so no database-read text reaches a statement. `CALL` cannot take a bound parameter
      (it must not run in a transaction block), hence a formatter rather than a bind.
- [x] `daemon/src/process.rs` — child stdout is bounded at 1 MiB; over that the output is
      treated as unusable and the child killed.
- [x] All three systemd units carry the hardening baseline. Three directives are deliberately
      omitted with the reason in the unit, because they break a component:
      `ProtectKernelTunables` would stop vigild writing `/sys` (no fan would ever be driven),
      `PrivateDevices` would hide `/dev/mpt3ctl`, and `MemoryDenyWriteExecute` would stop node
      starting. `systemd-analyze verify` accepts all three files.
- [x] Env file mode/ownership (`0640 root:vigil`) documented in the README and in both
      `.env` templates.

Out of scope by decision: vigil-web stays LAN-open without auth. See `docs/DECISIONS.md` ADR-008.

**Follow-up for the host** (needs the real hardware, so it cannot be done here):

- [ ] Narrow `CapabilityBoundingSet` on `vigild`. It runs as root and needs `CAP_CHOWN` for the
      socket's uid/gid; `smartctl` may want `CAP_SYS_RAWIO` for `SG_IO` on some kernels.
      Establish the minimum set on the target host, then add it to the unit.

## Phase 2 — Established libraries instead of our own (ADR-015)

Originally "extract the shared plumbing into our own crates". Changed on 2026-10-01: sharing a
hand-rolled HTTP server between two binaries is still a hand-rolled HTTP server. The registry
data behind each choice is in ADR-015.

- [x] `axum` + `hyper` + `tokio` for both APIs. Deletes the `httpd/` crate and both forked
      copies of request parsing, response writing and the capped accept loop.
- [x] `axum::response::sse` for both event streams, replacing our framing. The SSE *parser*
      stays for reading vigild's socket — nothing maintained exists for the client side.
- [x] `tracing` + `tracing-subscriber` + `tracing-journald` for logging, replacing the
      hand-rolled logger. vigil-core's copy had three levels and no priority prefix, so
      `journalctl -p err` showed nothing for it.
- [x] `time` for RFC 3339, replacing the hand-written civil-from-days calendar maths in
      `protocol/src/lib.rs`. `protocol/contract.json` pins the wire format, so any drift in
      output fails CI.
- [x] `sd-notify` for the readiness and watchdog datagrams.
- [x] `Intl.RelativeTimeFormat` replaces `relativeTime()`'s hand-written wording. Node's native
      `EventSource` was **rejected**: it is still behind `--experimental-eventsource` in Node 26,
      and vigil-web's stream to vigil-core is long-lived and load-bearing. The parser stays.
- [x] Keep `ConditionLog` (one implementation, renamed): `daemon/src/log.rs` and
      `core/src/alerts.rs` each had a `ConditionTracker` and they were different jobs — a
      transition log and a debouncer. Renamed rather than merged (STYLE.md §1.4).
- [x] `core/src/notify.rs` renamed to `ntfy.rs`: it is an ntfy client, while
      `daemon/src/notify.rs` is sd_notify. One name must not mean two things.
- [x] One declaration for every value crossing a language boundary. Done by phase 6.7, which
      left one such value: vigild's keepalive, which vigil-core's silence timeout must outlast.
      vigild publishes it in `daemon/contract.json` and `DaemonContractTests` checks the
      relationship. The port and the database URL default no longer cross a boundary: the
      web's only server-side reader of them is gone.

## Phase 3 — SQL out of the Rust source

Done. Verified against TimescaleDB 2.30.2 on a fresh volume: 10 tables, 3 compressed
hypertables, 6 continuous aggregates, every retention/refresh/compression policy, and samples
flowing into both raw and the rollups.

- [x] `core/sql/*.sql` and `core/sql/aggregates/*.sql`, loaded with `include_str!`, replacing
      ~340 lines of SQL quoted inside Rust. The continuous aggregate definitions in particular
      are now readable as SQL and runnable by hand.
- [x] `core/src/db.rs` (352 lines) split into `db/{mod,samples,drives,events}.rs` — 60, 145,
      64 and 22 lines.
- [x] Decided while doing it: **raw SQL, not an ORM.** vigil's data layer is TimescaleDB-shaped
      (hypertables, continuous aggregates, `time_bucket`, weighted-average rollups, array
      `unnest` bulk inserts); no Rust ORM models any of it, so an ORM would cover the five
      CRUD statements and add an entity layer for nothing. `sqlx` (compile-time-verified raw
      SQL) is the option worth revisiting if a column-index bug ever bites — `query_file!`
      reads exactly the `.sql` files this phase created. See Phase 6.
- [x] Fixed while verifying: both binaries installed **no tracing subscriber**, so after the
      Phase 2 logger migration they ran completely silently and no test noticed.
      `scripts/check.sh` now fails if a binary does not call `vigil_logging::init`.
- [x] Fixed while verifying: the journald check tested whether `$JOURNAL_STREAM` *exists*.
      systemd documents it as a `device:inode` to compare against stderr, and it is inherited
      by every child, so any shell under a unit looked like the journal. Logs were going to the
      journal of a process that was not being journalled.

## Phase 4 — Split the large Rust modules

vigild's half is done. **core's half is deferred** until Phase 6 is settled: `alerts.rs` and
`runtime.rs` are exactly what a .NET rewrite would replace, so splitting them now may be
throwaway work.

- [x] `daemon/src/config.rs` (633) → `config/mod.rs` (229, the shape and its defaults) and
      `config/validate.rs` (420, the validation).
- [x] `daemon/src/fans.rs` (542) → four modules split by failure mode, not by layer:
      `mod.rs` (98, what a channel is and the controller contract), `controller.rs` (166, the
      only code that writes a header), `stall.rs` (74, 0 RPM while being driven), `safety.rs`
      (263, the `Drop` guard and the deadman). The tests moved with the code they pin, and the
      three helpers two modules share live in a `test_support` module.
- [x] `daemon/src/curve.rs` (447) stays whole and says why: only the first 180 lines are code,
      and it is one job. `scripts/check.sh` now excludes a file that cites STYLE.md §2.1 in its
      first 40 lines, so the ceiling warning lists only files nobody has made the case for.
- [x] `core/src/alerts.rs` (517) and `core/src/runtime.rs` (471): replaced rather than split.
      Their C# ports are split by job from the start: `Alerting/` in four files (6.5) and
      `Runtime/` in six (6.6), none over 400 lines.

## Phase 5 — Tailwind + shadcn-svelte

- [ ] `prettier-plugin-tailwindcss`, for deterministic class order (deferred from Phase 0).
- [ ] `@tailwindcss/vite` + Tailwind v4; `shadcn-svelte` init (`components.json`, `$lib/utils.ts`
      with `cn()`, `tailwind-variants`, `tailwind-merge`, `bits-ui`, `mode-watcher`).
- [ ] `app.css` → `@import "tailwindcss"` + one `@theme` block. Existing roles map onto shadcn
      names so its components inherit the palette; `--series-*`, `--status-*`, `--meter-*` stay.
- [ ] Collapse the dark palette, written out three times verbatim in `web/src/app.css` today:
      resolve `auto` to a concrete `class="dark"` in an inline script in `app.html` (which also
      removes the current theme flash), and let `mode-watcher` own the toggle — deleting the
      hand-rolled `setTheme`/`localStorage` code in `+page.svelte`.
- [ ] shadcn for generic chrome only: Card, Button, Badge, Table, Tooltip, Select, Separator, Switch.
- [ ] `StatusBadge.svelte` severity states → a `tailwind-variants` variant set.
- [ ] Keep bespoke: `LineChart.svelte`, `Sparkline.svelte`, the `StatTile` meter.
- [ ] Split `web/src/routes/+page.svelte` (386) into section components plus a live-state module.
- [ ] Split `LineChart.svelte` (411): geometry and readout logic into `$lib/chart/`, SVG stays.

## Phase 6 — vigil-core becomes one ASP.NET Core app, serving the dashboard

**Decided 2026-10-01: go for ASP.NET Core.** vigil-core is rewritten in C# on `net10.0`, and
vigil-web's server half is folded into it: one process that polls, records, alerts, serves the
API, and serves the SvelteKit build as static files. vigild stays Rust — it runs as root,
drives the fans, and "as little as possible" is a safety property there, not a preference.

Verified before planning: `.NET 10` SDK and ASP.NET Core 10 are installed, and
`TypedResults.ServerSentEvents` over `IAsyncEnumerable<string>` compiles, so the live stream
needs no SSE library. `clang` is **not** installed, so NativeAOT is not available here without
adding it — see 6.7.

### What this is for

ASP.NET Core supplies what core actually needs and axum does not: minimal APIs with
first-class SSE, `IHostedService` for the pollers, `ILogger` with a journald sink, config
binding, DI and health checks. Merging deletes a whole SSE hop — today the chain is
vigild → core → vigil-web (Node) → browser — and with it `web/src/lib/server/core.ts` (the
state mirror, reconnect backoff and silence timer), `web/src/lib/server/sse.ts`, and the
keepalive-versus-silence coupling that `protocol/src/limits.rs` exists to hold together. Node
leaves the deployment entirely.

One real gain the Rust version could not have: **HTTPS is free in .NET**, so ntfy stops going
through `curl`. ADR-003's no-TLS rule was a Rust/musl constraint and does not apply to core
any more; it still binds vigild.

### What it is not for

**Not EF Core as an ORM.** Change tracking is pure overhead on append-only samples, and
`SaveChanges` would turn one snapshot insert into fifteen round trips where `unnest` does it in
one — Npgsql's binary `COPY` is the answer there, and that is Npgsql, not EF Core. EF Core is
used for **migrations only**, with `migrationBuilder.Sql(...)` for the TimescaleDB DDL, which
is the SQL already extracted into `core/sql/` in phase 3. That gets a real migration history,
which the current "re-apply idempotent DDL at every boot" has never had, with no third-party
TimescaleDB package (the best of those has 109k downloads against Npgsql's 506M).

### The security consequence, stated plainly

This **voids [ADR-008](DECISIONS.md) as written**. Phase 1 put the trust boundary *between* the
two processes: vigil-web was LAN-facing, unauthenticated, and connected as a `SELECT`-only
role, while vigil-core held the write credential on loopback only. Merged, the LAN-facing
process owns the schema and holds the write credential.

Two Kestrel endpoints do **not** fix this — they separate routing, not credentials; one process
means one address space. What makes it acceptable is that ADR-008 already accepts that anything
on the LAN can read everything, and a compromise of the web layer was already total. The read
path still uses the `SELECT`-only role via a second `NpgsqlDataSource`, so an injection bug in
a query cannot write; and every query stays parameterised.

**When action endpoints arrive, they go on a loopback-only Kestrel endpoint**, and that is the
point at which authentication stops being optional. ADR-008 is rewritten to say so.

### Steps, each ending green

- [x] **6.1 Scaffold.** `core-net/` on `net10.0`: `Vigil.Core` and `Vigil.Core.Tests`,
      env config binding with validation, `UseSystemd()` for journald logging and sd_notify
      readiness, `/health`, and `dotnet format`/`build`/`test` in `scripts/check.sh` and CI.
      Replaces nothing yet; the Rust core keeps running. 19 tests.
      Settled while doing it, each because the first attempt was wrong:
      `global.json` pins .NET 10 (the box also has an 11.0 RC that would otherwise be picked)
      and carries the `Microsoft.Testing.Platform` runner opt-in that xunit.v3 needs on this
      SDK; `AnalysisLevel` is `latest-recommended` rather than `latest-all`, and
      `EnforceCodeStyleInBuild` is deliberately off, because with `TreatWarningsAsErrors` it
      turns every style suggestion into a build failure — the same split as the Rust half,
      where the build enforces correctness and a separate format check enforces style;
      `check.sh` runs the test runner directly, since `dotnet test`'s bridge to
      Microsoft.Testing.Platform discovers zero tests on this SDK while the runner finds all of
      them. **A bug the smoke test caught:** .NET's configuration binds `CorePort`, not
      `CORE_PORT`, so every documented variable name read as nothing and vigil-core silently
      ignored its configuration. `VigilEnvironment` now maps the published names explicitly,
      and a test asserts every option has one.
- [x] **6.2 The contract.** C# records in `core-net/Vigil.Core/Protocol/`, checked against
      `protocol/contract.json` by a round trip: every sample is deserialised into the C# type and
      serialised straight back, which in one assertion catches a renamed field, a field C# has
      and Rust does not, a field Rust has and C# does not, and a mismatched type. Enum spellings
      and the shared limits are pinned too, and both variant lists are checked for completeness
      so a type added on one side cannot go unnoticed. 39 tests; verified to fail on a
      C#-side rename.
      **A flaw in the phase 0 generator came out here:** `serde_json::Map` is a `BTreeMap`, so
      contract.json listed keys alphabetically rather than in wire order. The real wire puts an
      internally tagged enum's `"type"` first, and System.Text.Json's polymorphic reader
      requires it there — so the file misrepresented what vigil actually sends, and four of the
      five `LiveMessage` samples could not be read (`ups` passed only because `"type" < "ups"`).
      `serde_json`'s `preserve_order` is now on as a dev-dependency, so the contract shows the
      bytes that really go over the socket.
      Numbers are compared structurally, not textually: serde writes an `f64` of 100 as `100.0`
      and System.Text.Json writes `100`, which is the same number and not a contract break.
- [x] **6.3 The clients.** `VigildClient` and `NutClient`, with every Rust test translated.
      64 tests.
      **The "port the SSE parser" plan was wrong**, and checking beat assuming: .NET 10 has
      `System.Net.ServerSentEvents.SseParser` in the framework, and
      `SocketsHttpHandler.ConnectCallback` lets `HttpClient` speak to a unix socket — so the C#
      client hand-rolls no framing at all, where the Rust one must because no maintained crate
      exists. NUT is still hand-rolled in both: `rups` had 1,100 recent downloads and no release
      since 2023, and .NET has no equivalent.
      Two behaviours needed care. `SseParser` discards comments, so a keepalive never surfaces
      as an event; timing out on events would make a *hung* vigild — which stops publishing but
      keeps sending keepalives — look disconnected and churn a reconnect every timeout.
      `IdleTimeoutStream` sits below the parser so any byte counts, matching the Rust read
      timeout. And derived UPS wattage rounds away from zero: .NET's default is banker's
      rounding, which would put the two implementations a watt apart.
      The unix socket is covered by its own test with an in-process socket server, because
      every other client test uses TCP and would have left the production path unexercised.
- [x] **6.4a The data layer: connection and writes.** Npgsql, two data sources, and the sample,
      drive and event writes. 88 tests, 6 of them against real TimescaleDB.
      **Scope corrected while starting:** the plan had EF Core migrations here, but the Rust
      core still *owns* the schema until 6.7 — two systems applying it would be worse than
      either. Migrations move to 6.7 with the cutover, and until then C# reads and writes a
      schema someone else owns.
      `DATABASE_URL` is a URL and Npgsql takes keyword strings, so `PostgresUri` converts, with
      15 tests: percent-decoding the user info is the easy thing to get wrong, since a password
      containing `@` or `:` must be encoded and failing to decode it looks like a wrong password
      rather than a parsing bug. Errors redact it.
      `DATABASE_URL_READONLY` gives the read path its own `SELECT`-only role. Merging the two
      processes cannot give back the boundary phase 1 built — one address space holds both
      credentials — but the restricted role still means a bug in a query path cannot write, and
      startup warns when it is unset.
      The database tests skip unless `VIGIL_TEST_DATABASE_URL` points at one, so
      `scripts/check.sh` needs no Docker; they cover what a mock cannot — `unnest` array
      expansion, the `$1::text::timestamptz` casts, `on conflict do nothing` on a retry, null
      versus zero for an absent metric, and the epoch-milliseconds round trip.
- [x] **6.4b The history queries.** Ported from `web/src/lib/server/history.ts`, with the SQL
      moved into `core/sql/history_*.sql` (STYLE.md §3.2). 99 tests, 11 against real TimescaleDB
      including the weighted-average rollup path. The TypeScript copy stays until 6.7 deletes it.
      Rounding is away from zero, matching the JavaScript it came from rather than .NET's
      banker's default, which would disagree on every halfway value.
      The rollup table name is interpolated from an exhaustive switch over a closed set, as the
      TypeScript does after validating the range — a continuous aggregate cannot be named by a
      bound parameter. `scripts/check.sh` now enforces the `sql-literal-ok` rule over the C#
      data layer too, and immediately caught an unjustified interpolation in a test helper.
      **Two failures that looked like query bugs were parallel test interference:** xunit runs
      test classes concurrently and these share one Postgres, each truncating the tables it
      uses. They are one serial collection now.
- [x] **6.5 Alerting.** Conditions and the debouncer, with every Rust test translated; this is
      pure logic and the tests are the specification. 117 tests: all 13 from `alerts.rs`, plus
      five pinning what the port could get wrong: ordinal key order, a return mid-clear
      resetting the absence count, a threshold of zero, a failed drive announced once, and the
      bypass/overload wording.
      `core-net/Vigil.Core/Alerting/` is split the way phase 4 meant to split `alerts.rs`:
      snapshot conditions, UPS conditions, the debouncer and the drive diff, none over 110 lines.
      Two things a straight translation would have broken. Conditions are a `SortedDictionary`
      with `StringComparer.Ordinal`, because the debouncer raises in key order and a
      culture-aware sort would order events differently from the Rust core. And UPS figures
      round away from zero, since .NET's banker's rounding turns 150s of runtime into "2 min"
      where Rust says 3; the ported test catches exactly that.
      **Fixed on the way:** `core-net/` had no `.gitattributes` line, so with `core.autocrlf`
      on Windows every C# file checked out CRLF and `dotnet format --verify-no-changes` failed
      on all of them. It is `eol=lf` now, matching `.editorconfig`.
      **Open, Windows only:** `NutClientTests.AClosedConnectionIsReportedAndRetried` fails here
      intermittently (most runs) and passes on Linux. The fake upsd closes before reading, so Windows answers the client's
      `LIST VAR` with a reset and the reason is the socket's wording, not "upsd closed the
      connection". Untouched, since it is the client's behaviour (6.3), not alerting's.
- [x] **6.6 The API.** `/api/live` over `TypedResults.ServerSentEvents`, `/api/history`,
      `/api/events`, and the SvelteKit build served as static files. 147 tests.
      **Scope corrected while starting:** `/live` streams the runtime's state, and no step had
      ported `core/src/runtime.rs`, so it is here. `Vigil.Core/Runtime/` is the single owner of
      state on one input channel, as in Rust, with all four Rust tests translated. The pollers
      are hosted services that only forward into that channel. It reaches the database through
      `IRuntimeStore`, so "the database is down" is a fast test case rather than a connect to a
      closed port. The runtime still does not create or migrate the schema (6.4a's rule).
      `DatabaseGate` keeps the Rust behaviour Npgsql's pool would otherwise lose: a lost
      connection closes the gate for 15s, because each attempt against a dead host waits out the
      connect timeout on the loop that feeds the live view. A `PostgresException` (the server
      answered) leaves it open, so one bad row does not stop history.
      ntfy is an `HttpClient` now; `curl` is gone from vigil-core.
      Decided while doing it, each worth a look at review:
      the browser path is `/api/live`, not `/live`, because that is what the page already
      requests and vigil-web, the only `/live` consumer, goes away in 6.7;
      the keepalive is a named `keepalive` event, not an SSE comment, because the framework's
      formatter writes events only (an SSE comment would mean hand-writing frames, STYLE.md
      §3.1), and EventSource gives a named event to no handler the page has;
      vigil-web's `core` message is not sent: the page's own stream is now the connection to
      vigil-core, so EventSource's `onerror`/`onopen` already say it;
      the CSP header carries only `frame-ancestors`, `object-src`, `base-uri` and `form-action`.
      Script and style policy must come from the static build's `<meta>` tag in SvelteKit's
      hash mode, since a header `script-src` would block the build's inline scripts. 6.7 has
      to switch `vite.config.ts` to `mode: 'hash'`.
      **A bug the smoke test caught:** a scripted edit to `Program.cs` matched nothing, so the
      process served only `/health` while every unit test passed, because each composed its
      own app. `VigilServicesTests` now builds the real composition with `ValidateOnBuild`.
      Smoke-tested against `dev/mock-vigild.mjs` with no database: state on connect, a
      snapshot every 2s, history 503 with its message, the event log `[]`, and only state
      changes in the log (ASP.NET's per-request lines are filtered to warnings).
      **Not run here:** the database test for the new `select_recent_events.sql`. Docker was
      not running.
      **Decided for 6.7:** one listener, on the LAN, and no loopback-only second one (ADR-017).
- [x] **6.7 Cut over.** The Rust `core/` and vigil-web's server half are deleted; `core-net/` is
      `core/`; the Rust workspace is vigild alone. `scripts/check.sh` is green on Windows:
      107 Rust tests, 136 .NET (11 skipped without a database), 31 web.
      Done in this step, each recorded where it belongs:
      **One LAN listener** (ADR-017). `CORE_HOST` defaults to `0.0.0.0` and must be an address;
      `CORE_ALLOW_NON_LOOPBACK` is gone. ADR-008 still holds because every route is a GET, and
      `ApiTests.EveryRouteIsAGet` now enforces that. Production keeps port 3000.
      **Rust folded into vigild.** `protocol/` and `logging/` became `daemon/src/protocol/` and
      `daemon/src/logging.rs`, keeping only what vigild produces; the SSE parser and the
      live-stream types went with the Rust core.
      **The contract, split by producer** (ADR-017). `daemon/contract.json` from vigild's tests
      (with its keepalive, which nothing published before), `core/contract.json` from
      vigil-core's (which rewrite it and fail once when stale), and the web test reads both.
      **The schema moved to C#.** `Data/SchemaSetup.cs` is `timescale.rs` ported as it was:
      idempotent setup on every database (re)connect. **Not EF Core migrations,** which this
      plan named: porting the existing, verified behaviour came first, and a migration history
      is deferred until something needs one (ADR-016).
      **Deployment:** self-contained single-file `linux-x64` (`core/deploy/package.sh`), not
      framework-dependent and not NativeAOT (ADR-016). `core/deploy/vigil-core.service` is
      `Type=notify` and takes the hardening baseline except `MemoryDenyWriteExecute`, which the
      JIT cannot run under.
      **The web is a static build** served by vigil-core: `adapter-static`, CSP in hash mode as
      a `<meta>` tag, the theme script as `static/theme.js`, the page filling itself from
      `/api/live` and `/api/events`, and `npm run dev` proxying `/api` to vigil-core.
      **Fixed on the way:** the Windows-only NutClient test failure from 6.5. A reset from
      upsd and an end of stream now both read "upsd closed the connection", since which one the
      client sees depends on platform and timing. `web/**` joined `core/**` as `eol=lf`, for
      the same CRLF reason. **And a bug older than this phase:** `global.json` sat in `core-net/`,
      but `dotnet` looks for it from the working directory, and every command here runs from the
      repo root. So `check.sh` and CI-like local runs built with the 11.0 RC SDK that the pin
      exists to avoid. It is at the repo root now, and builds resolve to 10.0.x.
      Docs: ADR-003, ADR-008 and ADR-015 amended and ADR-016/017 added; SECURITY.md and
      CLAUDE.md rewritten for two processes.
      Verified: the packaged layout's pieces run together against `dev/mock-vigild.mjs`;
      Chrome renders the dashboard live under its CSP with no console errors; and
      `core/deploy/package.sh` builds `vigil-core.tar.gz` (one 102 MB binary plus `web/`).
      **Not verified here:** anything against a real database (Docker was not running), so
      `SchemaSetup` has its pure tests but not yet a run against TimescaleDB 2.30.2; and the
      package and unit on the host. Run the database tests with
      `VIGIL_TEST_DATABASE_URL` before deploying.
      **Seen, not chased:** with the database down, the empty history charts print hundreds of
      x-axis date labels. Chart code was not touched by this phase.

### Follow-up: vigil-core on NativeAOT

- [ ] **Build vigil-core with NativeAOT instead of as a self-contained JIT build.** Not done in
      6.7 (ADR-016) because nothing here could build it, and porting the behaviour came first.
      **What it buys:** vigil-core's unit can set `MemoryDenyWriteExecute=true`, closing the one
      gap in its hardening baseline (the JIT is what forces the omission today). A smaller
      binary and faster start are expected too, but they are not the reason.
      **What it needs:**
      - **A Linux build.** NativeAOT does not cross-compile between operating systems, so
        `core/deploy/package.sh` can no longer build vigil-core on Windows. Build it in CI on
        Linux, or in WSL or a container, with `clang` and `zlib1g-dev` installed.
      - **`PublishAot=true`** in `Vigil.Core.csproj`. That turns on the trim and AOT analyzers,
        and with warnings as errors every reflection path becomes a build failure to fix:
        - JSON: a `JsonSerializerContext` source generator behind `VigilJson`, covering
          `LiveMessage`'s polymorphism, `HistoryResponse`, `EventRecord` and vigild's
          `Snapshot`;
        - options: the configuration binding source generator for `VigilOptions`;
        - Npgsql: `NpgsqlSlimDataSourceBuilder` in `VigilDataSources`, enabling only the type
          handlers vigil uses;
        - minimal APIs: the request delegate generator, which `PublishAot` switches on.
      **Done when:** the binary is built on Linux with zero AOT warnings; the unit sets
      `MemoryDenyWriteExecute=true` and loses its `NOT set` comment; ADR-016 is amended; and the
      AOT binary passes the database tests and a smoke test against `dev/mock-vigild.mjs`.
