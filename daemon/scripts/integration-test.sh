#!/bin/bash
# End-to-end test of the real Linux binary against a fake sysfs tree, with stand-in
# nvidia-smi/smartctl scripts. Exercises what unit tests can't: the unix socket API,
# several simultaneous event-stream consumers, real fan writes, and the fans being handed
# back on SIGTERM. Needs no hardware and no root; runs anywhere with bash + curl (WSL, CI).
#
# Usage: integration-test.sh <path-to-fancontrol-binary>
set -uo pipefail

BINARY="$(realpath "${1:?Usage: integration-test.sh <path-to-fancontrol-binary>}")"
WORK="$(mktemp -d)"
DAEMON_PID=""
FAILURES=0

cleanup() {
    [[ -n "$DAEMON_PID" ]] && kill -9 "$DAEMON_PID" 2>/dev/null
    rm -rf "$WORK"
}
trap cleanup EXIT

check() {
    local description="$1" actual="$2" expected="$3"
    if [[ "$actual" == "$expected" ]]; then
        echo "  ok    $description"
    else
        echo "  FAIL  $description: expected '$expected', got '$actual'"
        FAILURES=$((FAILURES + 1))
    fi
}

# The binary may live on a filesystem that can't mark it executable (a Windows drive under WSL).
cp "$BINARY" "$WORK/fancontrol" && chmod +x "$WORK/fancontrol"

SYS="$WORK/sys"
CHIP="$SYS/class/hwmon/hwmon2"
mkdir -p "$CHIP" "$SYS/class/hwmon/hwmon0" "$SYS/class/hwmon/hwmon5/device/block/sda" "$SYS/class/block/sda/device" "$WORK/bin"

echo nct6798 > "$CHIP/name"
echo SYSTIN > "$CHIP/temp1_label"; echo 31000 > "$CHIP/temp1_input"
for i in 1 2; do echo 0 > "$CHIP/pwm$i"; echo 5 > "$CHIP/pwm${i}_enable"; echo 900 > "$CHIP/fan${i}_input"; done

echo k10temp > "$SYS/class/hwmon/hwmon0/name"
echo Tctl > "$SYS/class/hwmon/hwmon0/temp1_label"; echo 60000 > "$SYS/class/hwmon/hwmon0/temp1_input"

echo drivetemp > "$SYS/class/hwmon/hwmon5/name"
echo 40000 > "$SYS/class/hwmon/hwmon5/temp1_input"
echo naa.5000c500aaaa0001 > "$SYS/class/block/sda/device/wwid"

printf '#!/bin/sh\necho 70\n' > "$WORK/bin/nvidia-smi"
printf '#!/bin/sh\necho '"'"'{"smart_status":{"passed":true},"ata_smart_attributes":{"table":[{"id":5,"raw":{"value":7}}]},"power_on_time":{"hours":100}}'"'"'\n' > "$WORK/bin/smartctl"
chmod +x "$WORK/bin/nvidia-smi" "$WORK/bin/smartctl"

SOCKET="$WORK/run/fancontrol.sock"
cat > "$WORK/fancontrol.toml" <<EOF
poll_interval_secs = 0.2
deadman_timeout_secs = 5
sysfs_root = "$SYS"

[api]
socket_path = "$SOCKET"
max_clients = 3

[gpu]
nvidia_smi_path = "$WORK/bin/nvidia-smi"

[hba]
enabled = false

[drive_health]
smartctl_path = "$WORK/bin/smartctl"

[[channels]]
id = "cpu-fan"
chip_name = "nct6798"
index = 1
minimum_duty_percent = 30

[[channels]]
id = "intake"
chip_name = "nct6798"
index = 2

[[curves]]
fan_channel_id = "cpu-fan"
sensor_ids = ["cpu"]
points = [[30, 20], [50, 50], [70, 100]]

[[curves]]
fan_channel_id = "intake"
sensor_ids = ["gpu", "drive:*"]
points = [[30, 20], [50, 50], [70, 100]]
EOF

api() { curl -s --max-time 5 --unix-socket "$SOCKET" "http://localhost$1"; }
field() { python3 -c "import json,sys; s=json.load(sys.stdin); print($1)"; }

echo "config check"
"$WORK/fancontrol" --config "$WORK/fancontrol.toml" --check > /dev/null; check "--check accepts the config" "$?" "0"
sed 's/index = 2/index = 1/' "$WORK/fancontrol.toml" > "$WORK/bad.toml"
"$WORK/fancontrol" --config "$WORK/bad.toml" --check > /dev/null 2>&1; check "--check rejects two channels on one header" "$?" "1"
check "a rejected config touches no fan" "$(cat "$CHIP/pwm1_enable")" "5"

