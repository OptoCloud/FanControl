# Architecture

Two processes, one job each, and a JSON contract between them. The split is a privilege
boundary first and a code boundary second: see `docs/SECURITY.md` §1.

```
                sysfs PWM, /dev/mpt3ctl, smartctl, nvidia-smi
                                  │
                        ┌─────────▼─────────┐
                        │  vigild  (root)   │  the only writer of hardware
                        │   Rust, daemon/   │  keeps no history
                        └─────────┬─────────┘
                       unix socket │ /status, /events
                        ┌─────────▼─────────┐
          upsd ────────>│    vigil-core     │──────> Postgres + TimescaleDB
     (NUT, TCP)         │    C#, core/      │        (owns the schema)
                        │  one state loop   │──────> ntfy (optional)
                        └─────────┬─────────┘
                              LAN │ one listener, no auth (ADR-008)
                                  │ the dashboard (web/build) + GET /api/*
                               browser
```

vigil-web used to be a third process, relaying vigil-core's stream and querying Postgres from
Node. It is gone: `web/` is now a static build that vigil-core serves, which removed a whole SSE
hop and Node from the deployment.

## Programs

| Path | What it owns | Language |
|---|---|---|
| `daemon/` | `vigild`: sensors, drive health, fan curves, fan safety, the socket API | Rust, one crate |
| `core/` | `vigil-core`: polling, history, alerting, the API, serving the dashboard | C#, .NET 10 |
| `web/` | the dashboard: one prerendered SvelteKit page | TypeScript, Svelte 5 |

vigild's HTTP is axum and its logger is `tracing` (ADR-015); vigil-core's are ASP.NET Core and
`ILogger` with systemd's journal format.

## The dependency rule

**The three never import each other.** What passes between them is JSON, described by the side
that produces it:

```
daemon/src/protocol/  ──writes──>  daemon/contract.json  <──checked by──  core tests, web tests
core/Vigil.Core/Protocol/  ──writes──>  core/contract.json  <──checked by──  web tests
```

A need for vigil-core to call into vigild means it belongs on the other side of the socket,
which is the real answer most of the time.

*Enforcer:* there is nothing to import across (one Rust crate, one C# application, one static
page); the contract tests in `docs/STYLE.md` §1.3 hold the boundary.

## Module map

### `daemon/`: vigild

Hardware-independent logic is separated from platform plumbing so every failure path is
testable against an in-memory sysfs, with no hardware and no root.

| Module | Job |
|---|---|
| `main.rs` | the loop, signals, startup, `--check` |
| `control.rs` | **one** poll: read, evaluate, drive, report. No timing, signals or sockets |
| `config/` | the config shape and defaults (`mod.rs`), and the validation that runs before any fan is touched (`validate.rs`) |
| `curve.rs` | piecewise-linear curves with asymmetric hysteresis |
| `fans/` | split by failure mode: `controller.rs` (the only code that writes a header), `stall.rs`, `safety.rs` (the `Drop` guard and the deadman) |
| `sensors.rs` | hwmon discovery by chip name, drive identity by WWN, bay by by-path |
| `sysfs.rs` | the sysfs trait, and its in-memory fake |
| `mpt3.rs` | pure MPI2 wire format for IO Unit Page 7: no native call, fully unit-tested |
| `hba.rs` | the thin `ioctl` wrapper around `mpt3.rs` |
| `smart.rs` | smartctl JSON parsing; never wakes a sleeping drive |
| `gpu.rs` | nvidia-smi, out-of-process on purpose |
| `process.rs` | child process with a hard timeout and bounded output |
| `status.rs` | the snapshot hub consumers subscribe to |
| `server.rs` | the socket API: axum's routes plus the socket's permissions |
| `conditions.rs` | `ConditionLog`: reporting a condition when it starts and when it clears |
| `notify.rs` | sd_notify readiness and watchdog |
| `logging.rs` | choosing journald or stderr for `tracing` |
| `protocol/` | the wire types vigild serves, and the generator for `daemon/contract.json` |

### `core/`: vigil-core

One loop owns all state. vigild's snapshots, UPS readings and new live subscribers arrive as
messages on one channel, so there are no locks and events are ordered as they happened.

| Namespace | Job | May use |
|---|---|---|
| `Protocol` | the wire types and their JSON options; the source of `core/contract.json` | nothing |
| `Configuration` | `VigilOptions` from the environment's published names, validated before start | nothing |
| `Clients` | `VigildClient` (vigild's socket, SSE via the framework's parser) and `NutClient` (upsd) | Protocol, Configuration |
| `Alerting` | **pure** condition logic: snapshot and UPS conditions, the debouncer, the drive diff. No I/O | Protocol |
| `Data` | Npgsql: two data sources, `SchemaSetup` (tables, hypertables, aggregates, policies, applied on every connect), the writers and the history and event queries. SQL from `core/sql/` | Protocol, Configuration |
| `Notifications` | `NtfyClient`, over HTTPS | Alerting, Protocol |
| `Runtime` | `VigilRuntime`, the one owner of state; `DatabaseGate`; `LiveHub` and the subscriber cap; the hosted services that feed it | all of the above |
| `Api` | the routes (`/api/live`, `/api/history`, `/api/events`, `/health`), the static dashboard and its security headers | Runtime, Data, Protocol |

`Program.cs` and `VigilServices.cs` only compose these. Nothing below `Runtime` knows it is in a
web server, which is why all of it is unit-tested without one.

### `web/`: the dashboard

| Path | Job |
|---|---|
| `routes/+page.svelte` | the dashboard page: the live stream, history and the event log, all from `/api/*` |
| `routes/+layout.ts` | prerenders the one page (`adapter-static`) |
| `static/theme.js` | applies the saved theme before first paint; a file, so the CSP needs no hand-kept hash |
| `lib/chart/` | scales, ticks, formatting |
| `lib/components/` | presentation |
| `lib/components/ui/`³ | vendored shadcn-svelte: never hand-edited |
| `lib/types.ts` | the mirror of both contract files |

³ arrives in Phase 5.

There is no server code in `web/`. In development, Vite proxies `/api` and `/health` to a
running vigil-core (`VIGIL_CORE_URL`, default `http://127.0.0.1:3001`).

## Invariants

Breaking any of these is a design change, not a refactor.

1. **vigild is the only component that changes hardware.** Consumers read.
2. **A fan channel is never left on manual.** Four independent layers guarantee it (safety guard
   via `Drop`, re-arming deadman thread, systemd watchdog, `ExecStopPost`). This is why
   `panic = "unwind"`: an abort would skip the `Drop`.
3. **An unreadable sensor is never treated as cold.** It falls back to the curve's fail-safe duty.
4. **vigild keeps no history.** Trends, memory of a sleeping drive's last good result, and
   alerting all need history, and history is vigil-core's job.
5. **One owner per piece of state.** vigil-core's runtime loop.
6. **vigil-core serves the LAN on one listener, and nothing it serves can change anything.**
   Every route is a GET; that is the whole basis of running without authentication (ADR-008).
   It writes history, but no request can cause a write.
7. **Sensors are resolved by name, never by a remembered `hwmonN` path**, and a drive carries two
   keys: its WWN (the disk) and its by-path port (the bay).
8. **The live view works without a database.** A database failure is retried, never fatal.
9. **No TLS stack in vigild, and its control loop is plain threads** (ADR-003, ADR-004). Both
   bind vigild only: vigil-core is .NET, where HTTPS and async are the platform.
