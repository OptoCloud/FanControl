# fancontrol

A sensor-aware fan control daemon for a Linux (Proxmox) host, built because
BIOS Smart Fan curves have no idea what an LSI HBA, a drive array, or a GPU
are actually doing.

## Why

The stock ASRock B550 Pro4 / NCT6798D Smart Fan curves only see board and CPU
temperatures. This host also carries:

- an LSI SAS9300-8i HBA (`mpt3sas`) driving 8 drives
- 13 drives total (8 HBA + 5 SATA), each exposed individually via `drivetemp`
- an RTX 3060 (`nvidia-smi`)
- 2 DIMMs via `jc42`

None of that feeds the BIOS curve. This daemon reads all of it and drives the
board's PWM headers directly.

It is a single static Rust binary of about 1 MB with no runtime. (It started
life as a .NET daemon; that implementation is in the git history.)

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
  the curve still runs but that fail-safe becomes its floor.
- **A sensor's kind doesn't decide which fan sees it; its physical location
  does.** `drive:*` is a sensor *category*, not a location: on this board two
  SSDs sit screwed to the case in the top-left compartment with the LSI card,
  GPU and CPU, nowhere near the drive cage. `zones` group sensor ids by where
  they physically sit, and a curve names zones (`zones = [...]`) instead of,
  or alongside, raw `sensor_ids`. A sensor named explicitly in one zone's
  `sensor_ids` is claimed by it and excluded from every other zone's `drive:*`
  wildcard, so the drive-cage curve's zone stops seeing the SSDs the moment
  they're listed in the component-bay zone instead, with no drive-cage config
  change needed.
- **One failing channel doesn't take the others down.** A header whose sysfs
  write fails is handed back to automatic control and retried every poll,
  while the remaining channels keep being driven.
- **Config is validated before any fan is touched.** Out-of-range duties,
  unsorted curve points, a curve naming an unknown channel, or a channel with
  no curve (or two) all refuse to start with every problem listed.
  `fancontrol --check` does only that and exits.
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
curl    --unix-socket /run/fancontrol/fancontrol.sock http://localhost/status
curl -N --unix-socket /run/fancontrol/fancontrol.sock http://localhost/events
```

- `GET /status`: the latest snapshot as JSON (`503` before the first poll).
- `GET /events`: Server-Sent Events. One `status` event per poll carrying the
  same JSON, starting with the current snapshot immediately on connect. A
  `: keepalive` comment is sent when nothing was published for 15 seconds.

SSE rather than WebSocket because data only flows one way: it is plain HTTP
and clients reconnect on their own. From Node:
`http.request({ socketPath, path: '/events' })`. Simultaneous connections are
capped by `api.max_clients`; past that, new ones get `503`.

```jsonc
{
  "timestampUtc": "2026-09-21T23:01:34.155Z",
  // false if a channel couldn't be driven this poll, or if this snapshot is
  // older than the deadman timeout (the loop has hung)
  "controlLoopHealthy": true,
  "sensors": [
    { "id": "cpu", "category": "cpu", "label": "Tctl", "celsiusOrNull": 38.25, "isAvailable": true, "sourcePath": "..." },
    { "id": "drive:naa.5000c500...", "category": "drive", "...": "..." }
    // category: cpu, boardAmbient, drive, gpu, memory, hba
  ],
  "fans": [
    { "id": "drive-cage", "dutyPercent": 65, "rpm": 1211, "mode": "manual", "stalled": false }
    // mode: disabled, manual, thermalCruise, speedCruise, smartFanIII, smartFanIV, or null if unreadable
  ],
  "driveHealth": [
    { "deviceName": "naa.5000c500...", "passed": true, "reallocatedSectorCount": 0, "pendingSectorCount": 0,
      "powerOnHours": 8760, "isAvailable": true, "asOf": "2026-09-21T23:00:38.412Z", "sourcePath": "/dev/sdc" }
    // Exactly what the last SMART poll saw. A sleeping drive is not woken, so it shows
    // isAvailable=false with nulls: keep its previous values on the consumer side.
    // pendingSectorCount is null on drives that don't report attribute 197 (most SSDs).
  ]
}
```

## Project layout

- `daemon/`: the Rust daemon. Hardware-independent logic (curves, config
  validation, the HBA wire format, SMART parsing, the safety guard, the
  control loop itself) is separated from the thin platform plumbing around it
  and unit-tested against an in-memory sysfs.
- `deploy/`: systemd unit, config, `tmpfiles.d` and `modules-load.d` entries,
  `release-fans.sh`, and the header-mapping helper scripts.

## Build and test

Developed and unit-tested on Windows, runs only on Linux. No cross toolchain
is needed: the musl target ships its own C runtime and links with Rust's
bundled LLD.

```bash
cd daemon
cargo test                                                   # unit tests, any OS
rustup target add x86_64-unknown-linux-musl                  # once
cargo build --release --target x86_64-unknown-linux-musl     # static Linux binary
```

`daemon/scripts/integration-test.sh <binary>` runs that real Linux binary end
to end against a fake sysfs tree with stand-in `nvidia-smi`/`smartctl`: the
socket API, several simultaneous event consumers, real fan writes, fail-safe
on a vanished sensor, and the fans being handed back on SIGTERM. It needs no
hardware and no root, so it runs under WSL or in CI:

```powershell
wsl -e bash /mnt/e/path/to/daemon/scripts/integration-test.sh /mnt/e/path/to/daemon/target/x86_64-unknown-linux-musl/release/fancontrol
```

Neither covers the `/dev/mpt3ctl` ioctl, real `nvidia-smi`/`smartctl` output
or the systemd integration. Those were verified on the target host (see
Status) and need re-verifying there after changes to them.

## Deployment

**Host prerequisite:** drive SMART health shells out to `smartctl` (package
`smartmontools`), not installed by default on a minimal Proxmox/Debian host:
`apt-get install -y smartmontools`. Without it every drive's health is simply
reported unavailable.

| File | Goes to |
|---|---|
| `daemon/target/x86_64-unknown-linux-musl/release/fancontrol` | `/opt/fancontrol/fancontrol` (`chmod +x`) |
| `deploy/release-fans.sh` | `/opt/fancontrol/release-fans.sh` |
| `deploy/fancontrol.toml` | `/etc/fancontrol/fancontrol.toml` |
| `deploy/fancontrol.service` | `/etc/systemd/system/fancontrol.service` |
| `deploy/tmpfiles.d/fancontrol.conf` | `/etc/tmpfiles.d/fancontrol.conf` |
| `deploy/modules-load.d/fancontrol.conf` | `/etc/modules-load.d/fancontrol.conf` |

**Stop the service before overwriting the binary, every time.** Linux
memory-maps the running executable from disk; overwriting it in place while
the old process is still executing out of it gets that process killed with
SIGBUS the next time it faults in a code page.

```bash
systemctl stop fancontrol.service
# ... copy the files ...
chmod +x /opt/fancontrol/fancontrol
systemd-tmpfiles --create /etc/tmpfiles.d/fancontrol.conf
modprobe -a nct6775 drivetemp   # -a is required: `modprobe a b` without it loads
                                 # only `a`, treating `b` as a module parameter
