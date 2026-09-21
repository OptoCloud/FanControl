#!/bin/sh
# Hands every fan header still on manual PWM back to the BIOS's automatic curve.
#
# Wired up as fancontrol.service's ExecStopPost, which systemd runs after the daemon is
# gone no matter HOW it went: clean stop, crash, SIGKILL, SIGBUS, OOM kill. The daemon's
# own FanSafetyGuard covers every exit it gets a chance to react to, but nothing
# in-process can cover being killed outright, and manual PWM is sticky in the chip: the
# header would hold its last duty forever. This is that external backstop.
#
# After a clean stop the daemon has already released everything, so this finds nothing on
# manual and does nothing. Only headers reading pwmN_enable=1 (manual) are touched, and
# they are set to 5 (Smart Fan IV), the mode this board ships in.
#
# Usage: release-fans.sh [chip_name_glob]     (default: nct67*)
# Run as root. Safe to run by hand at any time the daemon isn't running.

CHIP_GLOB="${1:-nct67*}"

for chip in /sys/class/hwmon/hwmon*; do
    name=$(cat "$chip/name" 2>/dev/null) || continue
    case "$name" in
        $CHIP_GLOB) ;;
        *) continue ;;
    esac

    for enable in "$chip"/pwm[0-9]*_enable; do
        [ -f "$enable" ] || continue
        if [ "$(cat "$enable" 2>/dev/null)" = "1" ]; then
            if echo 5 > "$enable" 2>/dev/null; then
                echo "release-fans: $enable was still on manual, released to automatic (5)."
            else
                echo "release-fans: FAILED to release $enable, it is still on manual." >&2
            fi
        fi
    done
done

# Never fail the unit's stop over this: there is nothing systemd could do about it anyway.
exit 0
