//! Validating the config before anything is acted on.
//!
//! This runs at startup, before any fan is touched, so a mistake surfaces as a refusal to
//! start with every problem listed rather than as a poll that fails every two seconds. Every
//! rule guards against something that would otherwise leave a fan uncontrolled:
//!
//!  - an out-of-range duty can't be written to the chip;
//!  - a channel with no curve is taken to manual and then never given a duty;
//!  - two curves on one channel fight over it and share one hysteresis state;
//!  - a curve naming an unknown channel silently does nothing;
//!  - unsorted points make interpolation return nonsense.
//!
//! Over the 400-line ceiling (STYLE.md §2.1) and staying that way: two thirds of it is the
//! test table, and every one of those cases is a config that would otherwise have left a fan
//! uncontrolled. Tests belong beside the rule they pin.

use super::{Config, is_wildcard};
use std::collections::{HashMap, HashSet};

impl Config {
    /// Every problem found (empty if valid), so they can all be fixed in one pass.
    pub fn validate(&self) -> Vec<String> {
        let mut errors = Vec::new();

        let positive = |errors: &mut Vec<String>, name: &str, value: f64| {
            if !(value.is_finite() && value > 0.0) {
                errors.push(format!("{name} must be a positive number of seconds (is {value})."));
            }
        };

        positive(&mut errors, "poll_interval_secs", self.poll_interval_secs);
        positive(&mut errors, "deadman_timeout_secs", self.deadman_timeout_secs);
        positive(&mut errors, "sensor_rescan_interval_secs", self.sensor_rescan_interval_secs);
        positive(&mut errors, "gpu.timeout_secs", self.gpu.timeout_secs);
        positive(&mut errors, "drive_health.timeout_secs", self.drive_health.timeout_secs);
        if self.drive_health.enabled {
            positive(&mut errors, "drive_health.poll_interval_secs", self.drive_health.poll_interval_secs);
        }

        if !(self.gpu.min_read_interval_secs.is_finite() && self.gpu.min_read_interval_secs >= 0.0) {
            errors.push(format!("gpu.min_read_interval_secs must not be negative (is {}).", self.gpu.min_read_interval_secs));
        }

        if self.poll_interval_secs > 0.0 && self.deadman_timeout_secs < self.poll_interval_secs * 2.0 {
            errors.push(format!(
                "deadman_timeout_secs ({}) must be at least twice poll_interval_secs ({}), or a single slow poll trips it.",
                self.deadman_timeout_secs, self.poll_interval_secs
            ));
        }

        if self.api.socket_mode_bits().is_none() {
            errors.push(format!("api.socket_mode must be an octal permission string like \"0660\" (is \"{}\").", self.api.socket_mode));
        }

        if self.api.max_clients == 0 {
            errors.push("api.max_clients must be at least 1.".to_owned());
        }

        for channel in &self.channels {
            if channel.id.trim().is_empty() {
                errors.push("A fan channel has an empty id.".to_owned());
            }

            if channel.index < 1 {
                errors.push(format!("Channel '{}': index must be 1 or greater (is {}).", channel.id, channel.index));
            }

            if !is_duty(channel.minimum_duty_percent) {
                errors.push(format!("Channel '{}': minimum_duty_percent must be 0-100 (is {}).", channel.id, channel.minimum_duty_percent));
            }
        }

        for (id, count) in counts(self.channels.iter().map(|c| c.id.as_str())) {
            if count > 1 {
                errors.push(format!("Channel id '{id}' is defined {count} times."));
            }
        }

        let mut headers: HashMap<(&str, i64), Vec<&str>> = HashMap::new();
        for channel in &self.channels {
            headers.entry((&channel.chip_name, channel.index)).or_default().push(&channel.id);
        }
        let mut shared: Vec<_> = headers.into_iter().filter(|(_, ids)| ids.len() > 1).collect();
        shared.sort();
        for ((chip, index), ids) in shared {
            errors.push(format!("Channels '{}' all map to {chip} pwm{index}.", ids.join("', '")));
        }

        for zone in &self.zones {
            if zone.id.trim().is_empty() {
                errors.push("A zone has an empty id.".to_owned());
            }

            if zone.sensor_ids.is_empty() {
                errors.push(format!("Zone '{}': sensor_ids is empty.", zone.id));
            }
        }

        for (id, count) in counts(self.zones.iter().map(|z| z.id.as_str())) {
            if count > 1 {
                errors.push(format!("Zone id '{id}' is defined {count} times."));
            }
        }

        // A sensor can only sit in one place. Two zones both naming it explicitly is a
        // contradiction, not a tie to break.
        let mut claims: Vec<(&str, Vec<&str>)> = Vec::new();
        for zone in &self.zones {
            for id in zone.sensor_ids.iter().filter(|id| !is_wildcard(id)) {
                match claims.iter_mut().find(|(sensor, _)| *sensor == id.as_str()) {
                    Some((_, zones)) => zones.push(&zone.id),
                    None => claims.push((id, vec![&zone.id])),
                }
            }
        }
        for (sensor, zones) in claims.into_iter().filter(|(_, zones)| zones.len() > 1) {
            errors.push(format!("Sensor '{sensor}' is named explicitly in zones '{}'; it can only be in one.", zones.join("', '")));
        }

        let channel_ids: HashSet<&str> = self.channels.iter().map(|c| c.id.as_str()).collect();
        let zone_ids: HashSet<&str> = self.zones.iter().map(|z| z.id.as_str()).collect();

        for curve in &self.curves {
            let name = format!("Curve for '{}'", curve.fan_channel_id);

            if !channel_ids.contains(curve.fan_channel_id.as_str()) {
                errors.push(format!("{name}: no fan channel with that id exists."));
            }

            validate_input(&mut errors, &name, &curve.zones, &curve.sensor_ids, &curve.points, &zone_ids);

            if !is_duty(curve.fail_safe_duty_percent) {
                errors.push(format!("{name}: fail_safe_duty_percent must be 0-100 (is {}).", curve.fail_safe_duty_percent));
            }

            if !(curve.hysteresis_celsius.is_finite() && curve.hysteresis_celsius >= 0.0) {
                errors.push(format!("{name}: hysteresis_celsius must not be negative (is {}).", curve.hysteresis_celsius));
            }
        }

        for (id, count) in counts(self.curves.iter().map(|c| c.fan_channel_id.as_str())) {
            if count > 1 {
                errors.push(format!("Fan channel '{id}' has {count} curves; exactly one is required."));
            }
        }

        let curve_channel_ids: HashSet<&str> = self.curves.iter().map(|c| c.fan_channel_id.as_str()).collect();
        for channel in self.channels.iter().filter(|c| !curve_channel_ids.contains(c.id.as_str())) {
            errors.push(format!(
                "Channel '{}' has no curve: it would be taken to manual control and never given a duty. Add a curve or remove the channel.",
                channel.id
            ));
        }

        errors
    }
}

