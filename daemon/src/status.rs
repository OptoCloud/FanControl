//! The status snapshot and the hub that hands it to API consumers.
//!
//! The daemon keeps exactly one snapshot: the latest. There is no history here by design;
//! consumers that want history subscribe to the event stream and keep their own.

use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::time::{Duration, Instant};
pub use vigil_protocol::{Snapshot, now_rfc3339};

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

#[cfg(test)]
mod tests {
    use super::*;

    fn snapshot(healthy: bool) -> Snapshot {
        Snapshot { timestamp_utc: "t".to_owned(), sensors: vec![], fans: vec![], drive_health: vec![], control_loop_healthy: healthy }
    }

    const FRESH: Duration = Duration::from_secs(3600);

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
