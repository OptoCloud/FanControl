#!/bin/bash
# Pulses one pwmN channel between 0% and 100% duty repeatedly, for positive
# visual/audible identification of which physical fan it drives — a steady
# full-speed ramp can be hard to distinguish from "always was spinning fast";
# a pulsing on/off pattern is unmistakable.
#
# Restores the channel's exact original pwmN_enable mode and duty afterward,
# on both the happy path and Ctrl-C/kill (same approach as walk-fan-headers.sh).
#
# Usage: pulse-fan.sh <index> [cycles] [total_seconds] [chip_name]
#   pulse-fan.sh 1            # pwm1 on nct6798, 5 cycles over 20s (defaults)
#   pulse-fan.sh 1 8 30       # 8 cycles over 30s
#
# Run this ON THE HOST as root.
set -euo pipefail

INDEX="${1:?Usage: pulse-fan.sh <index> [cycles] [total_seconds] [chip_name]}"
CYCLES="${2:-5}"
TOTAL_SECONDS="${3:-20}"
CHIP_NAME="${4:-nct6798}"

CHIP_DIR=""
for d in /sys/class/hwmon/hwmon*; do
    if [[ "$(cat "$d/name" 2>/dev/null)" == "$CHIP_NAME" ]]; then
        CHIP_DIR="$d"
        break
    fi
done

if [[ -z "$CHIP_DIR" ]]; then
    echo "No hwmon chip named '$CHIP_NAME' found." >&2
    exit 1
fi

PWM="$CHIP_DIR/pwm${INDEX}"
ENABLE="$CHIP_DIR/pwm${INDEX}_enable"
TACH="$CHIP_DIR/fan${INDEX}_input"

if [[ ! -f "$PWM" || ! -f "$ENABLE" ]]; then
    echo "pwm${INDEX} not found under $CHIP_DIR" >&2
    exit 1
fi

ORIG_ENABLE=$(cat "$ENABLE")
ORIG_PWM=$(cat "$PWM")
RESTORED=0

restore() {
    if [[ "$RESTORED" -eq 0 ]]; then
        echo "$ORIG_PWM" > "$PWM" 2>/dev/null || true
        echo "$ORIG_ENABLE" > "$ENABLE" 2>/dev/null || true
        RESTORED=1
        echo
        echo "pwm${INDEX} restored to original mode=${ORIG_ENABLE}, duty=${ORIG_PWM}."
    fi
}
trap restore EXIT INT TERM

HALF_PERIOD=$(awk "BEGIN { printf \"%.2f\", $TOTAL_SECONDS / ($CYCLES * 2) }")

echo "Pulsing pwm${INDEX} on $CHIP_DIR: $CYCLES cycles over ${TOTAL_SECONDS}s (original mode=${ORIG_ENABLE}, duty=${ORIG_PWM})."
echo "Watch/listen now."

echo 1 > "$ENABLE"

for ((i = 1; i <= CYCLES; i++)); do
    echo 255 > "$PWM"
    sleep "$HALF_PERIOD"
    echo 0 > "$PWM"
    sleep "$HALF_PERIOD"
    if [[ -f "$TACH" ]]; then
        echo "  cycle $i/$CYCLES done (fan${INDEX}_input now $(cat "$TACH") rpm)"
    else
        echo "  cycle $i/$CYCLES done"
    fi
done
