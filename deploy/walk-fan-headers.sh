#!/bin/bash
# Walks every pwmN channel on the board's Super I/O chip (nct6798 on this board),
# ramping each one in isolation and reading back the tach so the physical header
# behind each pwmN can be identified by ear/eye. Restores each channel's *exact*
# original pwmN_enable mode and pwmN duty afterward (not a hardcoded "back to
# auto") — a channel already sitting in manual mode before this script ran (as
# pwm6 was observed to be, unwired, on this board) is put back exactly as found,
# not switched into BIOS auto. Same restoration runs on Ctrl-C/kill via the trap.
# Ends by printing a ready-to-paste FanControl.Channels block for appsettings.json.
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
declare -A ORIG_ENABLE ORIG_PWM

restore_current() {
    if [[ -n "$CURRENT_MANUAL_CHANNEL" ]]; then
        local n="$CURRENT_MANUAL_CHANNEL"
        # Duty first, then mode: if the original mode was manual, this puts the
        # exact original duty back before re-arming manual mode; if it was auto,
        # the duty write is harmless since auto immediately recomputes it anyway.
        echo "${ORIG_PWM[$n]}" > "$CHIP_DIR/pwm${n}" 2>/dev/null || true
        echo "${ORIG_ENABLE[$n]}" > "$CHIP_DIR/pwm${n}_enable" 2>/dev/null || true
        echo "  (restored pwm${n} to its original mode=${ORIG_ENABLE[$n]}, duty=${ORIG_PWM[$n]})"
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
    ORIG_ENABLE[$n]=$(cat "$enable")
    ORIG_PWM[$n]=$(cat "$pwm")

    echo
    echo "--- pwm${n}: watch/listen to the case now (original mode=${ORIG_ENABLE[$n]}, duty=${ORIG_PWM[$n]}) ---"
    read -r -p "Press Enter to ramp this channel to full speed... "

    echo 1 > "$enable"
    CURRENT_MANUAL_CHANNEL="$n"

    echo 255 > "$pwm"
    sleep 4
    ramped_rpm=$(cat "$tach")
    read -r -p "At full speed now (tach=${ramped_rpm} rpm, baseline was ${baseline_rpm} rpm). Note which fan changed, then press Enter to restore... "

    # Exact restore, same as the trap: duty first, then mode.
    echo "${ORIG_PWM[$n]}" > "$pwm"
    echo "${ORIG_ENABLE[$n]}" > "$enable"
    CURRENT_MANUAL_CHANNEL=""
    echo "pwm${n} restored to its original mode=${ORIG_ENABLE[$n]}, duty=${ORIG_PWM[$n]}."

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
