//! Turns vigild's snapshots and the UPS's readings into discrete events ("drive-cage stalled",
//! "reallocated sectors grew", "on battery"). vigild deliberately reports only the present;
//! noticing that something CHANGED needs memory, and that lives here. Everything in this file
//! is pure, so it is fully unit-tested; runtime.rs wires it to the database and notifications.

use std::collections::{BTreeMap, BTreeSet};
use std::time::Duration;
use vigil_protocol::{DriveHealth, DriveState, PwmMode, SensorCategory, Severity, Snapshot, UpsState};

#[derive(Debug, Clone, PartialEq)]
pub struct NewEvent {
    pub severity: Severity,
    pub kind: String,
    pub message: String,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Condition {
    pub severity: Severity,
    pub message: String,
    /// What to log when it goes away.
    pub cleared: String,
}

pub type Conditions = BTreeMap<String, Condition>;

fn condition(severity: Severity, message: String, cleared: String) -> Condition {
    Condition { severity, message, cleared }
}

/// Every problem visible in one snapshot, keyed by a stable id for the condition.
pub fn conditions_in(snapshot: &Snapshot, known_sensor_ids: &BTreeSet<String>) -> Conditions {
    let mut conditions = Conditions::new();

    if !snapshot.control_loop_healthy {
        conditions.insert(
            "loop-unhealthy".to_owned(),
            condition(
                Severity::Critical,
                "The control loop reports unhealthy: a fan channel could not be driven, or the loop has stopped polling.".to_owned(),
                "The control loop is healthy again.".to_owned(),
            ),
        );
    }

    for fan in &snapshot.fans {
        if fan.stalled {
            conditions.insert(
                format!("fan-stalled:{}", fan.id),
                condition(
                    Severity::Critical,
                    format!("Fan '{}' reads 0 RPM at {}% duty: dead, jammed or unplugged?", fan.id, fan.duty_percent),
                    format!("Fan '{}' is spinning again.", fan.id),
                ),
            );
        }

        if fan.mode != Some(PwmMode::Manual) {
            conditions.insert(
                format!("fan-mode:{}", fan.id),
                condition(
                    Severity::Warning,
                    format!("Fan '{}' is not under vigild's control (pwm mode: {}).", fan.id, fan.mode.map_or("unreadable", mode_name)),
                    format!("Fan '{}' is back under vigild's control.", fan.id),
                ),
            );
        }
    }

    for sensor in &snapshot.sensors {
        // A drive with no temperature is usually just asleep, which is not a problem.
        if !sensor.is_available && sensor.category != SensorCategory::Drive {
            conditions.insert(
                format!("sensor-unavailable:{}", sensor.id),
                condition(
                    Severity::Warning,
                    format!(
                        "Sensor '{}' ({}) is unreadable. Curves that name it run at no less than their fail-safe duty.",
                        sensor.id, sensor.label
                    ),
                    format!("Sensor '{}' is readable again.", sensor.id),
                ),
            );
        }
    }

    let present: BTreeSet<&str> = snapshot.sensors.iter().map(|sensor| sensor.id.as_str()).collect();
    for id in known_sensor_ids {
        if !present.contains(id.as_str()) {
            conditions.insert(
                format!("sensor-missing:{id}"),
                condition(Severity::Warning, format!("Sensor '{id}' has disappeared from the host."), format!("Sensor '{id}' is back.")),
            );
        }
    }

    conditions
}

fn mode_name(mode: PwmMode) -> &'static str {
    match mode {
        PwmMode::Disabled => "disabled",
        PwmMode::Manual => "manual",
        PwmMode::ThermalCruise => "thermalCruise",
        PwmMode::SpeedCruise => "speedCruise",
        PwmMode::SmartFanIII => "smartFanIII",
        PwmMode::SmartFanIV => "smartFanIV",
    }
}

