//! The status snapshot and the hub that hands it to API consumers.
//!
//! The daemon keeps exactly one snapshot: the latest. There is no history here by design;
//! consumers that want history subscribe to the event stream and keep their own.

use crate::fans::FanStatus;
use crate::sensors::SensorReading;
use crate::smart::DriveHealth;
use serde::Serialize;
use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

/// The full read-only view of daemon state, serialized as-is. This is the contract
/// consumers build on: treat a field rename here as a breaking change.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    pub timestamp_utc: String,
    pub sensors: Vec<SensorReading>,
    pub fans: Vec<FanStatus>,
    pub drive_health: Vec<DriveHealth>,
    /// False when any channel couldn't be driven this poll, or (judged at read time) when
    /// this snapshot is older than the deadman timeout, i.e. the loop has hung.
    pub control_loop_healthy: bool,
}

/// Latest-value broadcast: the control loop publishes, any number of consumers read or
/// wait. Each snapshot is serialized once, and every consumer shares that one string.
pub struct StatusHub {
    state: Mutex<HubState>,
    published: Condvar,
}

struct HubState {
    generation: u64,
    latest: Option<Published>,
}

struct Published {
    snapshot: Snapshot,
    json: Arc<str>,
    at: Instant,
}

impl StatusHub {
    pub fn new() -> Self {
        Self { state: Mutex::new(HubState { generation: 0, latest: None }), published: Condvar::new() }
    }

    pub fn publish(&self, snapshot: Snapshot) {
        let json: Arc<str> = serde_json::to_string(&snapshot).unwrap_or_else(|_| "{}".to_owned()).into();

        let mut state = self.lock();
        state.generation += 1;
        state.latest = Some(Published { snapshot, json, at: Instant::now() });
        self.published.notify_all();
    }

    /// The latest snapshot as JSON with the generation it belongs to, or None before the
    /// first poll. One older than `stale_after` comes back with controlLoopHealthy forced
    /// to false: a loop that has hung outright can't mark its own last snapshot unhealthy.
    pub fn latest(&self, stale_after: Duration) -> Option<(u64, Arc<str>)> {
        let state = self.lock();
        let published = state.latest.as_ref()?;

        if published.snapshot.control_loop_healthy && published.at.elapsed() > stale_after {
            let stale = Snapshot { control_loop_healthy: false, ..published.snapshot.clone() };
            return Some((state.generation, serde_json::to_string(&stale).ok()?.into()));
        }

        Some((state.generation, Arc::clone(&published.json)))
    }

    /// Blocks until a snapshot newer than `after_generation` is published, or `timeout` passes (None).
    pub fn wait_for_newer(&self, after_generation: u64, timeout: Duration) -> Option<(u64, Arc<str>)> {
        let deadline = Instant::now() + timeout;
        let mut state = self.lock();

        while state.generation <= after_generation {
            let remaining = deadline.checked_duration_since(Instant::now()).filter(|d| !d.is_zero())?;
            state = self.published.wait_timeout(state, remaining).map_or_else(|e| e.into_inner().0, |(guard, _)| guard);
        }

        let published = state.latest.as_ref()?;
        Some((state.generation, Arc::clone(&published.json)))
    }

    fn lock(&self) -> MutexGuard<'_, HubState> {
        self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }
}

/// Current time as RFC 3339 UTC with millisecond precision, e.g. "2026-09-21T20:39:27.482Z".
pub fn now_rfc3339() -> String {
    rfc3339(SystemTime::now())
}

pub fn rfc3339(time: SystemTime) -> String {
    let since_epoch = time.duration_since(UNIX_EPOCH).unwrap_or_default();
    let seconds = since_epoch.as_secs();
    let (days, second_of_day) = (seconds / 86_400, seconds % 86_400);

    // Days since 1970-01-01 to a proleptic Gregorian date (Howard Hinnant's civil_from_days).
    let shifted = days + 719_468;
    let era = shifted / 146_097;
    let day_of_era = shifted % 146_097;
    let year_of_era = (day_of_era - day_of_era / 1_460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let month_index = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * month_index + 2) / 5 + 1;
    let month = if month_index < 10 { month_index + 3 } else { month_index - 9 };
    let year = year_of_era + era * 400 + u64::from(month <= 2);

    format!(
        "{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}.{:03}Z",
        second_of_day / 3_600,
        second_of_day % 3_600 / 60,
        second_of_day % 60,
        since_epoch.subsec_millis()
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn snapshot(healthy: bool) -> Snapshot {
        Snapshot { timestamp_utc: "t".to_owned(), sensors: vec![], fans: vec![], drive_health: vec![], control_loop_healthy: healthy }
    }

    const FRESH: Duration = Duration::from_secs(3600);

    #[test]
    fn formats_known_instants() {
        let at = |seconds: u64, millis: u32| UNIX_EPOCH + Duration::new(seconds, millis * 1_000_000);

        assert_eq!(rfc3339(at(0, 0)), "1970-01-01T00:00:00.000Z");
        assert_eq!(rfc3339(at(951_782_400, 0)), "2000-02-29T00:00:00.000Z"); // leap day
        assert_eq!(rfc3339(at(1_790_023_167, 482)), "2026-09-21T20:39:27.482Z");
        assert_eq!(rfc3339(at(4_102_444_799, 999)), "2099-12-31T23:59:59.999Z");
    }

    #[test]
    fn empty_until_first_publish_then_serves_the_latest() {
        let hub = StatusHub::new();
        assert!(hub.latest(FRESH).is_none());

        hub.publish(snapshot(true));
        hub.publish(snapshot(false));
        let (generation, json) = hub.latest(FRESH).unwrap();

        assert_eq!(generation, 2);
        assert!(json.contains(r#""controlLoopHealthy":false"#));
    }

    #[test]
    fn serializes_with_the_documented_field_names() {
        let hub = StatusHub::new();
        hub.publish(snapshot(true));

        let (_, json) = hub.latest(FRESH).unwrap();

        assert_eq!(&*json, r#"{"timestampUtc":"t","sensors":[],"fans":[],"driveHealth":[],"controlLoopHealthy":true}"#);
    }

    #[test]
    fn a_stale_snapshot_is_reported_unhealthy() {
        let hub = StatusHub::new();
        hub.publish(snapshot(true));
        std::thread::sleep(Duration::from_millis(30));

        let (_, json) = hub.latest(Duration::from_millis(1)).unwrap();

        assert!(json.contains(r#""controlLoopHealthy":false"#));
    }

    #[test]
    fn wait_for_newer_times_out_without_a_publish_and_wakes_on_one() {
        let hub = Arc::new(StatusHub::new());
        hub.publish(snapshot(true));

        assert!(hub.wait_for_newer(1, Duration::from_millis(30)).is_none());
        assert_eq!(hub.wait_for_newer(0, Duration::from_millis(30)).unwrap().0, 1);

        let publisher = Arc::clone(&hub);
        let thread = std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(50));
            publisher.publish(snapshot(true));
        });

        assert_eq!(hub.wait_for_newer(1, Duration::from_secs(10)).unwrap().0, 2);
        thread.join().unwrap();
    }
}
