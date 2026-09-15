#!/bin/bash
# Walks every pwmN channel on the board's Super I/O chip (nct6798 on this board),
# ramping each one in isolation and reading back the tach so the physical header
# behind each pwmN can be identified by ear/eye. Restores pwmN_enable=5 (BIOS
# Smart Fan IV) after every channel, including on interrupt. Ends by printing a
# ready-to-paste FanControl.Channels block for appsettings.json.
#
# Run this ON THE HOST as root:  bash walk-fan-headers.sh
set -euo pipefail

CHIP_DIR=""
for d in /sys/class/hwmon/hwmon*; do
    if [[ "$(cat "$d/name" 2>/dev/null)" == "nct6798" ]]; then
        CHIP_DIR="$d"
        break
    fi
done

if [[ -z "$CHIP_DIR" ]]; then
    echo "No nct6798 hwmon chip found. Is nct6775 loaded? (modprobe -a nct6775)" >&2
    exit 1
fi

echo "Using chip directory: $CHIP_DIR"
echo

CURRENT_MANUAL_CHANNEL=""

restore_current() {
    if [[ -n "$CURRENT_MANUAL_CHANNEL" ]]; then
        echo 5 > "$CHIP_DIR/pwm${CURRENT_MANUAL_CHANNEL}_enable" 2>/dev/null || true
        echo "  (restored pwm${CURRENT_MANUAL_CHANNEL} to auto)"
        CURRENT_MANUAL_CHANNEL=""
    fi
}
trap restore_current EXIT INT TERM

declare -a RESULT_N RESULT_LABEL

for n in 1 2 3 4 5 6 7; do
    pwm="$CHIP_DIR/pwm${n}"
    enable="$CHIP_DIR/pwm${n}_enable"
    tach="$CHIP_DIR/fan${n}_input"

    if [[ ! -f "$pwm" || ! -f "$enable" || ! -f "$tach" ]]; then
        continue
    fi

    baseline_rpm=$(cat "$tach")

    echo
    echo "--- pwm${n}: watch/listen to the case now ---"
    read -r -p "Press Enter to ramp this channel to full speed... "

    echo 1 > "$enable"
    CURRENT_MANUAL_CHANNEL="$n"

    echo 255 > "$pwm"
    sleep 4
    ramped_rpm=$(cat "$tach")
    read -r -p "At full speed now (tach=${ramped_rpm} rpm, baseline was ${baseline_rpm} rpm). Note which fan changed, then press Enter to drop to idle... "

    echo 60 > "$pwm"
    sleep 4

    echo 5 > "$enable"
    CURRENT_MANUAL_CHANNEL=""
    echo "pwm${n} restored to auto."

    if [[ "$ramped_rpm" -gt $((baseline_rpm + 200)) || ( "$baseline_rpm" -eq 0 && "$ramped_rpm" -gt 200 ) ]]; then
        default_hint="responded"
    else
        default_hint="unwired/no response"
    fi

    read -r -p "Label for pwm${n} (e.g. cpu, case-rear; blank to skip as unwired) [${default_hint}]: " label
    if [[ -n "$label" ]]; then
        RESULT_N+=("$n")
        RESULT_LABEL+=("$label")
    fi
done

echo
echo "=== Identified channels ==="
for i in "${!RESULT_N[@]}"; do
    echo "  pwm${RESULT_N[$i]} -> ${RESULT_LABEL[$i]}"
done

echo
echo "=== Paste into appsettings.json under FanControl.Channels ==="
echo "["
for i in "${!RESULT_N[@]}"; do
    comma=","
    if [[ "$i" -eq $((${#RESULT_N[@]} - 1)) ]]; then
        comma=""
    fi
    cat <<EOF
  {
    "Id": "${RESULT_LABEL[$i]}",
    "ChipName": "nct6798",
    "Index": ${RESULT_N[$i]},
    "MinimumDutyPercent": 20
  }${comma}
EOF
done
echo "]"
