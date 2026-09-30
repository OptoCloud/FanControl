//! vigil-core's heart: one thread that owns all state. vigild's snapshots, the UPS readings
//! and new live subscribers arrive as messages on one channel; history goes to Postgres,
//! events to the log, the database and ntfy, and every change to the live subscribers.
//! One owner means no locks, and the order of events is the order they happened in.
//!
//! The live view must work without a database, so a database failure is retried, never fatal.

use crate::alerts::{ConditionTracker, NewEvent, conditions_in, diff_drive_health, ups_conditions};
use crate::config::Config;
use crate::daemon::DaemonEvent;
use crate::{db, log, notify};
use postgres::Client;
use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;
use std::sync::mpsc::{Receiver, RecvTimeoutError, SyncSender, TrySendError};
use std::time::{Duration, Instant, SystemTime};
use vigil_protocol::{DriveState, EventRecord, LiveMessage, Severity, Snapshot, UpsReading, UpsState, now_rfc3339};

pub enum Input {
    Daemon(DaemonEvent),
    Ups(Box<Result<UpsReading, String>>),
    /// A live subscriber: gets the current state at once, then every change as a JSON line.
    Subscribe(SyncSender<Arc<str>>),
}

const DATABASE_RETRY: Duration = Duration::from_secs(15);
const INVENTORY_EVERY: Duration = Duration::from_secs(60);

pub struct Runtime {
    config: Config,
    database: Option<Client>,
    next_database_attempt: Instant,
    last_database_error: String,

    latest: Option<Snapshot>,
    daemon_connected: bool,
    daemon_lost_since: Option<(Instant, String)>,
    daemon_lost_raised: bool,
    drives: BTreeMap<String, DriveState>,
    ups: UpsState,
    ups_unreadable_since: Option<Instant>,

    tracker: ConditionTracker,
    // The UPS reports its own state, so one poll is enough: an OB flag is a real power event, not a flaky read.
    ups_tracker: ConditionTracker,
    known_sensor_ids: BTreeSet<String>,
    last_drive_health_as_of: String,
    last_persisted: Option<Instant>,
    last_ups_persisted: Option<Instant>,
    last_inventory: Option<Instant>,

    subscribers: Vec<SyncSender<Arc<str>>>,
}

impl Runtime {
    pub fn new(config: Config) -> Self {
        let ups = UpsState { enabled: config.nut.is_some(), name: config.ups_name.clone(), reading: None, error: None };
        Self {
            config,
            database: None,
            next_database_attempt: Instant::now(),
            last_database_error: String::new(),
            latest: None,
            daemon_connected: false,
            daemon_lost_since: Some((Instant::now(), "not connected yet".to_owned())),
            daemon_lost_raised: false,
            drives: BTreeMap::new(),
            ups,
            ups_unreadable_since: Some(Instant::now()),
            tracker: ConditionTracker::new(3),
            ups_tracker: ConditionTracker::new(1),
            known_sensor_ids: BTreeSet::new(),
            last_drive_health_as_of: String::new(),
            last_persisted: None,
            last_ups_persisted: None,
            last_inventory: None,
            subscribers: Vec::new(),
        }
    }

    /// Runs until every sender is gone.
    pub fn run(mut self, inputs: Receiver<Input>) {
        loop {
            match inputs.recv_timeout(Duration::from_secs(1)) {
                Ok(input) => self.handle(input),
                Err(RecvTimeoutError::Timeout) => {}
                Err(RecvTimeoutError::Disconnected) => return,
            }
            // Everything already queued goes first, so the time-based checks in tick() judge on
            // the latest news. Preparing the database can take a while (filling the aggregates
            // the first time), and a "connected to vigild" that arrived meanwhile must be seen
            // before an outage is declared.
            while let Ok(input) = inputs.try_recv() {
                self.handle(input);
            }
            self.tick();
        }
    }

    fn handle(&mut self, input: Input) {
        match input {
            Input::Daemon(DaemonEvent::Snapshot(snapshot)) => self.handle_snapshot(*snapshot),
            Input::Daemon(DaemonEvent::Connected) => self.handle_daemon_connection(true, None),
            Input::Daemon(DaemonEvent::Disconnected(reason)) => self.handle_daemon_connection(false, Some(reason)),
            Input::Ups(result) => self.handle_ups(*result),
            Input::Subscribe(subscriber) => self.subscribe(subscriber),
        }
    }

