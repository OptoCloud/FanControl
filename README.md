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

## How it works

- **Sensors are resolved by name, never by hwmon path.** `hwmonN` numbering
  shifts across reboots and module load order, so every sensor is found by
  its chip name (`k10temp`, `nct6798`, `drivetemp`, `jc42`), and drive
  sensors are keyed by the drive's WWN (`drive:<wwn>`), since even
  the `sdX` letter isn't stable. The hwmon tree is re-scanned periodically
  (`SensorRescanInterval`, default 30s), so a drive that resets or is
  hot-swapped is picked back up without a restart.
- **Only whitelisted sensors are read.** Unconnected/floating hwmon inputs
  (`AUXTIN0/1/2`, `CPUTIN` on this board) are never touched.
- **Fan headers are addressed by verified mapping, not guesswork.** There is
  no documented `pwmN` → silkscreen header table for this board; the mapping
  in config must be established by walking each channel in manual mode and
  watching which tach responds.
- **Manual PWM is never left dangling.** Three layers: a safety guard
  releases every channel back to the automatic mode it was found in (BIOS
  Smart Fan IV here) on clean shutdown; an independent, re-arming deadman
  timer does the same if the control loop ever stalls without crashing; and
  `deploy/release-fans.sh`, run by systemd as `ExecStopPost`, covers the
  exits nothing in-process can react to (SIGKILL, SIGBUS, OOM kill).
- **An unreadable sensor is never treated as "cold".** If every sensor behind
  a curve is unavailable the fan runs at the curve's `FailSafeDutyPercent`;
  if only some explicitly named sensor is (say `gpu` on a `gpu`+`hba`
  curve), the curve still runs but that fail-safe becomes its floor.
- **Config is validated before any fan is touched.** Out-of-range duties,
  unsorted curve points, a curve naming an unknown channel, or a channel
  with no curve (or two) all refuse to start with every problem listed,
  rather than surfacing as a poll that fails every 2 seconds.
- **Fan stalls are detected.** A header reading 0 RPM for several
  consecutive polls while being driven is logged and flagged as `stalled`
  in `/status`.
- **Status is exposed read-only, loopback-only.** The daemon binds its
  `/status` JSON endpoint to `127.0.0.1` — it is never the thing exposed to
  the internet. A separate, existing public API/dashboard polls that
  endpoint locally, caches, and streams updates to browsers.
- **HBA temperature is read via a raw ioctl.** `mpt3sas` has no hwmon
  exposure for the LSI SAS9300-8i's IOC/board temperature; it's read
  directly from Config Page IO_UNIT_PAGE_7 via `/dev/mpt3ctl` — the same
  mechanism vendor tools like `lsiutil` use, reimplemented from the actual
  kernel driver source rather than a third-party binary.
- **Drive SMART health never wakes a sleeping drive.** Polled on its own
  slow interval (default 15 min), fully decoupled from the 2s fan-control
  loop, using `smartctl -n standby` so a drive already asleep is skipped
  rather than spun up just to answer a health check. A skipped drive keeps
  its last good result (each entry carries an `asOf` timestamp saying how
  old it is). Beyond the overall pass/fail flag, which tends to be the last
  thing to change on a dying drive, new pending sectors and a growing
  reallocated count are logged as warnings.
- **External tools can't hang the daemon.** `nvidia-smi` and `smartctl` both
  run under a hard timeout and are killed if they exceed it.

## Project layout

- `src/FanControl.Core` — sensor discovery, PWM control, and curve logic,
  built against an `ISysFs` abstraction so it's unit-testable without a real
  Linux `/sys` tree.
- `src/FanControl.Daemon` — the ASP.NET Core minimal-API host that runs the
  control loop and serves `/status`.
- `tests/FanControl.Core.Tests` — xUnit tests against a fake in-memory sysfs.

## Deployment

