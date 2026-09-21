//! GPU temperature via nvidia-smi rather than an hwmon node: nvidia's hwmon exposure
//! varies by driver version/packaging, while --query-gpu is a stable, documented
//! interface. Staying out-of-process (rather than binding NVML) is also deliberate: a
//! wedged driver can only hang the child, which gets killed on timeout, never the control
//! loop's own thread.

use crate::config::GpuConfig;
use crate::process;
use crate::sensors::{SensorCategory, SensorReading};
use std::time::{Duration, Instant};

pub struct GpuTemperatureProvider {
    config: GpuConfig,
    /// Spawning a process is far heavier than a sysfs read, so a result is reused for
    /// min_read_interval instead of being re-queried on every poll.
    last: Option<(Instant, SensorReading)>,
}

impl GpuTemperatureProvider {
    pub fn new(config: GpuConfig) -> Self {
        Self { config, last: None }
    }

    pub fn read(&mut self) -> SensorReading {
        if !self.config.enabled {
            return self.reading(None);
        }

        let reuse_for = Duration::from_secs_f64(self.config.min_read_interval_secs);
        if let Some((read_at, reading)) = &self.last
            && read_at.elapsed() < reuse_for
        {
            return reading.clone();
        }

        let output = process::run(
            &self.config.nvidia_smi_path,
            &["--query-gpu=temperature.gpu", "--format=csv,noheader"],
            Duration::from_secs_f64(self.config.timeout_secs),
        );

        // None covers nvidia-smi missing (driver not installed) and a hang past the
        // timeout alike: report as absent, not fatal.
        let reading = self.reading(output.filter(|o| o.exit_code == Some(0)).and_then(|o| parse_celsius(&o.stdout)));
        self.last = Some((Instant::now(), reading.clone()));
        reading
    }

    fn reading(&self, celsius: Option<f64>) -> SensorReading {
        SensorReading::new("gpu", SensorCategory::Gpu, "GPU", celsius, &self.config.nvidia_smi_path)
    }
}

/// With several GPUs nvidia-smi prints one line each; the hottest is the one that matters
/// to a case fan.
fn parse_celsius(stdout: &str) -> Option<f64> {
    stdout.lines().filter_map(|line| line.trim().parse::<f64>().ok()).filter(|celsius| celsius.is_finite()).reduce(f64::max)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_single_and_multi_gpu_output() {
        assert_eq!(parse_celsius("37\n"), Some(37.0));
        assert_eq!(parse_celsius("37\r\n52\n"), Some(52.0));
    }

    #[test]
    fn rejects_output_that_is_not_a_temperature() {
        assert_eq!(parse_celsius(""), None);
        assert_eq!(parse_celsius("[N/A]\n"), None);
        assert_eq!(parse_celsius("NaN\n"), None);
    }

    #[test]
    fn disabled_provider_is_unavailable_without_spawning_anything() {
        let mut provider =
            GpuTemperatureProvider::new(GpuConfig { enabled: false, nvidia_smi_path: "/nonexistent".to_owned(), ..GpuConfig::default() });

        let reading = provider.read();

        assert_eq!((reading.id.as_str(), reading.is_available), ("gpu", false));
    }

    #[test]
    fn missing_nvidia_smi_is_unavailable_not_fatal() {
        let mut provider =
            GpuTemperatureProvider::new(GpuConfig { nvidia_smi_path: "definitely-not-nvidia-smi-4f1c".to_owned(), ..GpuConfig::default() });

        assert!(!provider.read().is_available);
    }
}