/opt/fancontrol/fancontrol --config /etc/fancontrol/fancontrol.toml --check
systemctl daemon-reload
systemctl enable --now fancontrol.service
journalctl -u fancontrol -f
```

`channels` and `curves` in `fancontrol.toml` are specific to one board and its
wiring. Leave both empty to run monitor-only, which never writes to any `pwmN`
file.

`zones` (optional) group sensor ids by physical location so a curve can react
to "everything in this part of the case" instead of listing chip-level
categories that don't say where a device actually sits. On orion, the two
Samsung 870 EVO SSDs are `drivetemp` sensors like every HDD but live in the
component compartment, not the drive cage, so they're pulled out of the
drive-bay zone's `drive:*` wildcard into their own zone:

```toml
[[zones]]
id = "drive-bay"
sensor_ids = ["drive:*"]

[[zones]]
id = "component-bay"
sensor_ids = ["hba", "drive:naa.5002538...ssd1", "drive:naa.5002538...ssd2"]
```

A curve then writes `zones = ["drive-bay"]` instead of `sensor_ids =
["drive:*"]`, and gets every drive *except* the two SSDs automatically:
naming a sensor explicitly in `component-bay` removes it from `drive-bay`'s
wildcard, wherever `drive-bay` is used. Find a drive's WWN with
`ls -l /dev/disk/by-id/ | grep ata-Samsung_SSD` (or `by-id/wwn-*`) on the
host; it's the same id the `/status` API reports as `drive:<wwn>`.

`fancontrol.service` runs as root (sysfs PWM attributes are root-owned, mode
644). On stop it relies on `TimeoutStopSec=30` + `SIGTERM` so the daemon
releases every channel itself. If it dies without that chance, `ExecStopPost`
runs `release-fans.sh`, which hands any header still on manual back to Smart
Fan IV; it can also be run by hand (`sh /opt/fancontrol/release-fans.sh`)
whenever the daemon isn't running.

One thing still defeats that: `systemctl kill -s SIGKILL fancontrol` signals
the whole unit by default, which kills the `ExecStopPost` script along with
the daemon and leaves every header on manual until the automatic restart takes
them over again. With `--kill-whom=main` (which is what a real crash, SIGBUS or
OOM kill looks like) the script runs and releases everything within a second.
Never SIGKILL the whole unit.

### A consumer in an unprivileged LXC

Bind-mount the socket's *directory* (the socket file itself is recreated on
every start):

```bash
pct set <vmid> -mp0 /run/fancontrol,mp=/run/fancontrol
```

and give the socket to the host uid the container's root maps to, in
`fancontrol.toml`:

```toml
[api]
socket_uid = 100000
socket_gid = 100000
```

The directory comes from `tmpfiles.d` rather than the unit's
`RuntimeDirectory=` on purpose: systemd recreates a runtime directory on every
service restart, which gives it a new inode and silently breaks the
container's bind mount.

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

The dashboard (`dashboard/`) is a SvelteKit app run under Node in an
unprivileged LXC. It keeps the one connection to the daemon's socket, writes
history to Postgres (10s samples for 7 days, 1-minute rollups forever), fans
live data out to browsers over SSE, and owns alerting (stalled fans, unreadable
or vanished sensors, SMART changes, daemon outages; optional ntfy push). See
`dashboard/deploy/` for the unit and env template, `dashboard/deploy/package.sh`
to build the deployable tarball, and `dashboard/scripts/mock-daemon.mjs` plus
`dashboard/docker-compose.dev.yml` for developing without the hardware.

## License

AGPL-3.0-or-later. See [LICENSE](LICENSE).
