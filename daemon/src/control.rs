//! One iteration of the control loop: read sensors, evaluate curves, drive fans, report.
//! Kept free of timing, signals and sockets so every failure path is unit-testable
//! against a fake sysfs; main.rs owns the loop around it.

use crate::config::Config;
use crate::curve::{CurveEngine, FanCurve};
use crate::fans::{FanChannel, FanController, FanStatus, PwmMode, StallDetector};
use crate::log;
use crate::log::ConditionTracker;
use crate::sensors::{self, DEFAULT_WHITELIST, HwmonResolver, ResolvedSensor, SensorReading};
use crate::sysfs::SysFs;
use std::sync::Arc;
use std::time::{Duration, Instant};

/// Polls a fan must read 0 RPM for before it counts as stalled (10s at the default 2s poll).
const STALL_POLLS: u32 = 5;

pub struct ControlLoop {
    sysfs: Arc<dyn SysFs>,
    controller: Arc<dyn FanController>,
    fans: Vec<(FanChannel, FanCurve)>,
    rescan_interval: Duration,
    sysfs_root: String,
    engine: CurveEngine,
    stall_detector: StallDetector,
    conditions: ConditionTracker,
    sensors: Option<(Instant, Vec<ResolvedSensor>)>,
}

pub struct PollResult {
    pub readings: Vec<SensorReading>,
    pub fans: Vec<FanStatus>,
    /// True if every channel was driven successfully this poll.
    pub healthy: bool,
}

impl ControlLoop {
    /// Binds every configured channel to its hwmon chip directory. Fails if a chip isn't
    /// present: a fan channel with no backing chip is something to refuse to start over
    /// (typically the module isn't loaded yet, which a restart a few seconds later fixes).
    pub fn new(config: &Config, sysfs: Arc<dyn SysFs>, controller: Arc<dyn FanController>) -> Result<Self, String> {
        let resolver = HwmonResolver::new(&*sysfs, &config.sysfs_root);

        let channels = config
            .channels
            .iter()
            .map(|channel| {
                let chip_dir = resolver.chip_directory(&channel.chip_name).ok_or_else(|| {
                    format!("No hwmon chip named '{}' was found (needed by fan channel '{}').", channel.chip_name, channel.id)
                })?;

                Ok(FanChannel {
                    id: channel.id.clone(),
                    chip_dir,
                    index: channel.index as u32,
                    minimum_duty_percent: channel.minimum_duty_percent.clamp(0, 100) as u8,
                })
            })
            .collect::<Result<Vec<_>, String>>()?;

        // Every curve has a channel and every channel exactly one curve: config validation guarantees it.
        let fans = config
            .curves
            .iter()
            .filter_map(|curve| {
                let channel = channels.iter().find(|channel| channel.id == curve.fan_channel_id)?;
                Some((channel.clone(), FanCurve::from_config(curve, &config.zones)))
            })
            .collect();

        Ok(Self {
            sysfs,
            controller,
            fans,
            rescan_interval: Duration::from_secs_f64(config.sensor_rescan_interval_secs),
            sysfs_root: config.sysfs_root.clone(),
            engine: CurveEngine::default(),
            stall_detector: StallDetector::new(STALL_POLLS),
            conditions: ConditionTracker::default(),
            sensors: None,
        })
    }

    pub fn channels(&self) -> Vec<FanChannel> {
        self.fans.iter().map(|(channel, _)| channel.clone()).collect()
    }

    /// `extra_readings` are the sensors that don't come from hwmon (gpu, hba).
    pub fn poll(&mut self, extra_readings: Vec<SensorReading>) -> PollResult {
        self.rescan_sensors_if_due();

        let resolved = self.sensors.as_ref().map_or(&[][..], |(_, sensors)| sensors);
        let mut readings: Vec<SensorReading> = resolved.iter().map(|sensor| sensors::read_sensor(&*self.sysfs, sensor)).collect();
        readings.extend(extra_readings);

        let mut healthy = true;
        for index in 0..self.fans.len() {
            healthy &= self.apply(index, &readings);
        }

        let mut statuses = Vec::with_capacity(self.fans.len());
        for (channel, _) in &self.fans {
            statuses.push(self.stall_detector.apply(self.controller.read_status(channel)));
        }
        self.log_fan_conditions(&statuses);

        PollResult { readings, fans: statuses, healthy }
    }

