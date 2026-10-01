//! vigild: vigil's hardware daemon. Reads the host's temperatures and drive health, and drives its fans.
//!
//! Deliberately holds as little state as it can: the latest snapshot, plus what the control
//! algorithm itself needs from one poll to the next (hysteresis anchors, stall counters).
//! History, trends and alerting belong to whatever consumes the status API.

// A panic in a test IS the failure report; the lint is aimed at production paths.
#![cfg_attr(test, allow(clippy::unwrap_used, clippy::expect_used))]
// The daemon only runs on Linux but is developed and unit-tested on Windows, where the
// socket server and the HBA ioctl are compiled out and their pure helpers look unused.
#![cfg_attr(not(target_os = "linux"), allow(dead_code))]

mod conditions;
mod config;
mod control;
mod curve;
mod fans;
mod gpu;
mod hba;
mod logging;
mod mpt3;
mod notify;
mod process;
mod protocol;
mod sensors;
mod server;
mod smart;
mod status;
mod sysfs;

use config::Config;
use control::ControlLoop;
use curve::{FanCurve, Member};
use fans::{FanSafetyGuard, SysfsFanController};
use sensors::{DEFAULT_WHITELIST, HwmonResolver, SensorCategory, SensorReading};
use status::{Snapshot, StatusHub};
use std::process::ExitCode;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use sysfs::{LinuxSysFs, SysFs};
use tracing::{error, info, warn};

/// Overridden by RUST_LOG. Info is the right default: the control loop logs transitions,
/// not every poll, so this stays quiet on a healthy host.
const DEFAULT_LOG_FILTER: &str = "info";

const DEFAULT_CONFIG_PATH: &str = "/etc/vigil/vigild.toml";

/// Set by SIGTERM/SIGINT. Everything that waits does so in short slices and checks this.
static SHUTDOWN: AtomicBool = AtomicBool::new(false);

#[allow(clippy::print_stdout, reason = "--help/--check report to stdout for a human; log lines go to stderr via log!()")]
fn main() -> ExitCode {
    // First, before anything can want to log: tracing drops every record silently until a
    // subscriber is installed.
    logging::init(DEFAULT_LOG_FILTER);

    let args: Vec<String> = std::env::args().skip(1).collect();
    let check_only = args.iter().any(|arg| arg == "--check");
    let config_path =
        args.iter().position(|arg| arg == "--config").and_then(|i| args.get(i + 1)).map_or(DEFAULT_CONFIG_PATH, String::as_str);

    if args.iter().any(|arg| arg == "--help" || arg == "-h") {
        println!(
            "Usage: vigild [--config PATH] [--check]\n\n  --config PATH  config file (default {DEFAULT_CONFIG_PATH})\n  --check        validate the config and exit without touching any fan"
        );
        return ExitCode::SUCCESS;
    }

    // Any failure here exits non-zero, so under systemd it counts as a failure and
    // Restart=on-failure applies (the usual cause, nct6775 not loaded yet at boot, is
    // exactly what a restart a few seconds later fixes).
    match run(config_path, check_only) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            error!("{error}");
            ExitCode::FAILURE
        }
    }
}