/// A curve's inputs and points.
fn validate_input(
    errors: &mut Vec<String>,
    name: &str,
    zones: &[String],
    sensor_ids: &[String],
    points: &[(f64, i64)],
    zone_ids: &HashSet<&str>,
) {
    if sensor_ids.is_empty() && zones.is_empty() {
        errors.push(format!("{name}: both sensor_ids and zones are empty; it needs at least one input."));
    }

    for zone in zones.iter().filter(|z| !zone_ids.contains(z.as_str())) {
        errors.push(format!("{name}: no zone with id '{zone}' exists."));
    }

    if points.is_empty() {
        errors.push(format!("{name}: points is empty."));
    }

    if points.iter().any(|(temperature, duty)| !temperature.is_finite() || !is_duty(*duty)) {
        errors.push(format!("{name}: every point needs a finite temperature and a duty of 0-100."));
    }

    if points.windows(2).any(|pair| pair[1].0 < pair[0].0) {
        errors.push(format!("{name}: points must be sorted by ascending temperature."));
    }
}

/// A PWM duty the chip will accept. Anything else refuses to start rather than being clamped:
/// a duty out of range is a typo, and guessing what was meant is how a fan ends up at 0%.
fn is_duty(percent: i64) -> bool {
    (0..=100).contains(&percent)
}