    /// Drives one channel. Isolated per channel so that one failing (a bad sysfs write)
    /// can't stop the channels after it from being driven this poll.
    fn apply(&mut self, index: usize, readings: &[SensorReading]) -> bool {
        let (channel, curve) = &self.fans[index];
        let condition = format!("apply-failed:{}", channel.id);
        let duty = self.engine.evaluate(curve, readings).max(channel.minimum_duty_percent);

        // Manual mode is re-asserted every poll, not just once at startup: observed on real
        // hardware that some Super I/O channels silently revert pwmN_enable (a chip-level
        // fail-safe the Linux driver doesn't document) unless it is periodically
        // refreshed. Writing the duty value alone wasn't enough.
        let result = self.controller.take_manual_control(channel).and_then(|()| self.controller.set_duty_percent(channel, duty));

        match result {
            Ok(()) => {
                if self.conditions.changed(&condition, false) {
                    log!(Info, "Fan channel '{}' is under control again.", channel.id);
                }
                true
            }
            Err(error) => {
                if self.conditions.changed(&condition, true) {
                    log!(
                        Error,
                        "Failed to drive fan channel '{}' ({error}); handing it back to automatic control and retrying every poll.",
                        channel.id
                    );
                }

                // It may be sitting on manual at a stale duty. Best effort: if this write
                // fails too there is nothing left to try until the next poll.
                if let Err(release_error) = self.controller.release_to_auto(channel) {
                    log!(Debug, "Releasing fan channel '{}' after a failed poll also failed: {release_error}", channel.id);
                }
                false
            }
        }
    }

    fn rescan_sensors_if_due(&mut self) {
        if self.sensors.as_ref().is_some_and(|(scanned_at, _)| scanned_at.elapsed() < self.rescan_interval) {
            return;
        }

        let current = HwmonResolver::new(&*self.sysfs, &self.sysfs_root).resolve(&DEFAULT_WHITELIST);

        match &self.sensors {
            None => {
                for spec in DEFAULT_WHITELIST.iter().filter(|spec| !spec.all_instances && !current.iter().any(|s| s.id == spec.id)) {
                    log!(Warning, "Sensor '{}' (chip {}) could not be resolved on this host.", spec.id, spec.chip_name);
                }
            }
            Some((_, previous)) => {
                for sensor in &current {
                    match previous.iter().find(|p| p.id == sensor.id) {
                        None => log!(Info, "Sensor '{}' appeared at {}.", sensor.id, sensor.temp_input_path),
                        Some(p) if p.temp_input_path != sensor.temp_input_path => {
                            log!(Info, "Sensor '{}' moved to {}.", sensor.id, sensor.temp_input_path)
                        }
                        Some(_) => {}
                    }
                }

                for sensor in previous.iter().filter(|p| !current.iter().any(|c| c.id == p.id)) {
                    log!(Warning, "Sensor '{}' disappeared (was at {}).", sensor.id, sensor.temp_input_path);
                }
            }
        }

        self.sensors = Some((Instant::now(), current));
    }