    fn tick(&mut self) {
        // A daemon restart drops the stream for a second or two; only a lasting outage is news.
        if let Some((since, reason)) = &self.daemon_lost_since
            && !self.daemon_lost_raised
            && since.elapsed() >= self.config.daemon_lost_after
        {
            self.daemon_lost_raised = true;
            let message = format!("vigild is unreachable ({reason}). If it is not running, the fans are on BIOS control.");
            self.raise(NewEvent { severity: Severity::Critical, kind: "daemon-lost".to_owned(), message });
        }

        // The unreadable-UPS condition depends on time, not only on the next poll.
        if self.ups.enabled && self.ups.reading.is_none() {
            self.evaluate_ups();
        }

        // Last, because it may block for a while; run() handles what queued up meanwhile first.
        if self.database.is_none() && Instant::now() >= self.next_database_attempt {
            self.prepare_database();
        }
    }

    fn prepare_database(&mut self) {
        let result = db::connect(&self.config.database_url).and_then(|mut client| {
            // Rollups, compression and retention are TimescaleDB's own jobs from here on.
            db::migrate(&mut client, self.config.raw_retention)?;
            let drives = db::load_drives(&mut client).map_err(|e| db::describe(&e))?;
            Ok((client, drives))
        });

        match result {
            Ok((client, drives)) => {
                for drive in drives {
                    self.drives.entry(drive.wwn.clone()).or_insert(drive);
                }
                self.database = Some(client);
                self.last_database_error.clear();
                log!(Info, "database ready");
                self.broadcast(&LiveMessage::Drives { drives: self.drive_list() });
            }
            Err(error) => {
                self.report_database_error("preparing the database", &error);
                self.next_database_attempt = Instant::now() + DATABASE_RETRY;
            }
        }
    }

    /// Runs `work` if the database is up. A failure is logged once per distinct error, and a
    /// connection that broke is dropped so the next tick reconnects.
    fn with_database<T>(&mut self, doing: &str, work: impl FnOnce(&mut Client) -> db::Result<T>) -> Option<T> {
        let client = self.database.as_mut()?;
        match work(client) {
            Ok(value) => Some(value),
            Err(error) => {
                if client.is_closed() {
                    self.database = None;
                    self.next_database_attempt = Instant::now() + DATABASE_RETRY;
                }
                self.report_database_error(doing, &db::describe(&error));
                None
            }
        }
    }

    fn report_database_error(&mut self, doing: &str, error: &str) {
        if error != self.last_database_error {
            log!(Error, "database error while {doing}: {error}");
            self.last_database_error = error.to_owned();
        }
    }

    fn handle_snapshot(&mut self, snapshot: Snapshot) {
        let mut events = self.tracker.update(&conditions_in(&snapshot, &self.known_sensor_ids));
        self.known_sensor_ids.extend(snapshot.sensors.iter().map(|sensor| sensor.id.clone()));

        // Drive health only changes when vigild's slow SMART poll runs, which shows up as a new asOf.
        let as_of = snapshot.drive_health.first().map(|health| health.as_of.clone()).unwrap_or_default();
        let mut drives_to_save = Vec::new();
        if !as_of.is_empty() && as_of != self.last_drive_health_as_of {
            self.last_drive_health_as_of = as_of;
            for health in &snapshot.drive_health {
                let update = diff_drive_health(self.drives.get(&health.device_name), health);
                let Some(state) = update.state else { continue };
                self.drives.insert(state.wwn.clone(), state.clone());
                drives_to_save.push((state, update.changed));
                events.extend(update.events);
            }
        }

        let now = Instant::now();
        let persist = self.last_persisted.is_none_or(|at| now - at >= self.config.persist_interval);
        let inventory = self.last_inventory.is_none_or(|at| now - at >= INVENTORY_EVERY);
        if self.database.is_some() {
            if persist {
                self.last_persisted = Some(now);
            }
            if inventory {
                self.last_inventory = Some(now);
            }
        }
        self.with_database("writing history", |client| {
            for (state, changed) in &drives_to_save {
                db::save_drive(client, state, *changed)?;
            }
            if persist {
                db::insert_samples(client, &snapshot)?;
            }
            if inventory {
                db::touch_inventory(client, &snapshot)?;
            }
            Ok(())
        });

        self.broadcast(&LiveMessage::Snapshot { snapshot: Box::new(snapshot.clone()) });
        self.latest = Some(snapshot);
        if !drives_to_save.is_empty() {
            self.broadcast(&LiveMessage::Drives { drives: self.drive_list() });
        }
        for event in events {
            self.raise(event);
        }
    }

