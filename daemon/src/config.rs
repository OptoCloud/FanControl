//! Config file shape (TOML) and its validation.
//!
//! Validation runs at startup, before any fan is touched, so mistakes surface as a refusal
//! to start with every problem listed rather than as a poll that fails every 2 seconds.
//! Every rule guards against something that would otherwise leave a fan uncontrolled:
//!  - an out-of-range duty can't be written to the chip;
//!  - a channel with no curve is taken to manual and then never given a duty;
//!  - two curves on one channel fight over it and share one hysteresis state;
//!  - a curve naming an unknown channel silently does nothing;
//!  - unsorted points make interpolation return nonsense.

use serde::Deserialize;
use std::collections::{HashMap, HashSet};
use std::time::Duration;

#[derive(Debug, Clone, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct Config {
    pub poll_interval_secs: f64,
    /// If the control loop doesn't complete a poll within this window, every channel is
    /// released back to automatic control.
    pub deadman_timeout_secs: f64,
    /// hwmon nodes aren't permanent: a drive that resets or is hot-swapped comes back
    /// under a new hwmonN, and without a re-scan it would stay unavailable (and silently
    /// out of every drive:* curve) until the daemon restarted.
    pub sensor_rescan_interval_secs: f64,
    /// Where sysfs is mounted. Only ever changed to run the daemon against a fake tree
    /// (integration tests, trying a config on a machine without the hardware).
    pub sysfs_root: String,
    pub api: ApiConfig,
    pub gpu: GpuConfig,
    pub hba: HbaConfig,
    pub drive_health: DriveHealthConfig,
    pub channels: Vec<ChannelConfig>,
    pub curves: Vec<CurveConfig>,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            poll_interval_secs: 2.0,
            deadman_timeout_secs: 30.0,
            sensor_rescan_interval_secs: 30.0,
            sysfs_root: "/sys".to_owned(),
            api: ApiConfig::default(),
            gpu: GpuConfig::default(),
            hba: HbaConfig::default(),
            drive_health: DriveHealthConfig::default(),
            channels: Vec::new(),
            curves: Vec::new(),
        }
    }
}

/// The status API listens on a unix socket only: this daemon runs as root with raw PWM and
/// ioctl access, so it never opens a network port. A containerised consumer gets the
/// socket's directory bind-mounted in.
#[derive(Debug, Clone, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct ApiConfig {
    pub enabled: bool,
    pub socket_path: String,
    /// Octal permission string for the socket file.
    pub socket_mode: String,
    /// Owner to chown the socket to. An unprivileged LXC maps its root to a high host uid
    /// (100000 by default), so that is what has to own the socket for the container to
    /// connect to it.
    pub socket_uid: Option<u32>,
    pub socket_gid: Option<u32>,
    /// Upper bound on simultaneous connections, so a misbehaving consumer can't exhaust threads.
    pub max_clients: usize,
}

impl Default for ApiConfig {
    fn default() -> Self {
        Self {
            enabled: true,
            socket_path: "/run/fancontrol/fancontrol.sock".to_owned(),
            socket_mode: "0660".to_owned(),
            socket_uid: None,
            socket_gid: None,
            max_clients: 16,
        }
    }
}