**Host prerequisite:** drive SMART health polling shells out to `smartctl`
(package `smartmontools`), which is not installed by default on a minimal
Proxmox/Debian host: `apt-get install -y smartmontools`. Without it,
`FanControl.DriveHealth.Enabled` still works, it just reports every drive's
health as unavailable — nothing crashes.

Publish a self-contained single-file build on the dev machine (avoids needing
the .NET runtime installed on the Proxmox host):

```bash
dotnet publish src/FanControl.Daemon -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -o publish/
```

**Stop the service before copying anything over, every time — including the first
install.** Linux memory-maps the running executable from disk; overwriting
`/opt/fancontrol/FanControl.Daemon` in place while the old process is still executing
out of that file corrupts its mapping and gets it killed with SIGBUS the next time it
faults in a code page. Harmless if the daemon isn't holding any fan channel in manual
mode at that moment (systemd's `Restart=on-failure` just relaunches it), but SIGBUS
bypasses `FanSafetyGuard`'s graceful-shutdown release path entirely — don't rely on
getting lucky once `Channels` is non-empty.

```bash
ssh root@proxmox-host systemctl stop fancontrol.service

scp -r publish/* root@proxmox-host:/opt/fancontrol/   # includes release-fans.sh
scp deploy/modules-load.d/fancontrol.conf root@proxmox-host:/etc/modules-load.d/
scp deploy/fancontrol.service root@proxmox-host:/etc/systemd/system/
```

On the host:

```bash
chmod +x /opt/fancontrol/FanControl.Daemon
systemctl daemon-reload
modprobe -a nct6775 drivetemp   # -a is required: `modprobe a b` without it loads
                                 # only `a`, treating `b` as a module parameter
systemctl enable --now fancontrol.service
```

Edit `/opt/fancontrol/appsettings.json` (or drop an
`appsettings.Production.json` alongside it) to fill in `FanControl.Channels`
and `FanControl.Curves` once the header mapping is verified — leave both
empty to run monitor-only, which never writes to any `pwmN` file.

Verify:

```bash
journalctl -u fancontrol -f
curl -s http://127.0.0.1:5178/status | jq .
```

`fancontrol.service` runs as root (sysfs PWM attributes are root-owned,
mode 644) and relies on `TimeoutStopSec=30` + `SIGTERM` so `FanSafetyGuard`
gets a real chance to release every channel itself on stop. If the daemon
dies without that chance, `ExecStopPost` runs `release-fans.sh`, which hands
any header still on manual back to Smart Fan IV; it can also be run by hand
(`sh /opt/fancontrol/release-fans.sh`) whenever the daemon isn't running.

One thing still defeats it: `systemctl kill -s SIGKILL fancontrol` signals the
whole unit by default, which kills the `ExecStopPost` script along with the
daemon and leaves every header on manual until the automatic restart takes them
over again. Verified on real hardware; with `--kill-whom=main` (which is what a
real crash, SIGBUS or OOM kill looks like) the script runs and releases
everything within a second. Never SIGKILL the whole unit.

In `/status`, `controlLoopHealthy` goes false when a poll fails, when any
channel couldn't be driven, or when the newest snapshot is older than
`DeadmanTimeout` (a hung loop), so a consumer doesn't need to judge
staleness from `timestampUtc` itself.

## Status

Live and running in production on the target host. Every sensor described
above is real, all six fan headers are under verified curve-driven manual
control, and drive SMART health is polled independently. Known accepted
quirks:

- One fan header (`lsi-cooling`, `pwm7`) has been observed silently
  reverting `pwm7_enable` from Manual back to Disabled sometime after being
  set — not documented by the Linux `nct6775` driver, likely a Super I/O
  chip-level watchdog or board-specific quirk. Worked around by
  re-asserting manual mode every poll rather than only once at startup;
  root cause not otherwise identified.
- `FanControl.Curves` breakpoints are tuned against real observed
  temperatures where possible, but several (especially `lsi-cooling`) are
  still based on limited data — revisit once more real-load history exists.

## License

AGPL-3.0-or-later. See [LICENSE](LICENSE).
