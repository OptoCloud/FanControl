# Architecture

Three processes, one job each, and a shared definition of what passes between them. The split
is a privilege boundary first and a code boundary second: see `docs/SECURITY.md` §1.

```
                sysfs PWM, /dev/mpt3ctl, smartctl, nvidia-smi
                                  │
                        ┌─────────▼─────────┐
                        │  vigild  (root)   │  the only writer of hardware
                        └─────────┬─────────┘
                       unix socket │ /status, /events
                        ┌─────────▼─────────┐
          upsd ────────>│    vigil-core     │──────> Postgres + TimescaleDB
     (NUT, TCP)         │  one state thread │        (owns the schema)
                        └─────────┬─────────┘
                        loopback   │ /live, /health
                        ┌─────────▼─────────┐
                        │    vigil-web      │──────> Postgres (reads only)
                        │   (SvelteKit)     │
                        └─────────┬─────────┘
                              LAN │ :3000, no auth (ADR-008)
                               browser
```

## Crates and packages

| Path | What it owns | Depends on |
|---|---|---|
| `protocol/` | the types that cross a process boundary, plus the SSE *parser* for reading them | nothing of ours |
| `logging/` | how both binaries configure `tracing` (journald when there is a journal, stderr when not) | `protocol` |
| `daemon/` | `vigild`: sensors, drive health, fan curves, fan safety | `protocol`, `logging` |
| `core/` | `vigil-core`: polling, history, alerting, the live stream | `protocol`, `logging` |
| `web/` | `vigil-web`: the dashboard | `protocol`'s contract file |

The HTTP servers are `axum` and the logger is `tracing` (ADR-015). There was briefly an
`httpd/` crate of our own, sharing one hand-rolled server between the two binaries; sharing a
hand-rolled HTTP server is still a hand-rolled HTTP server, and it is gone.

## The dependency rule

Dependencies point inward. **Nothing shared ever imports a binary crate, and the three binary
crates never import each other.**

```
protocol  <──  logging  <──  daemon, core
protocol  <──────────────────── web (via contract.json)
```

A need for `core` to call into `daemon` means the shared part belongs in a shared crate — or
that it belongs on the other side of the socket, which is the real answer most of the time.

*Enforcer:* `cargo tree` check in `scripts/check.sh`.

## Module map

### `daemon/` — vigild

Hardware-independent logic is separated from platform plumbing so every failure path is
testable against an in-memory sysfs, with no hardware and no root.

| Module | Job |
|---|---|
| `main.rs` | the loop, signals, startup, `--check` |
| `control.rs` | **one** poll: read, evaluate, drive, report. No timing, signals or sockets |
| `config.rs` | config shape, defaults, and validation that runs before any fan is touched |
| `curve.rs` | piecewise-linear curves with asymmetric hysteresis |
| `fans.rs` | sysfs PWM controller, stall detection, the safety guard and its deadman |
| `sensors.rs` | hwmon discovery by chip name, drive identity by WWN, bay by by-path |
| `sysfs.rs` | the sysfs trait, and its in-memory fake |
| `mpt3.rs` | pure MPI2 wire format for IO Unit Page 7 — no native call, fully unit-tested |
| `hba.rs` | the thin `ioctl` wrapper around `mpt3.rs` |
| `smart.rs` | smartctl JSON parsing; never wakes a sleeping drive |
| `gpu.rs` | nvidia-smi, out-of-process on purpose |
| `process.rs` | child process with a hard timeout |
| `status.rs` | the snapshot hub consumers subscribe to |
| `server.rs` | the socket API: axum's routes plus the socket's permissions |
| `conditions.rs` | `ConditionLog`: reporting a condition when it starts and when it clears |

### `core/` — vigil-core

One thread owns all state. Inputs arrive as messages on one channel, so there are no locks and
events are ordered as they happened.

| Module | Job |
|---|---|
| `main.rs` | wiring: config, listener, one thread per source |
| `runtime.rs` | the state thread: history, events, subscribers |
| `daemon.rs` | the client for vigild's socket |
| `nut.rs` | the upsd client (`LIST VAR`) |
| `alerts.rs` | **pure** condition logic — snapshot, UPS, drive diffs, and `ConditionDebouncer`. No I/O, so fully tested |
| `db.rs` | Postgres: connection, schema, every write |
| `timescale.rs` | hypertables, continuous aggregates, retention and compression policies |
| `ntfy.rs` | push notifications through `curl` |
| `server.rs` | `/live`, `/health`, loopback only |

`core/src/notify.rs` was renamed to `ntfy.rs` because `daemon/src/notify.rs` is sd_notify, and
one name must not mean two things. `core/src/sse.rs` moved into `protocol/`.

### `web/` — vigil-web

| Path | Job |
|---|---|
| `routes/+page.svelte` | the dashboard page |
| `routes/api/live` | relays vigil-core's stream to browsers |
| `routes/api/history` | the history query endpoint |
| `lib/server/core.ts` | the one connection to vigil-core, mirrored and fanned out |
| `lib/server/db.ts` | Postgres, reads only |
| `lib/server/history.ts` | chart queries, bucketed in SQL |
| `lib/chart/` | scales, ticks, formatting |
| `lib/components/` | presentation |
| `lib/components/ui/`³ | vendored shadcn-svelte — never hand-edited |
| `lib/types.ts` | the mirror of `protocol`, held by `contract.json` |

³ arrives in Phase 5.

## Invariants

Breaking any of these is a design change, not a refactor.

1. **vigild is the only component that changes hardware.** Consumers read.
2. **A fan channel is never left on manual.** Four independent layers guarantee it (safety guard
   via `Drop`, re-arming deadman thread, systemd watchdog, `ExecStopPost`). This is why
   `panic = "unwind"` — an abort would skip the `Drop`.
3. **An unreadable sensor is never treated as cold.** It falls back to the curve's fail-safe duty.
4. **vigild keeps no history.** Trends, memory of a sleeping drive's last good result, and
   alerting all need history, and history is the consumer's job.
5. **One owner per piece of state.** vigil-core's single state thread.
6. **vigil-web writes nothing.** No mutating route, and its database role holds `SELECT` only.
7. **Sensors are resolved by name, never by a remembered `hwmonN` path**, and a drive carries two
   keys: its WWN (the disk) and its by-path port (the bay).
8. **The live view works without a database.** A database failure is retried, never fatal.
