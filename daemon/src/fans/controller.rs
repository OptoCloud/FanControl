//! The sysfs PWM controller: the only code in vigil that writes a fan header.
//!
//! One failing channel must not take the others down, so a write that fails is reported and
//! the channel handed back to automatic control, while the rest keep being driven.

use super::{FanChannel, FanController, FanStatus, PwmMode, lock};
use crate::sysfs::{self, SysFs};
use std::collections::HashMap;
use std::io;
use std::sync::{Arc, Mutex};

pub struct SysfsFanController {
    sysfs: Arc<dyn SysFs>,
    /// Whatever mode each channel was in before this daemon first touched it.
    original_modes: Mutex<HashMap<String, PwmMode>>,
}

impl SysfsFanController {
    pub fn new(sysfs: Arc<dyn SysFs>) -> Self {
        Self { sysfs, original_modes: Mutex::new(HashMap::new()) }
    }

    fn read_mode(&self, channel: &FanChannel) -> Option<PwmMode> {
        PwmMode::from_raw(&self.sysfs.read(&channel.enable_path())?)
    }
}

impl FanController for SysfsFanController {
    fn take_manual_control(&self, channel: &FanChannel) -> io::Result<()> {
        let enable_path = channel.enable_path();
        {
            // Only the first sighting counts: this is re-asserted every poll, and later
            // reads would just see our own "1".
            let mut original_modes = lock(&self.original_modes);
            if !original_modes.contains_key(&enable_path)
                && let Some(mode) = self.read_mode(channel)
            {
                original_modes.insert(enable_path.clone(), mode);
            }
        }

        self.sysfs.write(&enable_path, &(PwmMode::Manual as u8).to_string())
    }

    fn set_duty_percent(&self, channel: &FanChannel, duty_percent: u8) -> io::Result<()> {
        if duty_percent > 100 {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, format!("duty must be 0-100 (is {duty_percent})")));
        }

        let raw = (f64::from(duty_percent) / 100.0 * 255.0).round() as u8;
        self.sysfs.write(&channel.pwm_path(), &raw.to_string())
    }

    fn release_to_auto(&self, channel: &FanChannel) -> io::Result<()> {
        // Only ever restore a mode where the chip regulates the fan. If the channel was
        // found already on Manual (a previous instance died without releasing it) or
        // Disabled, restoring that would recreate the stuck-fan situation release exists
        // to prevent, so fall back to the board's shipping default instead.
        let mode = lock(&self.original_modes)
            .get(&channel.enable_path())
            .copied()
            .filter(|mode| mode.is_automatic())
            .unwrap_or(PwmMode::SmartFanIV);

        self.sysfs.write(&channel.enable_path(), &(mode as u8).to_string())
    }

    fn read_status(&self, channel: &FanChannel) -> FanStatus {
        let raw_pwm = self.sysfs.read(&channel.pwm_path()).and_then(|raw| raw.parse::<u8>().ok());

        FanStatus {
            id: channel.id.clone(),
            duty_percent: raw_pwm.map_or(0, |pwm| (f64::from(pwm) / 255.0 * 100.0).round() as u8),
            rpm: self.sysfs.read(&channel.tach_path()).and_then(|raw| raw.parse().ok()),
            mode: self.read_mode(channel),
            stalled: false,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::fans::test_support::{channel, setup};

    #[test]
    fn take_manual_control_writes_mode_one_and_release_writes_five() {
        let cpu = channel("cpu", 1);
        let (sysfs, controller) = setup(&[&cpu]);

        controller.take_manual_control(&cpu).unwrap();
        assert_eq!(sysfs.get(&cpu.enable_path()), "1");

        controller.release_to_auto(&cpu).unwrap();
        assert_eq!(sysfs.get(&cpu.enable_path()), "5");
    }

    #[test]
    fn set_duty_percent_converts_to_raw_pwm_scale_and_rejects_out_of_range() {
        let cpu = channel("cpu", 1);
        let (sysfs, controller) = setup(&[&cpu]);

        for (duty, raw) in [(0, "0"), (50, "128"), (100, "255")] {
            controller.set_duty_percent(&cpu, duty).unwrap();
            assert_eq!(sysfs.get(&cpu.pwm_path()), raw);
        }

        assert!(controller.set_duty_percent(&cpu, 101).is_err());
    }

    #[test]
    fn release_restores_the_automatic_mode_the_channel_was_originally_in() {
        for original in ["2", "3", "5"] {
            let cpu = channel("cpu", 1);
            let (sysfs, controller) = setup(&[&cpu]);
            sysfs.set(&cpu.enable_path(), original);

            controller.take_manual_control(&cpu).unwrap();
            controller.take_manual_control(&cpu).unwrap(); // re-asserted every poll; must not overwrite the remembered original with "1"
            controller.release_to_auto(&cpu).unwrap();

            assert_eq!(sysfs.get(&cpu.enable_path()), original);
        }
    }

    #[test]
    fn release_never_restores_a_non_automatic_original_mode() {
        // "1": left on manual by a previous instance that died without releasing.
        for original in ["0", "1", "garbage"] {
            let cpu = channel("cpu", 1);
            let (sysfs, controller) = setup(&[&cpu]);
            sysfs.set(&cpu.enable_path(), original);

            controller.take_manual_control(&cpu).unwrap();
            controller.release_to_auto(&cpu).unwrap();

            assert_eq!(sysfs.get(&cpu.enable_path()), "5");
        }
    }

    #[test]
    fn read_status_reports_duty_rpm_and_mode() {
        let cpu = channel("cpu", 1);
        let (sysfs, controller) = setup(&[&cpu]);
        sysfs.set(&cpu.pwm_path(), "51"); // ~20%
        sysfs.set(&cpu.tach_path(), "0");
        sysfs.set(&cpu.enable_path(), "1");

        let status = controller.read_status(&cpu);

        assert_eq!((status.duty_percent, status.rpm, status.mode), (20, Some(0), Some(PwmMode::Manual)));
    }

    #[test]
    fn read_status_reports_unreadable_mode_and_tach_as_none_rather_than_guessing() {
        let cpu = channel("cpu", 1);
        let (sysfs, controller) = setup(&[&cpu]);
        sysfs.remove(&cpu.enable_path());

        let status = controller.read_status(&cpu);

        assert_eq!((status.mode, status.rpm), (None, None));
    }

    // ---- stall detector ----
}