    fn log_fan_conditions(&mut self, statuses: &[FanStatus]) {
        for status in statuses {
            // A channel that failed this poll was deliberately released, so it isn't expected to read Manual.
            let unexpected_mode =
                status.mode != Some(PwmMode::Manual) && !self.conditions.is_active(&format!("apply-failed:{}", status.id));
            if self.conditions.changed(&format!("unexpected-mode:{}", status.id), unexpected_mode) {
                if unexpected_mode {
                    log!(
                        Warning,
                        "Fan channel '{}' read back pwm_enable={} right after being re-asserted to Manual: a Super I/O watchdog or hardware quirk may be reverting it.",
                        status.id,
                        status.mode.map_or_else(|| "unreadable".to_owned(), |mode| format!("{mode:?}"))
                    );
                } else {
                    log!(Info, "Fan channel '{}' reads back as Manual again.", status.id);
                }
            }

            if self.conditions.changed(&format!("stalled:{}", status.id), status.stalled) {
                if status.stalled {
                    log!(
                        Warning,
                        "Fan channel '{}' reads 0 RPM at {}% duty: fan dead, jammed or unplugged?",
                        status.id,
                        status.duty_percent
                    );
                } else {
                    log!(Info, "Fan channel '{}' is spinning again ({} RPM).", status.id, status.rpm.unwrap_or_default());
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::{ChannelConfig, CurveConfig};
    use crate::fans::SysfsFanController;
    use crate::sensors::SensorCategory;
    use crate::sysfs::fake::FakeSysFs;

    const CHIP: &str = "/sys/class/hwmon/hwmon2";

    fn config(channel_ids: &[&str], sensor_ids: &[&str]) -> Config {
        Config {
            sensor_rescan_interval_secs: 0.000_001, // effectively: re-scan on every poll
            channels: channel_ids
                .iter()
                .enumerate()
                .map(|(i, id)| ChannelConfig {
                    id: (*id).to_owned(),
                    chip_name: "nct6798".to_owned(),
                    index: i as i64 + 1,
                    minimum_duty_percent: 30,
                })
                .collect(),
            curves: channel_ids
                .iter()
                .map(|id| CurveConfig {
                    fan_channel_id: (*id).to_owned(),
                    zones: Vec::new(),
                    sensor_ids: sensor_ids.iter().map(|s| (*s).to_owned()).collect(),
                    points: vec![(30.0, 20), (50.0, 50), (70.0, 100)],
                    hysteresis_celsius: 3.0,
                    fail_safe_duty_percent: 80,
                })
                .collect(),
            ..Config::default()
        }
    }

    fn host(channel_count: u32) -> Arc<FakeSysFs> {
        let sysfs = FakeSysFs::new()
            .with_file(&format!("{CHIP}/name"), "nct6798")
            .with_file("/sys/class/hwmon/hwmon0/name", "k10temp")
            .with_file("/sys/class/hwmon/hwmon0/temp1_label", "Tctl")
            .with_file("/sys/class/hwmon/hwmon0/temp1_input", "60000");
        for index in 1..=channel_count {
            sysfs.set(&format!("{CHIP}/pwm{index}"), "0");
            sysfs.set(&format!("{CHIP}/pwm{index}_enable"), "5");
            sysfs.set(&format!("{CHIP}/fan{index}_input"), "900");
        }
        Arc::new(sysfs)
    }

    fn control_loop(config: &Config, sysfs: &Arc<FakeSysFs>) -> ControlLoop {
        ControlLoop::new(config, sysfs.clone(), Arc::new(SysfsFanController::new(sysfs.clone()))).unwrap()
    }

    fn duty_of(sysfs: &FakeSysFs, index: u32) -> u8 {
        (sysfs.get(&format!("{CHIP}/pwm{index}")).parse::<f64>().unwrap() / 255.0 * 100.0).round() as u8
    }

    #[test]
    fn refuses_to_start_when_a_channels_chip_is_missing() {
        let sysfs = Arc::new(FakeSysFs::new());

        let error = ControlLoop::new(&config(&["cpu"], &["cpu"]), sysfs.clone(), Arc::new(SysfsFanController::new(sysfs))).err().unwrap();

        assert!(error.contains("nct6798") && error.contains("'cpu'"));
    }

    #[test]
    fn drives_each_channel_from_its_curve() {
        let sysfs = host(2);
        let mut control = control_loop(&config(&["front", "rear"], &["cpu"]), &sysfs);

        let result = control.poll(vec![]);

        assert!(result.healthy);
        assert_eq!((duty_of(&sysfs, 1), duty_of(&sysfs, 2)), (75, 75)); // 60C is halfway between (50,50) and (70,100)
        assert_eq!(sysfs.get(&format!("{CHIP}/pwm1_enable")), "1");
        assert_eq!(result.fans.len(), 2);
        assert!(result.readings.iter().any(|r| r.id == "cpu" && r.celsius_or_null == Some(60.0)));
    }

    #[test]
    fn never_drives_below_the_channel_minimum() {
        let sysfs = host(1);
        sysfs.set("/sys/class/hwmon/hwmon0/temp1_input", "20000"); // curve says 20%, channel minimum is 30%
        let mut control = control_loop(&config(&["cpu"], &["cpu"]), &sysfs);

        control.poll(vec![]);

        assert_eq!(duty_of(&sysfs, 1), 30);
    }

    #[test]
    fn uses_readings_supplied_from_outside_hwmon() {
        let sysfs = host(1);
        let mut control = control_loop(&config(&["intake"], &["gpu"]), &sysfs);

        control.poll(vec![SensorReading::new("gpu", SensorCategory::Gpu, "GPU", Some(70.0), "nvidia-smi")]);

        assert_eq!(duty_of(&sysfs, 1), 100);
    }

    #[test]
    fn unreadable_sensor_fails_safe_rather_than_cold() {
        let sysfs = host(1);
        let mut control = control_loop(&config(&["intake"], &["gpu"]), &sysfs);

        control.poll(vec![SensorReading::new("gpu", SensorCategory::Gpu, "GPU", None, "nvidia-smi")]);

        assert_eq!(duty_of(&sysfs, 1), 80);
    }

    #[test]
    fn one_failing_channel_is_released_and_does_not_block_the_others() {
        let sysfs = host(3);
        let mut control = control_loop(&config(&["a", "b", "c"], &["cpu"]), &sysfs);
        sysfs.fail_writes_to(&format!("{CHIP}/pwm2"));

        let result = control.poll(vec![]);

        assert!(!result.healthy);
        assert_eq!((duty_of(&sysfs, 1), duty_of(&sysfs, 3)), (75, 75));
        assert_eq!(sysfs.get(&format!("{CHIP}/pwm1_enable")), "1");
        assert_eq!(sysfs.get(&format!("{CHIP}/pwm2_enable")), "5"); // handed back, not left on manual at a stale duty
        assert_eq!(sysfs.get(&format!("{CHIP}/pwm3_enable")), "1");
    }

    #[test]
    fn picks_up_a_drive_that_reappears_under_a_new_hwmon_number() {
        let sysfs = host(1);
        sysfs.set("/sys/class/hwmon/hwmon5/name", "drivetemp");
        sysfs.set("/sys/class/hwmon/hwmon5/temp1_input", "40000");
        let mut control = control_loop(&config(&["cage"], &["drive:*"]), &sysfs);

        control.poll(vec![]);
        assert_eq!(duty_of(&sysfs, 1), 35);

        // The drive resets: its old node vanishes and it comes back as hwmon9, hotter.
        sysfs.remove("/sys/class/hwmon/hwmon5/name");
        sysfs.remove("/sys/class/hwmon/hwmon5/temp1_input");
        sysfs.set("/sys/class/hwmon/hwmon9/name", "drivetemp");
        sysfs.set("/sys/class/hwmon/hwmon9/temp1_input", "70000");
        std::thread::sleep(Duration::from_millis(5));

        control.poll(vec![]);
        assert_eq!(duty_of(&sysfs, 1), 100);
    }

    #[test]
    fn flags_a_fan_that_stays_at_zero_rpm() {
        let sysfs = host(1);
        sysfs.set(&format!("{CHIP}/fan1_input"), "0");
        let mut control = control_loop(&config(&["cpu"], &["cpu"]), &sysfs);

        let stalled: Vec<bool> = (0..STALL_POLLS).map(|_| control.poll(vec![]).fans[0].stalled).collect();

        assert_eq!(stalled.iter().filter(|s| **s).count(), 1);
        assert!(stalled.last().unwrap());
    }
}
