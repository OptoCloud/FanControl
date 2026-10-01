//! The status snapshot and the hub that hands it to API consumers.
//!
//! The daemon keeps exactly one snapshot: the latest. There is no history here by design;
//! consumers that want history subscribe to the event stream and keep their own.
//!
//! The hub bridges the synchronous control loop to the asynchronous API. `publish` is called
//! from the control loop and never blocks or needs a runtime, because that is all
//! `broadcast::Sender::send` does; the API task awaits on its receiver.

use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};
use tokio::sync::broadcast;
pub use vigil_protocol::{Snapshot, now_rfc3339};

/// How many snapshots a subscriber may fall behind by before it starts missing them. At a
/// two-second poll that is two minutes of slack — far more than a merely busy consumer needs,
/// and this is a latest-value API, so a dropped snapshot is superseded rather than lost.
const SNAPSHOT_BACKLOG: usize = 64;

/// Latest-value broadcast: the control loop publishes, any number of consumers read or
/// subscribe. Each snapshot is serialized once and every consumer shares that one string.
pub struct StatusHub {
    latest: Mutex<Option<Published>>,
    updates: broadcast::Sender<Arc<str>>,
}

struct Published {
    snapshot: Snapshot,
    json: Arc<str>,
    at: Instant,
}

impl StatusHub {
    pub fn new() -> Self {
        let (updates, _) = broadcast::channel(SNAPSHOT_BACKLOG);
        Self { latest: Mutex::new(None), updates }
    }

    pub fn publish(&self, snapshot: Snapshot) {
        let json: Arc<str> = serde_json::to_string(&snapshot).unwrap_or_else(|_| "{}".to_owned()).into();

        *self.lock() = Some(Published { snapshot, json: Arc::clone(&json), at: Instant::now() });
        // An error only means nobody is subscribed, which is the normal case.
        let _ = self.updates.send(json);
    }

    /// The latest snapshot as JSON, or None before the first poll. One older than
    /// `stale_after` comes back with controlLoopHealthy forced to false: a loop that has hung
    /// outright cannot mark its own last snapshot unhealthy.
    pub fn latest(&self, stale_after: Duration) -> Option<Arc<str>> {
        let state = self.lock();
        let published = state.as_ref()?;

        if published.snapshot.control_loop_healthy && published.at.elapsed() > stale_after {
            let stale = Snapshot { control_loop_healthy: false, ..published.snapshot.clone() };
            return Some(serde_json::to_string(&stale).ok()?.into());
        }

        Some(Arc::clone(&published.json))
    }

    /// A receiver for every snapshot published from now on.
    pub fn subscribe(&self) -> broadcast::Receiver<Arc<str>> {
        self.updates.subscribe()
    }

    /// The control loop must not die because a consumer thread panicked holding the lock.
    fn lock(&self) -> MutexGuard<'_, Option<Published>> {
        self.latest.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }
}

impl Default for StatusHub {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const FRESH: Duration = Duration::from_secs(3600);

    fn snapshot(timestamp: &str, healthy: bool) -> Snapshot {
        Snapshot { timestamp_utc: timestamp.to_owned(), sensors: vec![], fans: vec![], drive_health: vec![], control_loop_healthy: healthy }
    }

    #[test]
    fn empty_until_first_publish_then_serves_the_latest() {
        let hub = StatusHub::new();
        assert!(hub.latest(FRESH).is_none());

        hub.publish(snapshot("first", true));
        assert!(hub.latest(FRESH).unwrap().contains("first"));

        hub.publish(snapshot("second", true));
        assert!(hub.latest(FRESH).unwrap().contains("second"));
    }

    #[test]
    fn a_stale_snapshot_is_reported_unhealthy() {
        let hub = StatusHub::new();
        hub.publish(snapshot("t", true));

        // Nothing has hung, so the snapshot is served as published.
        assert!(hub.latest(FRESH).unwrap().contains(r#""controlLoopHealthy":true"#));
        // Judged stale at read time: the loop cannot have marked this itself.
        assert!(hub.latest(Duration::ZERO).unwrap().contains(r#""controlLoopHealthy":false"#));
    }

    #[test]
    fn a_subscriber_receives_every_snapshot_published_after_it_subscribed() {
        let hub = StatusHub::new();
        hub.publish(snapshot("before", true));

        let mut updates = hub.subscribe();
        hub.publish(snapshot("after", true));

        // try_recv is enough: send completes before it returns, so nothing is in flight.
        let received = updates.try_recv().expect("the snapshot published after subscribing");
        assert!(received.contains("after"));
        assert!(updates.try_recv().is_err(), "only the one");
    }
}
