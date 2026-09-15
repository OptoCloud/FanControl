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
  its chip name (`k10temp`, `nct6798`, `drivetemp`, `jc42`) at startup, and
  drive sensors are further identified by their backing block device
  (`drive:sda`, not `hwmon7`).
- **Only whitelisted sensors are read.** Unconnected/floating hwmon inputs
  (`AUXTIN0/1/2`, `CPUTIN` on this board) are never touched.
- **Fan headers are addressed by verified mapping, not guesswork.** There is
  no documented `pwmN` → silkscreen header table for this board; the mapping
  in config must be established by walking each channel in manual mode and
  watching which tach responds.
- **Manual PWM is never left dangling.** Taking a channel to manual mode is
  paired with a safety guard that releases it back to BIOS Smart Fan IV on
  clean shutdown *and* via an independent deadman timer if the control loop
  ever stalls without crashing outright.
- **Status is exposed read-only, loopback-only.** The daemon binds its
  `/status` JSON endpoint to `127.0.0.1` — it is never the thing exposed to
  the internet. A separate, existing public API/dashboard polls that
  endpoint locally, caches, and streams updates to browsers.

## Project layout

- `src/FanControl.Core` — sensor discovery, PWM control, and curve logic,
  built against an `ISysFs` abstraction so it's unit-testable without a real
  Linux `/sys` tree.
- `src/FanControl.Daemon` — the ASP.NET Core minimal-API host that runs the
  control loop and serves `/status`.
- `tests/FanControl.Core.Tests` — xUnit tests against a fake in-memory sysfs.

## Deployment

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

scp -r publish/* root@proxmox-host:/opt/fancontrol/
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
gets a real chance to release every channel back to BIOS control on stop —
never `systemctl kill -s SIGKILL` this service while `Channels` is non-empty.

## Status

Early scaffold. Not yet resolved:

- The `LSI SAS9300-8i` has no hwmon temperature exposure in mainline
  `mpt3sas` — reading it requires an ioctl to `/dev/mpt3ctl` (as
  `lsiutil`/`storcli` do). Stubbed as unavailable for now.
- The `pwmN` → physical fan header mapping has not yet been verified on this
  board. `FanControl.Channels`/`FanControl.Curves` in config are empty until
  it is.

## License

AGPL-3.0-or-later. See [LICENSE](LICENSE).