/// Every problem visible in the UPS's state. `unreadable_for` is how long there has been no
/// reading: a single dropped poll or an upsd restart is not worth an event, a lasting gap is.
pub fn ups_conditions(ups: &UpsState, unreadable_for: Duration) -> Conditions {
    let mut conditions = Conditions::new();
    if !ups.enabled {
        return conditions;
    }

    let Some(reading) = &ups.reading else {
        if unreadable_for >= Duration::from_secs(30) {
            conditions.insert(
                "ups-unreadable".to_owned(),
                condition(
                    Severity::Warning,
                    format!(
                        "The UPS can't be read ({}). A power cut would not show up here; orion's own upsmon is unaffected.",
                        ups.error.as_deref().unwrap_or("no reason given")
                    ),
                    "The UPS is readable again.".to_owned(),
                ),
            );
        }
        return conditions;
    };

    let flags: BTreeSet<&str> = reading.status.iter().map(String::as_str).collect();
    let charge = reading.battery_charge.map_or(String::new(), |c| format!(" Battery at {}%", c.round()));
    let runtime = reading.battery_runtime_seconds.map_or(String::new(), |s| format!(", about {} min of runtime left", (s / 60.0).round()));

    if flags.contains("OB") {
        conditions.insert(
            "ups-on-battery".to_owned(),
            condition(
                Severity::Warning,
                format!("Mains power lost: the UPS is on battery.{charge}{runtime}."),
                "Mains power is back: the UPS is online again.".to_owned(),
            ),
        );
    }
    if flags.contains("LB") {
        conditions.insert(
            "ups-low-battery".to_owned(),
            condition(
                Severity::Critical,
                format!("The UPS battery is LOW.{charge}. orion's upsmon shuts the host down on this."),
                "The UPS battery is no longer low.".to_owned(),
            ),
        );
    }
    if flags.contains("FSD") {
        conditions.insert(
            "ups-forced-shutdown".to_owned(),
            condition(
                Severity::Critical,
                "The UPS is in forced shutdown (FSD): the load is about to lose power.".to_owned(),
                "Forced shutdown is over.".to_owned(),
            ),
        );
    }
    if flags.contains("RB") {
        conditions.insert(
            "ups-replace-battery".to_owned(),
            condition(
                Severity::Warning,
                "The UPS reports its battery needs replacing.".to_owned(),
                "The UPS no longer reports a battery to replace.".to_owned(),
            ),
        );
    }
    if flags.contains("OVER") {
        let load = reading.load.map_or(String::new(), |l| format!(" ({}% load)", l.round()));
        conditions.insert(
            "ups-overload".to_owned(),
            condition(Severity::Warning, format!("The UPS is overloaded{load}."), "The UPS is no longer overloaded.".to_owned()),
        );
    }
    if flags.contains("BYPASS") || flags.contains("OFF") {
        let what = if flags.contains("OFF") { "off" } else { "on bypass" };
        conditions.insert(
            "ups-not-protecting".to_owned(),
            condition(
                Severity::Warning,
                format!("The UPS is {what}: the load is not protected."),
                "The UPS is protecting the load again.".to_owned(),
            ),
        );
    }

    conditions
}

/// Debounces conditions into raise/clear events. A condition has to hold for `threshold`
/// consecutive updates before it is raised, and be absent for as many before it clears,
/// so a single odd poll (nvidia-smi timing out once) never produces a notification.
pub struct ConditionTracker {
    threshold: u32,
    pending: BTreeMap<String, u32>,
    raised: BTreeMap<String, (Condition, u32)>,
}

impl ConditionTracker {
    pub fn new(threshold: u32) -> Self {
        Self { threshold: threshold.max(1), pending: BTreeMap::new(), raised: BTreeMap::new() }
    }