    fn handle_daemon_connection(&mut self, connected: bool, reason: Option<String>) {
        if connected == self.daemon_connected {
            // Still down: keep the first reason, which is the one worth reporting.
            return;
        }
        self.daemon_connected = connected;
        self.broadcast(&LiveMessage::Daemon { connected });

        if connected {
            log!(Info, "connected to vigild");
            self.daemon_lost_since = None;
            if self.daemon_lost_raised {
                self.daemon_lost_raised = false;
                self.raise(NewEvent {
                    severity: Severity::Info,
                    kind: "daemon-lost".to_owned(),
                    message: "vigild is reachable again.".to_owned(),
                });
            }
        } else {
            let reason = reason.unwrap_or_else(|| "unknown reason".to_owned());
            log!(Warning, "vigild connection lost: {reason}");
            self.daemon_lost_since = Some((Instant::now(), reason));
        }
    }

    fn handle_ups(&mut self, result: Result<UpsReading, String>) {
        match result {
            Ok(reading) => {
                if self.ups.reading.is_none() {
                    log!(Info, "reading the UPS: {}", reading.status.join(" "));
                }
                self.ups_unreadable_since = None;
                self.ups.error = None;
                self.ups.reading = Some(reading.clone());

                let now = Instant::now();
                if self.database.is_some() && self.last_ups_persisted.is_none_or(|at| now - at >= self.config.persist_interval) {
                    self.last_ups_persisted = Some(now);
                    self.with_database("writing UPS history", |client| db::insert_ups_sample(client, &reading));
                }
            }
            Err(error) => {
                if self.ups.error.as_deref() != Some(&error) {
                    log!(Warning, "cannot read the UPS: {error}");
                }
                if self.ups.reading.is_some() || self.ups_unreadable_since.is_none() {
                    self.ups_unreadable_since = Some(Instant::now());
                }
                self.ups.reading = None;
                self.ups.error = Some(error);
            }
        }
        self.broadcast(&LiveMessage::Ups { ups: Box::new(self.ups.clone()) });
        self.evaluate_ups();
    }

    fn evaluate_ups(&mut self) {
        let unreadable_for = self.ups_unreadable_since.map_or(Duration::ZERO, |since| since.elapsed());
        for event in self.ups_tracker.update(&ups_conditions(&self.ups, unreadable_for)) {
            self.raise(event);
        }
    }

    fn raise(&mut self, event: NewEvent) {
        match event.severity {
            Severity::Info => log!(Info, "{}", event.message),
            _ => log!(Warning, "{:?}: {}", event.severity, event.message),
        }

        let record = self
            .with_database("recording an event", |client| db::insert_event(client, event.severity, &event.kind, &event.message))
            .unwrap_or_else(|| EventRecord {
                // Negative ids never collide with the database's, and are unique enough for a page's list.
                id: -(SystemTime::now().duration_since(SystemTime::UNIX_EPOCH).unwrap_or_default().as_millis() as i64),
                ts: now_rfc3339(),
                severity: event.severity,
                kind: event.kind.clone(),
                message: event.message.clone(),
            });

        self.broadcast(&LiveMessage::Event { event: record });
        if event.severity != Severity::Info
            && let Some(url) = &self.config.ntfy_url
        {
            notify::send(url, &event);
        }
    }

    fn drive_list(&self) -> Vec<DriveState> {
        self.drives.values().cloned().collect()
    }

    fn current_state(&self) -> Vec<LiveMessage> {
        let mut messages = vec![LiveMessage::Daemon { connected: self.daemon_connected }];
        if let Some(snapshot) = &self.latest {
            messages.push(LiveMessage::Snapshot { snapshot: Box::new(snapshot.clone()) });
        }
        messages.push(LiveMessage::Drives { drives: self.drive_list() });
        messages.push(LiveMessage::Ups { ups: Box::new(self.ups.clone()) });
        messages
    }

    fn subscribe(&mut self, subscriber: SyncSender<Arc<str>>) {
        for message in self.current_state() {
            if subscriber.try_send(serialize(&message)).is_err() {
                return;
            }
        }
        self.subscribers.push(subscriber);
    }

    /// Serialized once, shared by every subscriber. One that can't keep up is dropped; its
    /// connection then ends and vigil-web reconnects, starting again from the current state.
    fn broadcast(&mut self, message: &LiveMessage) {
        if self.subscribers.is_empty() {
            return;
        }
        let json = serialize(message);
        self.subscribers.retain(|subscriber| match subscriber.try_send(Arc::clone(&json)) {
            Ok(()) => true,
            Err(TrySendError::Full(_) | TrySendError::Disconnected(_)) => false,
        });
    }
}