echo "startup"
"$WORK/fancontrol" --config "$WORK/fancontrol.toml" 2> "$WORK/daemon.log" &
DAEMON_PID=$!
for _ in $(seq 50); do [[ -S "$SOCKET" ]] && break; sleep 0.1; done
sleep 1
check "daemon is running" "$(kill -0 "$DAEMON_PID" 2>/dev/null && echo yes)" "yes"
check "socket mode is 0660" "$(stat -c %a "$SOCKET")" "660"

echo "fan control"
check "pwm1 taken to manual" "$(cat "$CHIP/pwm1_enable")" "1"
check "pwm1 duty follows cpu at 60C (75% = 191)" "$(cat "$CHIP/pwm1")" "191"
check "pwm2 duty follows the hotter of gpu 70C / drive 40C (100% = 255)" "$(cat "$CHIP/pwm2")" "255"

echo "GET /status"
STATUS="$(api /status)"
check "loop healthy" "$(echo "$STATUS" | field 's["controlLoopHealthy"]')" "True"
check "sensor ids" "$(echo "$STATUS" | field '",".join(sorted(x["id"] for x in s["sensors"]))')" "board,cpu,drive:naa.5000c500aaaa0001,gpu,hba"
check "fan mode is a string" "$(echo "$STATUS" | field 's["fans"][0]["mode"]')" "manual"
check "drive health keyed by WWN" "$(echo "$STATUS" | field 's["driveHealth"][0]["deviceName"]')" "naa.5000c500aaaa0001"
check "drive health parsed" "$(echo "$STATUS" | field 's["driveHealth"][0]["reallocatedSectorCount"]')" "7"
check "unknown path is 404" "$(curl -s -o /dev/null -w '%{http_code}' --unix-socket "$SOCKET" http://localhost/nope)" "404"
check "POST is 405" "$(curl -s -o /dev/null -w '%{http_code}' -X POST --unix-socket "$SOCKET" http://localhost/status)" "405"

echo "GET /events, three consumers at once"
for n in 1 2 3; do curl -sN --max-time 2 --unix-socket "$SOCKET" http://localhost/events > "$WORK/events$n" & done
sleep 0.5
check "a fourth connection over max_clients is refused" "$(curl -s -o /dev/null -w '%{http_code}' --unix-socket "$SOCKET" http://localhost/status)" "503"
wait $(jobs -p | grep -v "^$DAEMON_PID$") 2>/dev/null
for n in 1 2 3; do
    COUNT="$(grep -c '^event: status' "$WORK/events$n")"
    check "consumer $n received a stream of events (got $COUNT)" "$([[ "$COUNT" -ge 5 ]] && echo yes)" "yes"
done
# A consumer's departure is only noticed when the next event is written to it.
sleep 0.5
check "slots are freed once consumers leave" "$(curl -s -o /dev/null -w '%{http_code}' --unix-socket "$SOCKET" http://localhost/status)" "200"

echo "reacts to a temperature change"
echo 30000 > "$SYS/class/hwmon/hwmon0/temp1_input"
sleep 1
check "pwm1 drops to the channel minimum (30% = 77), not the curve's 20%" "$(cat "$CHIP/pwm1")" "77"

echo "unreadable sensor fails safe"
rm "$SYS/class/hwmon/hwmon0/temp1_input"
sleep 1
check "pwm1 goes to the fail-safe duty (100% = 255)" "$(cat "$CHIP/pwm1")" "255"

echo "SIGTERM"
kill -TERM "$DAEMON_PID"
for _ in $(seq 50); do kill -0 "$DAEMON_PID" 2>/dev/null || break; sleep 0.1; done
wait "$DAEMON_PID"; EXIT_CODE=$?
DAEMON_PID=""
check "clean exit code" "$EXIT_CODE" "0"
check "pwm1 handed back to its original mode" "$(cat "$CHIP/pwm1_enable")" "5"
check "pwm2 handed back to its original mode" "$(cat "$CHIP/pwm2_enable")" "5"
check "socket removed" "$([[ -e "$SOCKET" ]] && echo present || echo gone)" "gone"

echo "missing chip"
rm "$CHIP/name"
"$WORK/fancontrol" --config "$WORK/fancontrol.toml" 2> /dev/null; check "exits non-zero so systemd restarts it" "$?" "1"

echo
if [[ "$FAILURES" -eq 0 ]]; then
    echo "All checks passed."
else
    echo "$FAILURES check(s) FAILED. Daemon log:"; cat "$WORK/daemon.log"
    exit 1
fi
