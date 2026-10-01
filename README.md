# vigil

Looks after the hardware of a Linux (Proxmox) NAS host. Two programs, one job
each:

- **`vigild`** (`daemon/`, Rust), the hardware daemon. It reads every
  temperature on the host and each drive's SMART health, and drives the fans,
  because BIOS Smart Fan curves have no idea what an LSI HBA, a drive array,
  or a GPU are actually doing. It runs as root on the host and is the only
  part that changes anything.
- **`vigil-core`** (`core/`, C# on .NET 10), everything else. It reads
  `vigild` and the UPS, records history in Postgres, raises alerts, and serves
  the dashboard (`web/`, a static SvelteKit build) with its live stream and
  history to the LAN.

```
vigild (host, root) ──unix socket──┐
                                   ├──> vigil-core ──> Postgres + TimescaleDB
upsd (NUT, host) ──────TCP─────────┘        │
                                            └── LAN: dashboard + GET /api/* ──> browser
```

Most of this README is `vigild`; [vigil-core](#vigil-core) and
[the dashboard](#the-dashboard) have their own sections.

## Why

The stock ASRock B550 Pro4 / NCT6798D Smart Fan curves only see board and CPU
temperatures. This host also carries:

- an LSI SAS9300-8i HBA (`mpt3sas`) driving 8 drives
- 13 drives total (8 HBA + 5 SATA), each exposed individually via `drivetemp`
- an RTX 3060 (`nvidia-smi`)
- 2 DIMMs via `jc42`

None of that feeds the BIOS curve. This daemon reads all of it and drives the
board's PWM headers directly.

It is a single static Rust binary of about 1.5 MB. The control loop and the
fan-safety machinery are plain threads; only the status API is async, served by
axum on a small tokio runtime of its own ([ADR-015](docs/DECISIONS.md)). (It
started life as a .NET daemon; that implementation is in the git history.)

## How it works

- **Sensors are resolved by name, never by hwmon path.** `hwmonN` numbering
  shifts across reboots and module load order, so every sensor is found by
  its chip name (`k10temp`, `nct6798`, `drivetemp`, `jc42`), and drive
  sensors are keyed by the drive's WWN (`drive:<wwn>`), since even the `sdX`
  letter isn't stable. The hwmon tree is re-scanned periodically
  (`sensor_rescan_interval_secs`, default 30), so a drive that resets or is
  hot-swapped is picked back up without a restart.
- **Only whitelisted sensors are read.** Unconnected/floating hwmon inputs
  (`AUXTIN0/1/2`, `CPUTIN` on this board) are never touched.
- **Fan headers are addressed by verified mapping, not guesswork.** There is
  no documented `pwmN` to silkscreen header table for this board; the mapping
  in config must be established by walking each channel in manual mode and
  watching which tach responds (`deploy/walk-fan-headers.sh`,
  `deploy/pulse-fan.sh`).
- **Manual PWM is never left dangling.** Manual PWM is sticky in the chip: a
  header left on manual holds its last duty forever. Four layers prevent that:
  1. a safety guard releases every channel back to the automatic mode it was
     found in (BIOS Smart Fan IV here) on shutdown, on an early exit and on a
     panic, since it runs from `Drop`;
  2. an independent, re-arming deadman thread does the same if the control
     loop stalls while the process stays alive;
  3. the control loop pings systemd after every poll (`WatchdogSec`), so a
     process that freezes as a whole, deadman thread included, gets killed;
  4. `deploy/release-fans.sh`, run by systemd as `ExecStopPost`, covers every
     exit nothing in-process can react to (watchdog kill, SIGKILL, SIGBUS,
     OOM kill).
- **An unreadable sensor is never treated as "cold".** If every sensor behind
  a curve is unavailable the fan runs at the curve's `fail_safe_duty_percent`;
  if only some explicitly named sensor is (say `gpu` on a `gpu`+`hba` curve),
  the curve still runs but never below that fail-safe duty.
- **A sensor's kind doesn't decide which fan sees it; its physical location
  does.** `drive:*` is a sensor *category*, not a location: on this board two
  SSDs sit screwed to the case in the top compartment with the LSI card, GPU
  and CPU, nowhere near the drive cage. `zones` group sensors by the airflow
  they sit in, and a curve names zones (`zones = [...]`) instead of, or
  alongside, raw `sensor_ids`.
- **A drive has two keys: the disk and the bay.** Its sensor id is its WWN
  (`drive:naa.5000c500...`), which follows the disk wherever it's plugged in:
  right for health and history. Its `port` is its `/dev/disk/by-path` name
  (`pci-0000:01:00.1-ata-3`, `pci-0000:03:00.0-sas-phy0-lun-0`), which stays
  with the bay whatever disk is in it: right for cooling. Zones name bays by
  port (`port:pci-0000:01:00.1-ata-3`), so a swapped disk lands in the right
  zone with no config change. by-path carries the controller's PCI address,
  so two controllers' "port 3" never collide.
- **One failing channel doesn't take the others down.** A header whose sysfs
  write fails is handed back to automatic control and retried every poll,
  while the remaining channels keep being driven.
- **Config is validated before any fan is touched.** Out-of-range duties,
  unsorted curve points, a curve naming an unknown channel, or a channel with
  no curve (or two) all refuse to start with every problem listed.
  `vigild --check` does only that, then (on the target machine) lists what
  every curve would read and warns about any member that matches
  nothing there, and exits. It reads no temperature and touches no fan.
- **Fan stalls are detected.** A header reading 0 RPM for several consecutive
  polls while being driven is logged and flagged as `stalled`.
- **HBA temperature is read via a raw ioctl.** `mpt3sas` has no hwmon
  exposure for the LSI SAS9300-8i's IOC/board temperature; it's read directly
  from Config Page IO_UNIT_PAGE_7 via `/dev/mpt3ctl`, the same mechanism
  vendor tools like `lsiutil` use, reimplemented from the actual kernel
  driver source rather than a third-party binary.
- **Drive SMART health never wakes a sleeping drive.** Polled on its own slow
  interval (default 15 min), fully decoupled from the 2s control loop, using
  `smartctl -n standby` so a drive already asleep is skipped rather than spun
  up just to answer a health check.
- **External tools can't hang the daemon.** `nvidia-smi` and `smartctl` both
  run under a hard timeout and are killed if they exceed it. `nvidia-smi`
  stays out-of-process on purpose: a wedged GPU driver can then only hang a
  child that gets killed, never the control loop.
- **As little state as possible.** The daemon holds the latest snapshot plus
  what the control algorithm itself needs between polls (hysteresis anchors,
  stall counters, the original `pwm_enable` modes). It keeps no history.
  Remembering a sleeping drive's last good SMART result, noticing that a
  sector count grew, trends and alerting all need history, and history is the
  consumer's job.
- **Status is read-only and never on the network.** A root daemon with raw
  PWM and ioctl access should not listen on a port, so the API is a unix
  socket. A containerised consumer gets the socket's directory bind-mounted
  in. Any number of consumers can connect at once.

## API

```bash
curl    --unix-socket /run/vigil/vigild.sock http://localhost/status
curl -N --unix-socket /run/vigil/vigild.sock http://localhost/events
```

- `GET /status`: the latest snapshot as JSON (`503` before the first poll).
- `GET /events`: Server-Sent Events. One `status` event per poll carrying the
  same JSON, starting with the current snapshot immediately on connect. A
  `: keepalive` comment is sent when nothing was published for 15 seconds.

SSE rather than WebSocket because data only flows one way: it is plain HTTP
and clients reconnect on their own. vigil-core reads it with .NET's
`HttpClient` over the socket. Simultaneous connections are
capped by `api.max_clients`; past that, new ones get `503`.

```jsonc
{
  "timestampUtc": "2026-09-21T23:01:34.155Z",
  // false if a channel couldn't be driven this poll, or if this snapshot is
  // older than the deadman timeout (the loop has hung)
  "controlLoopHealthy": true,
  "sensors": [
    { "id": "cpu", "category": "cpu", "label": "Tctl", "celsiusOrNull": 38.25, "isAvailable": true, "sourcePath": "..." },
    { "id": "drive:naa.5000c500...", "category": "drive", "port": "pci-0000:01:00.1-ata-3", "...": "..." }
    // category: cpu, boardAmbient, drive, gpu, memory, hba
    // port: drives only, the /dev/disk/by-path name of where it's plugged in; null otherwise
  ],
  "fans": [
    { "id": "drive-cage", "dutyPercent": 65, "rpm": 1211, "mode": "manual", "stalled": false }
    // mode: disabled, manual, thermalCruise, speedCruise, smartFanIII, smartFanIV, or null if unreadable
  ],
  "driveHealth": [
    { "deviceName": "naa.5000c500...", "port": "pci-0000:01:00.1-ata-3", "passed": true, "reallocatedSectorCount": 0, "pendingSectorCount": 0,
      "powerOnHours": 8760, "isAvailable": true, "asOf": "2026-09-21T23:00:38.412Z", "sourcePath": "/dev/sdc" }
    // Exactly what the last SMART poll saw. A sleeping drive is not woken, so it shows
    // isAvailable=false with nulls: keep its previous values on the consumer side.
    // pendingSectorCount is null on drives that don't report attribute 197 (most SSDs).
  ]
}
```

## Project layout

- `daemon/`: `vigild`, a single Rust crate. Hardware-independent logic
  (curves, config validation, the HBA wire format, SMART parsing, the safety
  guard, the control loop itself) is separated from the thin platform
  plumbing around it and unit-tested against an in-memory sysfs. Its wire
  types are in `daemon/src/protocol/`, and its tests write them out as
  `daemon/contract.json`.
- `deploy/`: `vigild`'s systemd unit, config, `tmpfiles.d` and
  `modules-load.d` entries, `release-fans.sh`, and the header-mapping helper
  scripts.
- `core/`: `vigil-core` (`Vigil.Core`, `Vigil.Core.Tests`), every SQL
  statement it runs in `core/sql/`, its deployment in `core/deploy/`, and
  `core/contract.json`, the wire types it sends the browser, written by its
  tests. See [below](#vigil-core).
- `web/`: the dashboard, see [below](#the-dashboard). `web/src/lib/types.ts`
  mirrors both contract files, and a test checks it against them.
- `dev/`: stand-ins for `vigild` and upsd, and a Postgres, for developing
  without the hardware.

## Conventions

Four short documents, all specific to this codebase. Every rule in them names
what enforces it, because a rule nothing checks does not survive.

- [`docs/STYLE.md`](docs/STYLE.md): shared code, module boundaries, protocol
  bodies, naming, comments, errors, tests, and the per-language sections.
- [`docs/SECURITY.md`](docs/SECURITY.md): trust boundaries, privilege,
  untrusted input, subprocesses, SQL, secrets, and a pre-merge checklist.
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md): the module map, the
  dependency rule, and the invariants that are design changes, not refactors.
- [`docs/DECISIONS.md`](docs/DECISIONS.md): why the non-obvious choices are
  what they are. Read it before "simplifying" one.

[`docs/CLEANUP.md`](docs/CLEANUP.md) is the live work plan, and `CLAUDE.md` is
the short version of all of it.

## Build and test

`scripts/check.sh` runs every check there is: formatting, clippy, the Rust
tests, `cargo-deny`, the .NET format, build and tests, both wire contracts,
the web lint, type-check and tests, `npm audit`, and the project rules a grep
can enforce. CI runs that same script and nothing else.

```bash
./scripts/check.sh            # everything, before every commit
./scripts/check.sh --rust     # or --dotnet, --web, --rules
```

Developed and unit-tested on Windows, runs only on Linux. No cross toolchain
is needed for either program: the musl target ships its own C runtime and
links with Rust's bundled LLD, and .NET publishes for Linux from anywhere.

```bash
cargo test --workspace                                         # vigild's tests, any OS
dotnet run --project core/Vigil.Core.Tests                     # vigil-core's tests
rustup target add x86_64-unknown-linux-musl                    # once
cargo build --release --target x86_64-unknown-linux-musl       # static vigild
core/deploy/package.sh                                         # vigil-core.tar.gz, see below
```

`global.json` at the repo root pins the .NET 10 SDK; every command runs from
the root, which is where `dotnet` looks for it. vigil-core's tests run with
`dotnet run`, not `dotnet test`: on this SDK
`dotnet test`'s bridge to xunit.v3 discovers no tests at all. Its database
tests skip unless `VIGIL_TEST_DATABASE_URL` points at a TimescaleDB, such as
the one in `dev/docker-compose.yml`:

```bash
VIGIL_TEST_DATABASE_URL=postgres://vigil:vigil@127.0.0.1:5433/vigil dotnet run --project core/Vigil.Core.Tests
```

`daemon/scripts/integration-test.sh <binary>` runs the real Linux `vigild`
end to end against a fake sysfs tree with stand-in `nvidia-smi`/`smartctl`:
the socket API, several simultaneous event consumers, real fan writes,
fail-safe on a vanished sensor, and the fans being handed back on SIGTERM.
It needs no hardware and no root, so it runs under WSL or in CI:

```powershell
wsl -e bash /mnt/e/path/to/daemon/scripts/integration-test.sh /mnt/e/path/to/target/x86_64-unknown-linux-musl/release/vigild
```

Neither covers the `/dev/mpt3ctl` ioctl, real `nvidia-smi`/`smartctl` output
or the systemd integration. Those were verified on the target host (see
Status) and need re-verifying there after changes to them.

## Deployment

### vigild

**Host prerequisite:** drive SMART health shells out to `smartctl` (package
`smartmontools`), not installed by default on a minimal Proxmox/Debian host:
`apt-get install -y smartmontools`. Without it every drive's health is simply
reported unavailable.

| File | Goes to |
|---|---|
| `target/x86_64-unknown-linux-musl/release/vigild` | `/opt/vigil/vigild` (`chmod +x`) |
| `deploy/release-fans.sh` | `/opt/vigil/release-fans.sh` |
| `deploy/vigild.toml` | `/etc/vigil/vigild.toml` |
| `deploy/vigild.service` | `/etc/systemd/system/vigild.service` |
| `deploy/tmpfiles.d/vigil.conf` | `/etc/tmpfiles.d/vigil.conf` |
| `deploy/modules-load.d/vigil.conf` | `/etc/modules-load.d/vigil.conf` |

**Stop the service before overwriting the binary, every time.** Linux
memory-maps the running executable from disk; overwriting it in place while
the old process is still executing out of it gets that process killed with
SIGBUS the next time it faults in a code page.

```bash
systemctl stop vigild.service
# ... copy the files ...
chmod +x /opt/vigil/vigild
systemd-tmpfiles --create /etc/tmpfiles.d/vigil.conf
modprobe -a nct6775 drivetemp   # -a is required: `modprobe a b` without it loads
                                 # only `a`, treating `b` as a module parameter
/opt/vigil/vigild --config /etc/vigil/vigild.toml --check
systemctl daemon-reload
systemctl enable --now vigild.service
journalctl -u vigild -f
```

`channels` and `curves` in `vigild.toml` are specific to one board and its
wiring. Leave both empty to run monitor-only, which never writes to any `pwmN`
file.

`zones` (optional) group sensors by the airflow they sit in, so a curve can
react to "this part of the case" instead of listing chip-level categories
that don't say where a device actually sits. Members are sensor ids or drive
ports, and anything ending in `*` is a prefix wildcard. On orion:

```toml
# The LSI's eight phys: the 8-bay stack.
[[zones]]
id = "eight-bay"
sensor_ids = ["port:pci-0000:03:00.0-sas-*"]

# The 4-bay stack: scratch, and the two hot spares.
[[zones]]
id = "four-bay"
sensor_ids = ["port:pci-0000:01:00.1-ata-3", "port:pci-0000:01:00.1-ata-6", "port:pci-0000:01:00.1-ata-5"]

# The hottest HDD in either stack sets the drive-cage fans.
[[curves]]
fan_channel_id = "drive-cage"
zones = ["eight-bay", "four-bay"]
points = [[28, 30], [33, 45], [38, 65], [43, 90], [48, 100]]
```

A member named explicitly in one zone is claimed by it and left out of every
other zone's wildcards, so a zone written as `drive:*` stops covering a drive
the moment its port or WWN is listed in another zone. A port named explicitly
with no drive on it counts as an unreadable sensor (the curve's fail-safe
becomes its minimum), so list only filled bays. `ls -l /dev/disk/by-path/` on the
host lists every port; the daemon uses the shortest link for each disk,
ignoring partitions. Run `--check` on the host after editing zones: it prints
exactly which drives each curve picked up.

`vigild.service` runs as root (sysfs PWM attributes are root-owned, mode
644). On stop it relies on `TimeoutStopSec=30` + `SIGTERM` so the daemon
releases every channel itself. If it dies without that chance, `ExecStopPost`
runs `release-fans.sh`, which hands any header still on manual back to Smart
Fan IV; it can also be run by hand (`sh /opt/vigil/release-fans.sh`)
whenever the daemon isn't running.

One thing still defeats that: `systemctl kill -s SIGKILL vigild` signals
the whole unit by default, which kills the `ExecStopPost` script along with
the daemon and leaves every header on manual until the automatic restart takes
them over again. With `--kill-whom=main` (which is what a real crash, SIGBUS or
OOM kill looks like) the script runs and releases everything within a second.
Never SIGKILL the whole unit.

### A consumer in an unprivileged LXC

Bind-mount the socket's *directory* (the socket file itself is recreated on
every start):

```bash
pct set <vmid> -mp0 /run/vigil,mp=/mnt/vigil
```

and give the socket to the host uid the container's root maps to, in
`vigild.toml`:

```toml
[api]
socket_uid = 100000
socket_gid = 100000
```

The directory comes from `tmpfiles.d` rather than the unit's
`RuntimeDirectory=` on purpose: systemd recreates a runtime directory on every
service restart, which gives it a new inode and silently breaks the
container's bind mount.

### vigil-core and the dashboard

`vigil-core` runs in an unprivileged LXC, with `vigild`'s socket directory
bind-mounted in as described above. `core/deploy/package.sh` builds
`vigil-core.tar.gz`: a self-contained, single-file Linux build of about
100 MB (the host needs no .NET runtime; [ADR-016](docs/DECISIONS.md) has why
not NativeAOT), the
dashboard's static build beside it as `web/`, and the unit and env template.

| File | Goes to |
|---|---|
| `vigil-core` | `/opt/vigil-core/vigil-core` (`chmod +x`) |
| `web/` | `/opt/vigil-core/web/` |
| `vigil-core.service` | `/etc/systemd/system/vigil-core.service` |
| `vigil-core.env` | `/etc/vigil-core.env`, filled in (below) |

```bash
systemctl stop vigil-core.service     # same reason as vigild: never overwrite a running binary
tar -xzf vigil-core.tar.gz -C /opt
install -o root -g vigil -m 0640 /opt/vigil-core/vigil-core.env /etc/vigil-core.env   # first time only
cp /opt/vigil-core/vigil-core.service /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now vigil-core.service
journalctl -u vigil-core -f
```

The unit is `Type=notify`, so `systemctl start` returns once vigil-core is
listening. It takes the systemd hardening baseline except
`MemoryDenyWriteExecute`, which .NET's JIT cannot run under; the unit says so.

The dashboard answers on `CORE_PORT` (3000 in the template, where it has
always been) on every interface. **It has no authentication, deliberately:
the LAN is treated as trusted, and every route is a GET that changes
nothing** ([ADR-008](docs/DECISIONS.md)). Anything on the network can read
the host's state and the event log. A test fails the build if a route with
any other method is ever added; adding one means authentication comes first.

#### Secrets and database roles

`/etc/vigil-core.env` holds `DATABASE_URL`, which contains a password, so it
is a secret: outside the repo, `0640 root:vigil`.

vigil-core uses **two roles**. `DATABASE_URL` owns the schema and writes
every row. `DATABASE_URL_READONLY` is what the history and event-log queries
run as, and it can only read, which is the role's job to enforce and not a
comment's: a bug in a query path cannot write. Once, as a superuser on the
Postgres host:

```sql
create role vigil_web login password 'choose-one';
grant connect on database vigil to vigil_web;
grant usage on schema public to vigil_web;
grant select on all tables in schema public to vigil_web;
-- vigil-core creates tables and continuous aggregates as it starts, so new ones have to
-- inherit the grant; without this line a new rollup is invisible to the dashboard.
alter default privileges in schema public grant select on tables to vigil_web;
```

The role keeps the name it had when the dashboard was a separate process, so
an existing grant still works. Left unset, the read path shares the owner's
role and vigil-core says so at startup. See [`docs/SECURITY.md`](docs/SECURITY.md).

## Status

Live and running in production on the target host. Verified there: every
sensor including the HBA ioctl, all six fan headers under curve-driven manual
control, drive SMART health, the systemd notify/watchdog handshake, a clean
stop restoring every header, and `ExecStopPost` releasing every header within
a second of the daemon being SIGKILLed, followed by an automatic restart.
Known accepted quirks:

- One fan header (`lsi-cooling`, `pwm7`) has been observed silently reverting
  `pwm7_enable` from Manual back to Disabled sometime after being set. Not
  documented by the Linux `nct6775` driver, likely a Super I/O chip-level
  watchdog or board-specific quirk. Worked around by re-asserting manual mode
  every poll rather than only once at startup; root cause not otherwise
  identified.
- Curve breakpoints are tuned against real observed temperatures where
  possible, but several (especially `lsi-cooling` and `drive-cage`) are still
  based on limited data. Revisit once real-load history exists.

`vigil-core` was rewritten in C# and merged with the dashboard's server on
2026-10-01, replacing a Rust `vigil-core` and a Node `vigil-web`. It has been
run end to end against the stand-ins in `dev/`, and its writes and history
queries against TimescaleDB 2.30.2. Its port of the schema setup has not yet
run against a real TimescaleDB, and it has not yet been deployed to the host.

## vigil-core

`vigil-core` (`core/`) is an ASP.NET Core application. One loop owns all
state, as in `vigild`: the pollers and the live stream's subscribers only
send it messages, so there are no locks and events are handled in the order
they happened.

- It keeps the one connection to `vigild`'s socket and one to NUT's `upsd`
  over TCP, polling `LIST VAR` every 5s like `upsmon`. Reads on upsd are
  anonymous, so it needs no NUT account. It only watches: `upsmon` on the
  host still owns the shutdown.
- It writes history to Postgres with the TimescaleDB extension, and owns the
  schema: tables, hypertables, continuous aggregates and policies are applied
  idempotently each time it connects ([ADR-007](docs/DECISIONS.md)). It only
  inserts raw samples (every 10s); TimescaleDB's own jobs do the rest:
  continuous aggregates roll them up per minute (`*_1m`) and per hour
  (`*_1h`), raw chunks are compressed after 7 days and dropped after 30,
  minute rollups after a year, and hourly ones are kept forever. The
  extension has to be created in the database by a superuser first;
  vigil-core checks for it and retries until it is there. The first fill of
  the rollups over existing history can take minutes, once per database.
  A drive's temperature is stored twice: as `drive:<wwn>`, the disk's own
  trend wherever it's plugged in, and as `port:<by-path>`, the bay's trend
  whichever disk is in it. `bay_occupants` records which disk sat in which
  bay and when.
- It owns alerting: stalled fans, fans not under `vigild`'s control,
  unreadable or vanished sensors, SMART changes, `vigild` outages, and the UPS
  on battery, low, in forced shutdown, needing a battery, overloaded or not
  protecting. A condition must hold for three polls before it is raised and
  be gone for three before it clears; UPS power events count on the first.
  Events go to Postgres and, optionally, ntfy over HTTPS.
- Without a database the live view still works, and it retries the database
  every 15 seconds.

Its HTTP API, all `GET`:

| Route | What it serves |
|---|---|
| `/` and its assets | the dashboard, from `WEB_ROOT` |
| `/api/live` | Server-Sent Events: the current state on connect, then every change (snapshots, `vigild` reachability, drives, the UPS, new events). A named `keepalive` event after 20s of quiet. At most 64 at once; `503` past that |
| `/api/history?range=` | chart history for `1h`, `6h`, `24h`, `7d`, `30d` or `1y`, bucketed in SQL. Each range reads the coarsest level that still has its resolution: raw samples up to 6 hours, minute rollups up to 7 days, hourly ones beyond. `503` without a database |
| `/api/events` | the newest 50 events, `[]` without a database |
| `/health` | `ok` once running; says nothing about the database, which the live view does not need |

It is configured entirely from the environment (`/etc/vigil-core.env` in
production, from `core/deploy/vigil-core.env`). Every value is validated
before it starts, with every problem listed:

| Variable | Default | |
|---|---|---|
| `VIGILD_SOCKET` | | `vigild`'s socket, e.g. `/mnt/vigil/vigild.sock` |
| `VIGILD_URL` | | `host:port` of a TCP stand-in instead; development only |
| `DATABASE_URL` | `postgres://vigil@localhost/vigil` | the schema owner |
| `DATABASE_URL_READONLY` | the same as `DATABASE_URL` | the `SELECT`-only role for history and the event log |
| `PERSIST_INTERVAL_SECONDS` | `10` | how often a raw sample is written |
| `RAW_RETENTION_DAYS` | `30` | how long raw samples are kept |
| `NUT_HOST` | | upsd's address; unset leaves the UPS out rather than showing it broken |
| `NUT_PORT` | `3493` | |
| `NUT_UPS` | `apc` | the UPS's name on upsd |
| `NUT_POLL_SECONDS` | `5` | |
| `NTFY_URL` | | an ntfy topic URL; unset sends no notifications |
| `CORE_HOST` | `0.0.0.0` | the one address it listens on; an IP address or `localhost` |
| `CORE_PORT` | `3001` | the production template sets `3000` |
| `DAEMON_LOST_AFTER_SECONDS` | `20` | how long `vigild` must be unreachable before that is an event |
| `WEB_ROOT` | | the dashboard's build; unset serves the API only, as in development |

## The dashboard

`web/` is a SvelteKit app built with `adapter-static` into one prerendered
page, which vigil-core serves. It has no server of its own: everything comes
from vigil-core's `/api/*`, so the browser opens the live stream, fetches the
event log and asks for history as the charts need it. Its script policy is a
content security policy in the page itself, carrying the hash of each inline
script SvelteKit emits; vigil-core adds the policies only a header can carry.

## Development

Without the hardware, `dev/mock-vigild.mjs` and `dev/mock-upsd.mjs` stand in
for `vigild` and upsd (type a letter and Enter to inject a fault), and
`dev/docker-compose.yml` runs Postgres with TimescaleDB, the same versions as
production. `core/.env.example` points vigil-core at all three:

```bash
docker compose -f dev/docker-compose.yml up -d
node dev/mock-vigild.mjs & node dev/mock-upsd.mjs &
cp core/.env.example core/.env       # once; core/.env is ignored by git
set -a; . core/.env; set +a; dotnet run --project core/Vigil.Core
cd web && npm run dev
```

`npm run dev` serves the page with hot reload and proxies `/api` and
`/health` to vigil-core at `VIGIL_CORE_URL` (default
`http://127.0.0.1:3001`), so the page talks to it exactly as in production.
To try the built page instead, run `npm run build` in `web/` and start
vigil-core with `WEB_ROOT=web/build`.

## License

AGPL-3.0-or-later. See [LICENSE](LICENSE).