fn serialize(message: &LiveMessage) -> Arc<str> {
    serde_json::to_string(message).unwrap_or_else(|_| "{}".to_owned()).into()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::DaemonTarget;
    use std::sync::mpsc;
    use vigil_protocol::{FanStatus, PwmMode};

    fn config() -> Config {
        Config {
            daemon: DaemonTarget::Tcp("127.0.0.1:1".to_owned()),
            // Nothing listens on port 1: the database stays down, which the live view must survive.
            database_url: "postgres://vigil@127.0.0.1:1/vigil".to_owned(),
            persist_interval: Duration::from_secs(10),
            raw_retention: Duration::from_secs(7 * 86_400),
            ntfy_url: None,
            nut: None,
            ups_name: "apc".to_owned(),
            daemon_lost_after: Duration::from_secs(20),
            listen: "127.0.0.1:0".to_owned(),
        }
    }

    fn snapshot(stalled: bool) -> Snapshot {
        let fan = FanStatus { id: "drive-cage".to_owned(), duty_percent: 60, rpm: Some(0), mode: Some(PwmMode::Manual), stalled };
        Snapshot { timestamp_utc: now_rfc3339(), sensors: vec![], fans: vec![fan], drive_health: vec![], control_loop_healthy: true }
    }

    fn messages(receiver: &mpsc::Receiver<Arc<str>>) -> Vec<String> {
        receiver.try_iter().map(|json| json.to_string()).collect()
    }

    #[test]
    fn a_subscriber_gets_the_current_state_then_changes_without_a_database() {
        let mut runtime = Runtime::new(config());
        runtime.handle_daemon_connection(true, None);
        runtime.handle_snapshot(snapshot(false));

        let (sender, receiver) = mpsc::sync_channel(64);
        runtime.subscribe(sender);
        let first = messages(&receiver);
        assert_eq!(first.len(), 4);
        assert!(first[0].contains(r#""type":"daemon","connected":true"#));
        assert!(first[1].starts_with(r#"{"type":"snapshot""#));
        assert!(first[3].contains(r#""enabled":false,"name":"apc""#));

        // Three stalled polls raise one critical event, broadcast even with no database.
        for _ in 0..3 {
            runtime.handle_snapshot(snapshot(true));
        }
        let later = messages(&receiver);
        let events: Vec<_> = later.iter().filter(|m| m.contains(r#""type":"event""#)).collect();
        assert_eq!(events.len(), 1);
        assert!(events[0].contains(r#""severity":"critical""#) && events[0].contains("fan-stalled:drive-cage"));
    }

    #[test]
    fn a_subscriber_that_cannot_keep_up_is_dropped() {
        let mut runtime = Runtime::new(config());
        let (sender, _receiver) = mpsc::sync_channel(3);
        runtime.subscribe(sender);
        assert_eq!(runtime.subscribers.len(), 1);

        runtime.handle_snapshot(snapshot(false));
        assert!(runtime.subscribers.is_empty());
    }

    #[test]
    fn news_already_queued_is_handled_before_an_outage_is_judged() {
        let config = Config { daemon_lost_after: Duration::ZERO, ..config() };
        let (inputs, receiver) = mpsc::channel();
        let (subscriber, stream) = mpsc::sync_channel(64);
        inputs.send(Input::Subscribe(subscriber)).unwrap();
        inputs.send(Input::Daemon(DaemonEvent::Connected)).unwrap();
        drop(inputs);

        Runtime::new(config).run(receiver);

        assert!(!messages(&stream).iter().any(|m| m.contains("daemon-lost")));
    }

    #[test]
    fn a_lasting_daemon_outage_is_raised_once_and_cleared_on_reconnect() {
        let mut runtime = Runtime::new(Config { daemon_lost_after: Duration::ZERO, ..config() });
        let (sender, receiver) = mpsc::sync_channel(64);
        runtime.subscribe(sender);
        messages(&receiver);

        runtime.handle_daemon_connection(false, Some("connection refused".to_owned()));
        runtime.tick();
        runtime.tick();
        let lost: Vec<_> = messages(&receiver).into_iter().filter(|m| m.contains("daemon-lost")).collect();
        assert_eq!(lost.len(), 1);
        assert!(lost[0].contains("vigild is unreachable (not connected yet)"));

        runtime.handle_daemon_connection(true, None);
        assert!(messages(&receiver).iter().any(|m| m.contains("vigild is reachable again.")));
    }
}