impl ApiConfig {
    pub fn socket_mode_bits(&self) -> Option<u32> {
        u32::from_str_radix(self.socket_mode.trim_start_matches("0o"), 8).ok().filter(|mode| *mode <= 0o777)
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct GpuConfig {
    /// False on a host with no NVIDIA GPU: never spawns nvidia-smi, gpu is always unavailable.
    pub enabled: bool,
    pub nvidia_smi_path: String,
    /// nvidia-smi is a process spawn, far heavier than the sysfs reads the rest of a poll
    /// consists of, so its result is reused for this long. Zero queries on every poll.
    pub min_read_interval_secs: f64,
    /// nvidia-smi is killed and gpu reported unavailable past this. Counts against the deadman.
    pub timeout_secs: f64,
}

impl Default for GpuConfig {
    fn default() -> Self {
        Self { enabled: true, nvidia_smi_path: "nvidia-smi".to_owned(), min_read_interval_secs: 6.0, timeout_secs: 5.0 }
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct HbaConfig {
    pub enabled: bool,
    pub device_path: String,
    /// Which IOC this is, per mpt3sas' enumeration order. 0 is correct for a single HBA.
    pub ioc_number: u32,
}

impl Default for HbaConfig {
    fn default() -> Self {
        Self { enabled: true, device_path: "/dev/mpt3ctl".to_owned(), ioc_number: 0 }
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct DriveHealthConfig {
    pub enabled: bool,
    pub smartctl_path: String,
    /// Deliberately independent of and much slower than the control loop: SMART health
    /// barely changes, and running smartctl against every drive every 2s would be pure waste.
    pub poll_interval_secs: f64,
    /// Per drive: smartctl is killed and that drive reported unavailable past this.
    pub timeout_secs: f64,
}

impl Default for DriveHealthConfig {
    fn default() -> Self {
        Self { enabled: true, smartctl_path: "smartctl".to_owned(), poll_interval_secs: 900.0, timeout_secs: 60.0 }
    }
}

/// One fan header. chip_name + index are resolved against the live hwmon tree at startup
/// rather than storing a hwmonN path, because hwmon numbering isn't stable.
#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ChannelConfig {
    pub id: String,
    pub chip_name: String,
    /// The N in pwmN / fanN.
    pub index: i64,
    /// Floor below which this fan stalls. The curve result is never allowed under it.
    #[serde(default = "default_minimum_duty")]
    pub minimum_duty_percent: i64,
}

fn default_minimum_duty() -> i64 {
    20
}

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CurveConfig {
    pub fan_channel_id: String,
    /// Sensor ids, aggregated by max. "prefix:*" matches every sensor id with that prefix.
    pub sensor_ids: Vec<String>,
    /// [temperature_celsius, duty_percent] pairs, ascending by temperature.
    pub points: Vec<(f64, i64)>,
    #[serde(default = "default_hysteresis")]
    pub hysteresis_celsius: f64,
    #[serde(default = "default_fail_safe")]
    pub fail_safe_duty_percent: i64,
}

fn default_hysteresis() -> f64 {
    3.0
}

fn default_fail_safe() -> i64 {
    100
}

impl Config {
    pub fn parse(toml_text: &str) -> Result<Self, String> {
        toml::from_str(toml_text).map_err(|e| e.to_string())
    }

    pub fn poll_interval(&self) -> Duration {
        Duration::from_secs_f64(self.poll_interval_secs)
    }

    pub fn deadman_timeout(&self) -> Duration {
        Duration::from_secs_f64(self.deadman_timeout_secs)
    }

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

        let channel_ids: HashSet<&str> = self.channels.iter().map(|c| c.id.as_str()).collect();

        for curve in &self.curves {
            let name = format!("Curve for '{}'", curve.fan_channel_id);

            if !channel_ids.contains(curve.fan_channel_id.as_str()) {
                errors.push(format!("{name}: no fan channel with that id exists."));
            }

            if curve.sensor_ids.is_empty() {
                errors.push(format!("{name}: sensor_ids is empty."));
            }

            if curve.points.is_empty() {
                errors.push(format!("{name}: points is empty."));
            }

            if curve.points.iter().any(|(temperature, duty)| !temperature.is_finite() || !is_duty(*duty)) {
                errors.push(format!("{name}: every point needs a finite temperature and a duty of 0-100."));
            }

            if curve.points.windows(2).any(|pair| pair[1].0 < pair[0].0) {
                errors.push(format!("{name}: points must be sorted by ascending temperature."));
            }

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

    fn channel(id: &str, index: i64) -> ChannelConfig {
        ChannelConfig { id: id.to_owned(), chip_name: "nct6798".to_owned(), index, minimum_duty_percent: 30 }
    }

    fn curve(channel_id: &str) -> CurveConfig {
        CurveConfig {
            fan_channel_id: channel_id.to_owned(),
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
        assert!(has(&errors, "sensor_ids is empty"));
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

            [[channels]]
            id = "cpu"
            chip_name = "nct6798"
            index = 2

            [[curves]]
            fan_channel_id = "cpu"
            sensor_ids = ["cpu", "drive:*"]
            points = [[30, 30], [45.5, 45]]
            "#,
        )
        .unwrap();

        assert_eq!(config.api.socket_uid, Some(100000));
        assert_eq!(config.channels[0].minimum_duty_percent, 20);
        assert_eq!(config.curves[0].points, vec![(30.0, 30), (45.5, 45)]);
        assert_eq!(config.curves[0].fail_safe_duty_percent, 100);
        assert!(config.validate().is_empty());

        assert!(Config::parse("pol_interval_secs = 2").is_err());
    }
}