/// Occurrence counts in first-seen order, so error output is deterministic.
fn counts<'a>(items: impl Iterator<Item = &'a str>) -> Vec<(&'a str, usize)> {
    let mut result: Vec<(&str, usize)> = Vec::new();
    for item in items {
        match result.iter_mut().find(|(seen, _)| *seen == item) {
            Some((_, count)) => *count += 1,
            None => result.push((item, 1)),
        }
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::{ChannelConfig, CurveConfig, ZoneConfig};

    fn channel(id: &str, index: i64) -> ChannelConfig {
        ChannelConfig { id: id.to_owned(), chip_name: "nct6798".to_owned(), index, minimum_duty_percent: 30 }
    }

    fn curve(channel_id: &str) -> CurveConfig {
        CurveConfig {
            fan_channel_id: channel_id.to_owned(),
            zones: Vec::new(),
            sensor_ids: vec!["cpu".to_owned()],
            points: vec![(30.0, 30), (70.0, 100)],
            hysteresis_celsius: 3.0,
            fail_safe_duty_percent: 100,
        }
    }

    fn config(channels: Vec<ChannelConfig>, curves: Vec<CurveConfig>) -> Config {
        Config { channels, curves, ..Config::default() }
    }

    fn has(errors: &[String], needle: &str) -> bool {
        errors.iter().any(|e| e.contains(needle))
    }

    #[test]
    fn accepts_a_valid_config_and_an_empty_one() {
        assert_eq!(config(vec![channel("cpu", 1)], vec![curve("cpu")]).validate(), Vec::<String>::new());
        assert_eq!(Config::default().validate(), Vec::<String>::new());
    }

    #[test]
    fn rejects_curve_for_unknown_channel() {
        let errors = config(vec![channel("cpu", 1)], vec![curve("cpu"), curve("typo")]).validate();

        assert!(has(&errors, "'typo'"));
    }

    #[test]
    fn rejects_channel_with_no_curve() {
        let errors = config(vec![channel("cpu", 1), channel("case", 2)], vec![curve("cpu")]).validate();

        assert!(errors.iter().any(|e| e.contains("'case'") && e.contains("no curve")));
    }

    #[test]
    fn rejects_two_curves_on_one_channel() {
        let errors = config(vec![channel("cpu", 1)], vec![curve("cpu"), curve("cpu")]).validate();

        assert!(has(&errors, "2 curves"));
    }

    #[test]
    fn rejects_duplicate_channel_ids_and_duplicate_headers() {
        let errors = config(vec![channel("a", 1), channel("a", 2), channel("b", 2)], vec![curve("a"), curve("b")]).validate();

        assert!(has(&errors, "'a' is defined 2 times"));
        assert!(has(&errors, "pwm2"));
    }

    #[test]
    fn rejects_out_of_range_duties() {
        for duty in [101, -1] {
            let mut bad_channel = channel("cpu", 1);
            bad_channel.minimum_duty_percent = duty;
            let mut bad_curve = curve("cpu");
            bad_curve.fail_safe_duty_percent = duty;
            bad_curve.points = vec![(30.0, 30), (70.0, duty)];

            let errors = config(vec![bad_channel], vec![bad_curve]).validate();

            assert!(has(&errors, "minimum_duty_percent"));
            assert!(has(&errors, "fail_safe_duty_percent"));
            assert!(has(&errors, "duty of 0-100"));
        }
    }

    #[test]
    fn rejects_unsorted_points_but_allows_a_vertical_step() {
        let mut unsorted = curve("cpu");
        unsorted.points = vec![(60.0, 70), (30.0, 30)];
        let mut step = curve("cpu");
        step.points = vec![(30.0, 30), (50.0, 40), (50.0, 80)];

        assert!(has(&config(vec![channel("cpu", 1)], vec![unsorted]).validate(), "sorted"));
        assert!(config(vec![channel("cpu", 1)], vec![step]).validate().is_empty());
    }

    #[test]
    fn rejects_empty_points_and_sensor_ids() {
        let mut empty = curve("cpu");
        empty.points.clear();
        empty.sensor_ids.clear();

        let errors = config(vec![channel("cpu", 1)], vec![empty]).validate();

        assert!(has(&errors, "points is empty"));
        assert!(has(&errors, "both sensor_ids and zones are empty"));
    }

    fn zone(id: &str, sensor_ids: &[&str]) -> ZoneConfig {
        ZoneConfig { id: id.to_owned(), sensor_ids: sensor_ids.iter().map(|s| (*s).to_owned()).collect() }
    }

    #[test]
    fn accepts_a_curve_driven_by_zones_alone() {
        let mut zoned = curve("cage");
        zoned.sensor_ids.clear();
        zoned.zones = vec!["drive-bay".to_owned()];
        let mut config = config(vec![channel("cage", 1)], vec![zoned]);
        config.zones = vec![zone("drive-bay", &["drive:*"]), zone("main", &["cpu", "drive:naa.1"])];

        assert_eq!(config.validate(), Vec::<String>::new());
    }

    #[test]
    fn rejects_unknown_zone_reference_duplicate_zone_ids_empty_zones_and_double_claims() {
        let mut zoned = curve("cage");
        zoned.zones = vec!["typo".to_owned()];
        let mut config = config(vec![channel("cage", 1)], vec![zoned]);
        config.zones = vec![zone("a", &["drive:naa.1"]), zone("a", &[]), zone("b", &["drive:naa.1", "drive:*"])];

        let errors = config.validate();

        assert!(has(&errors, "no zone with id 'typo'"));
        assert!(has(&errors, "Zone id 'a' is defined 2 times"));
        assert!(has(&errors, "Zone 'a': sensor_ids is empty"));
        assert!(has(&errors, "Sensor 'drive:naa.1' is named explicitly in zones 'a', 'b'"));
    }

    /// The dashboard in CT 203 reads the socket as its `dashboard` group. A deploy config
    /// that drops these still passes every check run as root on the host, while the
    /// dashboard silently loses access.
    #[test]
    fn the_orion_config_gives_the_socket_to_the_dashboard_container() {
        let config = Config::parse(include_str!("../../../deploy/vigild.toml")).unwrap();

        assert_eq!((config.api.socket_uid, config.api.socket_gid), (Some(100000), Some(102000)));
        assert_eq!(config.api.socket_mode_bits(), Some(0o660));
    }

    #[test]
    fn rejects_deadman_timeout_too_close_to_poll_interval() {
        let config = Config { poll_interval_secs: 10.0, deadman_timeout_secs: 15.0, ..Config::default() };

        assert!(has(&config.validate(), "deadman_timeout_secs"));
    }

    #[test]
    fn rejects_non_positive_intervals_negative_hysteresis_and_bad_socket_mode() {
        let mut bad_curve = curve("cpu");
        bad_curve.hysteresis_celsius = -1.0;
        let mut config = config(vec![channel("cpu", 1)], vec![bad_curve]);
        config.poll_interval_secs = 0.0;
        config.sensor_rescan_interval_secs = f64::NAN;
        config.api.socket_mode = "rw-rw----".to_owned();

        let errors = config.validate();

        assert!(has(&errors, "poll_interval_secs"));
        assert!(has(&errors, "sensor_rescan_interval_secs"));
        assert!(has(&errors, "hysteresis_celsius"));
        assert!(has(&errors, "socket_mode"));
    }

    #[test]
    fn reports_every_problem_at_once() {
        let mut bad_curve = curve("typo");
        bad_curve.fail_safe_duty_percent = 500;

        assert!(config(vec![channel("cpu", 1), channel("case", 2)], vec![bad_curve]).validate().len() >= 4);
    }

    #[test]
    fn parses_toml_including_point_pairs_and_rejects_unknown_keys() {
        let config = Config::parse(
            r#"
            poll_interval_secs = 2

            [api]
            socket_uid = 100000

            [[zones]]
            id = "drive-bay"
            sensor_ids = ["drive:*"]

            [[channels]]
            id = "cpu"
            chip_name = "nct6798"
            index = 2

            [[curves]]
            fan_channel_id = "cpu"
            zones = ["drive-bay"]
            sensor_ids = ["cpu", "drive:*"]
            points = [[30, 30], [45.5, 45]]
            "#,
        )
        .unwrap();

        assert_eq!(config.api.socket_uid, Some(100000));
        assert_eq!(config.zones[0].sensor_ids, vec!["drive:*"]);
        assert_eq!(config.curves[0].zones, vec!["drive-bay"]);
        assert_eq!(config.channels[0].minimum_duty_percent, 20);
        assert_eq!(config.curves[0].points, vec![(30.0, 30), (45.5, 45)]);
        assert_eq!(config.curves[0].fail_safe_duty_percent, 100);
        assert!(config.validate().is_empty());

        assert!(Config::parse("pol_interval_secs = 2").is_err());
    }
}