#[allow(clippy::print_stdout, reason = "--help/--check report to stdout for a human; log lines go to stderr via log!()")]
fn run(config_path: &str, check_only: bool) -> Result<(), String> {
    let config_text = std::fs::read_to_string(config_path).map_err(|e| format!("Cannot read config file {config_path}: {e}"))?;
    let config = Config::parse(&config_text).map_err(|e| format!("Cannot parse config file {config_path}: {e}"))?;

    // Refuse to start on a bad config, before any fan has been touched.
    let errors = config.validate();
    if !errors.is_empty() {
        return Err(format!("Invalid configuration in {config_path}:\n  - {}", errors.join("\n  - ")));
    }

    if check_only {
        println!("{config_path}: OK ({} fan channel(s), {} curve(s))", config.channels.len(), config.curves.len());
        report_curve_inputs(&config, &LinuxSysFs);
        return Ok(());
    }

    install_signal_handlers();

    let sysfs: Arc<dyn SysFs> = Arc::new(LinuxSysFs);
    let controller = Arc::new(SysfsFanController::new(Arc::clone(&sysfs)));
    let mut control = ControlLoop::new(&config, Arc::clone(&sysfs), controller.clone())?;

    // From here on every exit path, a panic included, unwinds through this guard's Drop,
    // which hands every channel back to automatic control.
    let guard = FanSafetyGuard::new(controller, control.channels(), config.deadman_timeout()).map_err(|e| e.to_string())?;

    let hub = Arc::new(StatusHub::new());
    let drive_health = Arc::new(Mutex::new(Vec::new()));

    start_status_api(&config, &hub)?;
    if config.drive_health.enabled {
        spawn_drive_health_poller(&config, Arc::clone(&sysfs), Arc::clone(&drive_health));
    } else {
        info!("Drive health polling disabled via config.");
    }

    let mut gpu = gpu::GpuTemperatureProvider::new(config.gpu.clone());
    let mut hba = hba::HbaTemperatureProvider::new(config.hba.clone());

    info!("Control loop starting: {} fan channel(s), poll every {:?}.", config.channels.len(), config.poll_interval());
    notify::ready();

    let mut next_poll = Instant::now();
    while !SHUTDOWN.load(Ordering::Relaxed) {
        let result = control.poll(vec![gpu.read(), hba.read()]);

        // Kicked even if an individual channel failed: that channel was handed back to
        // automatic control on its own, and the loop itself is demonstrably alive. Not
        // kicking would make the deadman release the healthy channels too.
        guard.kick(config.deadman_timeout());
        notify::watchdog();

        hub.publish(Snapshot {
            timestamp_utc: status::now_rfc3339(),
            sensors: result.readings,
            fans: result.fans,
            drive_health: drive_health.lock().unwrap_or_else(|e| e.into_inner()).clone(),
            control_loop_healthy: result.healthy,
        });

        // Fixed cadence; after an overrun the next poll runs right away rather than bunching up.
        next_poll = (next_poll + config.poll_interval()).max(Instant::now());
        sleep_until(next_poll);
    }

    info!("Shutting down: releasing every fan channel back to automatic control.");
    notify::stopping();
    drop(guard);

    if config.api.enabled {
        let _ = std::fs::remove_file(&config.api.socket_path);
    }

    Ok(())
}

#[cfg(unix)]
fn start_status_api(config: &Config, hub: &Arc<StatusHub>) -> Result<(), String> {
    if !config.api.enabled {
        info!("Status API disabled via config.");
        return Ok(());
    }

    // A snapshot older than the deadman timeout means the loop has hung.
    server::spawn(&config.api, Arc::clone(hub), config.deadman_timeout(), &SHUTDOWN)
        .map_err(|e| format!("Cannot listen on unix socket {}: {e}", config.api.socket_path))
}

#[cfg(not(unix))]
fn start_status_api(_config: &Config, _hub: &Arc<StatusHub>) -> Result<(), String> {
    warn!("The status API needs unix sockets and is unavailable on this platform.");
    Ok(())
}

/// For --check: what every curve would read on this machine. Resolution only
/// lists hwmon and the by-path links: no temperature is read and no fan is touched, so a
/// zone config can be checked against the real drives before it goes live.
#[allow(clippy::print_stdout, reason = "--help/--check report to stdout for a human; log lines go to stderr via log!()")]
fn report_curve_inputs(config: &Config, sysfs: &dyn SysFs) {
    let resolved = HwmonResolver::new(sysfs, &config.sysfs_root).with_by_path_dir(&config.disk_by_path_dir).resolve(&DEFAULT_WHITELIST);
    if resolved.is_empty() {
        println!("No hwmon sensors under {}: not on the target machine, so curve inputs are not checked.", config.sysfs_root);
        return;
    }

    let mut present: Vec<SensorReading> = resolved
        .iter()
        .map(|sensor| {
            SensorReading::new(&sensor.id, sensor.category, &sensor.label, None, &sensor.temp_input_path).with_port(sensor.port.clone())
        })
        .collect();
    // These two come from nvidia-smi and the HBA ioctl rather than hwmon, and aren't probed here.
    if config.gpu.enabled {
        present.push(SensorReading::new("gpu", SensorCategory::Gpu, "GPU", None, "nvidia-smi"));
    }
    if config.hba.enabled {
        present.push(SensorReading::new("hba", SensorCategory::Hba, "LSI HBA", None, "mpt3ctl"));
    }

    for curve_config in &config.curves {
        let curve = FanCurve::from_config(curve_config, &config.zones);
        println!();
        report_input(&curve.fan_channel_id, &curve.members, &present);
    }
}