    pub fn update(&mut self, conditions: &Conditions) -> Vec<NewEvent> {
        let mut events = Vec::new();

        for (kind, condition) in conditions {
            if let Some((_, absent_for)) = self.raised.get_mut(kind) {
                *absent_for = 0;
                continue;
            }

            let seen = self.pending.get(kind).copied().unwrap_or(0) + 1;
            if seen >= self.threshold {
                self.pending.remove(kind);
                self.raised.insert(kind.clone(), (condition.clone(), 0));
                events.push(NewEvent { severity: condition.severity, kind: kind.clone(), message: condition.message.clone() });
            } else {
                self.pending.insert(kind.clone(), seen);
            }
        }

        self.pending.retain(|kind, _| conditions.contains_key(kind));

        let threshold = self.threshold;
        self.raised.retain(|kind, (condition, absent_for)| {
            if conditions.contains_key(kind) {
                return true;
            }
            *absent_for += 1;
            if *absent_for >= threshold {
                events.push(NewEvent { severity: Severity::Info, kind: kind.clone(), message: condition.cleared.clone() });
                return false;
            }
            true
        });

        events
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct DriveUpdate {
    /// The drive's new last-good state, or None if this poll had nothing usable (asleep, smartctl failed).
    pub state: Option<DriveState>,
    /// True if anything other than power-on hours moved, i.e. worth a history row.
    pub changed: bool,
    pub events: Vec<NewEvent>,
}

/// Compares one drive's fresh health poll with its last good state. smart_status.passed is a
/// lagging indicator (drives routinely die with it still true), so sector counts are watched
/// too, but on CHANGE rather than level: a drive with a long-standing, stable reallocated
/// count would otherwise alert forever.
pub fn diff_drive_health(previous: Option<&DriveState>, current: &DriveHealth) -> DriveUpdate {
    if !current.is_available {
        return DriveUpdate { state: None, changed: false, events: vec![] };
    }

    let state = DriveState {
        wwn: current.device_name.clone(),
        port: current.port.clone(),
        passed: current.passed,
        reallocated_sector_count: current.reallocated_sector_count,
        pending_sector_count: current.pending_sector_count,
        power_on_hours: current.power_on_hours,
        source_path: current.source_path.clone(),
        as_of: current.as_of.clone(),
    };

    let mut events = Vec::new();
    let name = format!("Drive {} ({})", current.device_name, current.source_path);
    let event = |severity, kind: &str, message: String| NewEvent { severity, kind: format!("{kind}:{}", state.wwn), message };

    let previous_passed = previous.and_then(|p| p.passed);
    if current.passed == Some(false) && previous_passed != Some(false) {
        events.push(event(Severity::Critical, "drive-failed", format!("{name} FAILED its SMART overall-health self-assessment.")));
    } else if current.passed == Some(true) && previous_passed == Some(false) {
        events.push(event(Severity::Info, "drive-failed", format!("{name} passes its SMART self-assessment again.")));
    }

    if let (Some(reallocated), Some(previous_reallocated)) =
        (current.reallocated_sector_count, previous.and_then(|p| p.reallocated_sector_count))
        && reallocated > previous_reallocated
    {
        events.push(event(
            Severity::Warning,
            "drive-reallocated",
            format!("{name}: reallocated sector count grew from {previous_reallocated} to {reallocated}."),
        ));
    }

    let previous_pending = previous.and_then(|p| p.pending_sector_count).unwrap_or(0);
    if let Some(pending) = current.pending_sector_count
        && pending != previous_pending
    {
        events.push(if pending > 0 {
            event(
                Severity::Warning,
                "drive-pending",
                format!("{name}: {pending} pending (unreadable, not yet reallocated) sector(s), was {previous_pending}."),
            )
        } else {
            event(Severity::Info, "drive-pending", format!("{name}: no pending sectors any more (was {previous_pending})."))
        });
    }

    let changed = previous.is_none_or(|p| {
        p.passed != state.passed
            || p.reallocated_sector_count != state.reallocated_sector_count
            || p.pending_sector_count != state.pending_sector_count
    });

    DriveUpdate { state: Some(state), changed, events }
}

#[cfg(test)]
mod tests {
    use super::*;
    use vigil_protocol::{FanStatus, SensorReading, UpsReading};

    fn fan(id: &str, mode: Option<PwmMode>, stalled: bool) -> FanStatus {
        FanStatus { id: id.to_owned(), duty_percent: 65, rpm: Some(if stalled { 0 } else { 1200 }), mode, stalled }
    }

    fn sensor(id: &str, category: SensorCategory, celsius: Option<f64>) -> SensorReading {
        SensorReading::new(id, category, "label", celsius, "path")
    }

    fn snapshot(fans: Vec<FanStatus>, sensors: Vec<SensorReading>, healthy: bool) -> Snapshot {
        Snapshot { timestamp_utc: "t".to_owned(), sensors, fans, drive_health: vec![], control_loop_healthy: healthy }
    }

    fn known(ids: &[&str]) -> BTreeSet<String> {
        ids.iter().map(|id| id.to_string()).collect()
    }

    #[test]
    fn finds_nothing_wrong_with_a_healthy_snapshot() {
        let healthy =
            snapshot(vec![fan("drive-cage", Some(PwmMode::Manual), false)], vec![sensor("cpu", SensorCategory::Cpu, Some(40.0))], true);
        assert!(conditions_in(&healthy, &known(&["cpu"])).is_empty());
    }

    #[test]
    fn flags_an_unhealthy_loop_a_stalled_fan_and_fans_not_on_manual() {
        let fans =
            vec![fan("drive-cage", Some(PwmMode::Manual), true), fan("intake", Some(PwmMode::SmartFanIV), false), fan("rear", None, false)];
        let conditions = conditions_in(&snapshot(fans, vec![], false), &known(&[]));

        assert_eq!(conditions.keys().collect::<Vec<_>>(), ["fan-mode:intake", "fan-mode:rear", "fan-stalled:drive-cage", "loop-unhealthy"]);
        assert_eq!(conditions["fan-stalled:drive-cage"].severity, Severity::Critical);
        assert!(conditions["fan-mode:intake"].message.contains("smartFanIV"));
        assert!(conditions["fan-mode:rear"].message.contains("unreadable"));
    }

    #[test]
    fn a_sleeping_drive_is_not_a_problem_but_an_unreadable_sensor_or_a_vanished_one_is() {
        let sensors = vec![sensor("drive:naa.1", SensorCategory::Drive, None), sensor("gpu", SensorCategory::Gpu, None)];
        let conditions = conditions_in(&snapshot(vec![], sensors, true), &known(&["drive:naa.1", "gpu", "hba"]));

        assert_eq!(conditions.keys().collect::<Vec<_>>(), ["sensor-missing:hba", "sensor-unavailable:gpu"]);
    }

    #[test]
    fn the_tracker_raises_after_the_threshold_and_clears_after_as_many_absences() {
        let mut tracker = ConditionTracker::new(3);
        let mut present = Conditions::new();
        present.insert("fan-stalled:x".to_owned(), condition(Severity::Critical, "stalled".to_owned(), "spinning".to_owned()));
        let absent = Conditions::new();

        assert!(tracker.update(&present).is_empty());
        assert!(tracker.update(&present).is_empty());
        assert_eq!(tracker.update(&present)[0].message, "stalled");
        assert!(tracker.update(&present).is_empty());

        assert!(tracker.update(&absent).is_empty());
        assert!(tracker.update(&absent).is_empty());
        let cleared = tracker.update(&absent);
        assert_eq!((cleared[0].severity, cleared[0].message.as_str()), (Severity::Info, "spinning"));
    }

    #[test]
    fn a_blip_shorter_than_the_threshold_never_raises() {
        let mut tracker = ConditionTracker::new(3);
        let mut present = Conditions::new();
        present.insert("gpu".to_owned(), condition(Severity::Warning, "w".to_owned(), "c".to_owned()));

        assert!(tracker.update(&present).is_empty());
        assert!(tracker.update(&Conditions::new()).is_empty());
        assert!(tracker.update(&present).is_empty());
        assert!(tracker.update(&present).is_empty());
    }

    fn health(passed: Option<bool>, reallocated: Option<u64>, pending: Option<u64>, available: bool) -> DriveHealth {
        DriveHealth {
            device_name: "naa.1".to_owned(),
            port: Some("pci-0000:01:00.1-ata-3".to_owned()),
            passed,
            reallocated_sector_count: reallocated,
            pending_sector_count: pending,
            power_on_hours: Some(100),
            source_path: "/dev/sdc".to_owned(),
            is_available: available,
            as_of: "t".to_owned(),
        }
    }

    #[test]
    fn a_sleeping_drive_leaves_its_last_good_state_alone() {
        assert_eq!(diff_drive_health(None, &health(None, None, None, false)), DriveUpdate { state: None, changed: false, events: vec![] });
    }

    #[test]
    fn the_first_sighting_is_recorded_without_alerting_on_a_stable_count() {
        let update = diff_drive_health(None, &health(Some(true), Some(1048), Some(0), true));
        assert!(update.changed);
        assert!(update.events.is_empty());
        assert_eq!(update.state.unwrap().port.as_deref(), Some("pci-0000:01:00.1-ata-3"));
    }

    #[test]
    fn alerts_on_failure_growth_and_pending_sectors_and_on_recovery() {
        let previous = diff_drive_health(None, &health(Some(true), Some(8), Some(0), true)).state.unwrap();

        let worse = diff_drive_health(Some(&previous), &health(Some(false), Some(16), Some(2), true));
        let kinds: Vec<_> = worse.events.iter().map(|e| (e.kind.as_str(), e.severity)).collect();
        assert_eq!(
            kinds,
            [
                ("drive-failed:naa.1", Severity::Critical),
                ("drive-reallocated:naa.1", Severity::Warning),
                ("drive-pending:naa.1", Severity::Warning)
            ]
        );
        assert!(worse.events[1].message.contains("grew from 8 to 16"));

        let better = diff_drive_health(worse.state.as_ref(), &health(Some(true), Some(16), Some(0), true));
        assert!(better.events.iter().all(|e| e.severity == Severity::Info));
        assert_eq!(better.events.len(), 2);
    }

    #[test]
    fn only_power_on_hours_moving_is_not_a_change() {
        let previous = diff_drive_health(None, &health(Some(true), Some(8), Some(0), true)).state.unwrap();
        let mut next = health(Some(true), Some(8), Some(0), true);
        next.power_on_hours = Some(101);
        assert!(!diff_drive_health(Some(&previous), &next).changed);
    }

    fn ups(status: &[&str], charge: Option<f64>, runtime: Option<f64>) -> UpsState {
        let reading = UpsReading {
            timestamp_utc: "t".to_owned(),
            name: "apc".to_owned(),
            model: None,
            status: status.iter().map(|s| s.to_string()).collect(),
            battery_charge: charge,
            battery_runtime_seconds: runtime,
            load: Some(25.0),
            real_power: None,
            input_voltage: None,
            output_voltage: None,
            battery_voltage: None,
            variables: BTreeMap::new(),
        };
        UpsState { enabled: true, name: "apc".to_owned(), reading: Some(reading), error: None }
    }

    #[test]
    fn a_ups_online_is_fine() {
        assert!(ups_conditions(&ups(&["OL", "CHRG"], Some(100.0), Some(2000.0)), Duration::ZERO).is_empty());
    }

    #[test]
    fn flags_on_battery_low_battery_and_a_battery_to_replace() {
        let conditions = ups_conditions(&ups(&["OB", "DISCHRG", "LB", "RB"], Some(9.0), Some(150.0)), Duration::ZERO);

        assert_eq!(conditions.keys().collect::<Vec<_>>(), ["ups-low-battery", "ups-on-battery", "ups-replace-battery"]);
        assert_eq!(conditions["ups-low-battery"].severity, Severity::Critical);
        assert_eq!(
            conditions["ups-on-battery"].message,
            "Mains power lost: the UPS is on battery. Battery at 9%, about 3 min of runtime left."
        );
    }

    #[test]
    fn an_unreadable_ups_is_only_a_problem_after_30_seconds_and_only_when_configured() {
        let unreadable = UpsState { enabled: true, name: "apc".to_owned(), reading: None, error: Some("upsd: DATA-STALE".to_owned()) };
        assert!(ups_conditions(&unreadable, Duration::from_secs(10)).is_empty());
        assert!(ups_conditions(&unreadable, Duration::from_secs(30))["ups-unreadable"].message.contains("DATA-STALE"));

        let disabled = UpsState { enabled: false, ..unreadable };
        assert!(ups_conditions(&disabled, Duration::from_secs(60)).is_empty());
    }

    #[test]
    fn with_a_threshold_of_one_power_events_raise_and_clear_on_the_first_poll() {
        let mut tracker = ConditionTracker::new(1);
        assert_eq!(tracker.update(&ups_conditions(&ups(&["OB"], None, None), Duration::ZERO))[0].severity, Severity::Warning);
        assert_eq!(
            tracker.update(&ups_conditions(&ups(&["OL"], None, None), Duration::ZERO))[0].message,
            "Mains power is back: the UPS is online again."
        );
    }
}