#[allow(clippy::print_stdout, reason = "--help/--check report to stdout for a human; log lines go to stderr via log!()")]
fn report_input(name: &str, members: &[Member], present: &[SensorReading]) {
    println!("{name}:");
    for reading in present.iter().filter(|reading| members.iter().any(|member| member.matches(reading))) {
        match &reading.port {
            Some(port) => println!("  {} on {port}", reading.id),
            None => println!("  {}", reading.id),
        }
    }

    for member in members.iter().filter(|member| !present.iter().any(|reading| member.matches(reading))) {
        match member {
            Member::Named(_) => println!("  WARNING: {member} matches nothing here; this input will run at its fail-safe duty."),
            Member::Wildcard { .. } => println!("  WARNING: {member} matches nothing here."),
        }
    }
}

/// SMART health on its own slow cycle, completely decoupled from the control loop. Drives
/// are re-resolved on every cycle, so this follows hot-swaps and sdX reshuffles on its own.
fn spawn_drive_health_poller(config: &Config, sysfs: Arc<dyn SysFs>, results: Arc<Mutex<Vec<smart::DriveHealth>>>) {
    let sysfs_root = config.sysfs_root.clone();
    let by_path_dir = config.disk_by_path_dir.clone();
    let config = config.drive_health.clone();
    info!("Drive health polling every {:?}.", Duration::from_secs_f64(config.poll_interval_secs));

    let spawned = std::thread::Builder::new().name("drive-health".to_owned()).spawn(move || {
        while !SHUTDOWN.load(Ordering::Relaxed) {
            let drives = HwmonResolver::new(&*sysfs, &sysfs_root).with_by_path_dir(&by_path_dir).resolve(&[sensors::DRIVE_SPEC]);
            let health = smart::poll_all(&config, &drives, &status::now_rfc3339());

            for drive in health.iter().filter(|drive| drive.passed == Some(false)) {
                warn!("Drive {} ({}) FAILED its SMART overall-health self-assessment.", drive.device_name, drive.source_path);
            }

            *results.lock().unwrap_or_else(|e| e.into_inner()) = health;
            sleep_until(Instant::now() + Duration::from_secs_f64(config.poll_interval_secs));
        }
    });

    if let Err(error) = spawned {
        error!("Cannot start the drive health poller; drive health will be unavailable: {error}");
    }
}

/// Sleeps in short slices so a shutdown signal is acted on promptly.
fn sleep_until(deadline: Instant) {
    while !SHUTDOWN.load(Ordering::Relaxed) {
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return;
        }
        std::thread::sleep(remaining.min(Duration::from_millis(100)));
    }
}

#[cfg(unix)]
fn install_signal_handlers() {
    extern "C" fn request_shutdown(_signal: libc::c_int) {
        // Storing to an atomic is one of the few things that is safe inside a signal handler.
        SHUTDOWN.store(true, Ordering::Relaxed);
    }

    // SAFETY: the handler is async-signal-safe (a single atomic store) and lives for the whole program.
    #[allow(unsafe_code, reason = "audited: signal handler installation, SAFETY above")]
    unsafe {
        libc::signal(libc::SIGTERM, request_shutdown as *const () as libc::sighandler_t);
        libc::signal(libc::SIGINT, request_shutdown as *const () as libc::sighandler_t);
        // A consumer disconnecting mid-write must surface as a write error, not kill the daemon.
        libc::signal(libc::SIGPIPE, libc::SIG_IGN);
    }
}

#[cfg(not(unix))]
fn install_signal_handlers() {}
