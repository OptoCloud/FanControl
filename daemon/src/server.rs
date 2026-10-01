//! The read-only status API: a tiny axum service on a unix socket.
//!
//!   GET /status   the latest snapshot as JSON (503 before the first poll)
//!   GET /events   the same snapshots as a Server-Sent Events stream, one per poll
//!
//! axum and hyper do the HTTP (ADR-015); what is vigild's here is the two routes, the socket's
//! permissions, and the fact that it is a unix socket at all.
//!
//! There is no TCP listener on purpose (ADR-002). This daemon runs as root with raw PWM and
//! ioctl access; a unix socket cannot be reached from the network at all, and a containerised
//! consumer gets the socket's directory bind-mounted in instead.
//!
//!   curl --unix-socket /run/vigil/vigild.sock http://localhost/status

use crate::status::StatusHub;
use axum::Router;
use axum::extract::State;
use axum::http::{StatusCode, header};
use axum::response::sse::{Event, KeepAlive, Sse};
use axum::response::{IntoResponse, Response};
use axum::routing::get;
use std::convert::Infallible;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::broadcast::error::RecvError;
use tokio_stream::Stream;

/// How long an event stream stays silent before a keepalive comment is sent, which is also
/// what notices that a consumer has gone away.
const KEEPALIVE_INTERVAL: Duration = Duration::from_secs(15);

/// How often the server checks whether the process is shutting down. Short enough that a
/// SIGTERM is not visibly delayed, long enough to cost nothing.
const SHUTDOWN_POLL_INTERVAL: Duration = Duration::from_millis(100);

#[derive(Clone)]
struct Api {
    hub: Arc<StatusHub>,
    /// A snapshot older than this means the control loop has hung.
    stale_after: Duration,
}

pub fn router(hub: Arc<StatusHub>, stale_after: Duration) -> Router {
    Router::new()
        .route("/status", get(status))
        .route("/events", get(events))
        .fallback(|| async { (StatusCode::NOT_FOUND, "try /status or /events\n") })
        .with_state(Api { hub, stale_after })
}

async fn status(State(api): State<Api>) -> Response {
    match api.hub.latest(api.stale_after) {
        // Already a serialized string, so it is handed over as-is rather than re-encoded.
        Some(json) => ([(header::CONTENT_TYPE, "application/json")], json.to_string()).into_response(),
        None => (StatusCode::SERVICE_UNAVAILABLE, "no poll has completed yet\n").into_response(),
    }
}

async fn events(State(api): State<Api>) -> Sse<impl Stream<Item = Result<Event, Infallible>>> {
    let stream = async_stream::stream! {
        // Subscribed before the current snapshot is read, so nothing published in between is
        // missed: a repeat is harmless here, a gap is not.
        let mut updates = api.hub.subscribe();

        // A new subscriber gets the current state immediately rather than waiting out a poll.
        if let Some(json) = api.hub.latest(api.stale_after) {
            yield Ok(Event::default().event("status").data(&*json));
        }

        loop {
            match updates.recv().await {
                Ok(json) => yield Ok(Event::default().event("status").data(&*json)),
                // The consumer fell far enough behind to miss snapshots. This is a
                // latest-value API, so the missed ones are superseded, not lost: carry on
                // with the next one rather than dropping the connection.
                Err(RecvError::Lagged(_)) => continue,
                Err(RecvError::Closed) => break,
            }
        }
    };

    Sse::new(stream).keep_alive(KeepAlive::new().interval(KEEPALIVE_INTERVAL).text("keepalive"))
}

#[cfg(unix)]
pub use listener::spawn;

#[cfg(unix)]
mod listener {
    use super::{SHUTDOWN_POLL_INTERVAL, router};
    use crate::config::ApiConfig;
    use crate::status::StatusHub;
    use axum::error_handling::HandleErrorLayer;
    use axum::http::StatusCode;
    use std::io;
    use std::os::unix::fs::PermissionsExt;
    use std::path::Path;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::time::Duration;
    use tower::limit::ConcurrencyLimitLayer;
    use tower::load_shed::LoadShedLayer;
    use tower::load_shed::error::Overloaded;
    use tower::{BoxError, ServiceBuilder};
    use tracing::info;

    /// The mode the socket falls back to if the configured one will not parse: owner and group
    /// only, never world-readable. A root daemon's API is not for everyone.
    const FALLBACK_SOCKET_MODE: u32 = 0o660;

    /// The API runs on its own small runtime so it can never compete with the control loop for
    /// threads. Two workers is plenty for a handful of mostly-idle event streams.
    const API_WORKER_THREADS: usize = 2;

    /// Binds the socket and serves it in the background for the life of the process.
    pub fn spawn(config: &ApiConfig, hub: Arc<StatusHub>, stale_after: Duration, shutdown: &'static AtomicBool) -> io::Result<()> {
        let path = Path::new(&config.socket_path);
        if let Some(directory) = path.parent() {
            std::fs::create_dir_all(directory)?;
        }

        // A socket file left behind by a previous instance would make bind fail.
        match std::fs::remove_file(path) {
            Err(error) if error.kind() != io::ErrorKind::NotFound => return Err(error),
            _ => {}
        }

        let listener = std::os::unix::net::UnixListener::bind(path)?;
        std::fs::set_permissions(path, std::fs::Permissions::from_mode(config.socket_mode_bits().unwrap_or(FALLBACK_SOCKET_MODE)))?;
        if config.socket_uid.is_some() || config.socket_gid.is_some() {
            std::os::unix::fs::chown(path, config.socket_uid, config.socket_gid)?;
        }
        // tokio requires a non-blocking socket to take it over.
        listener.set_nonblocking(true)?;

        info!("Status API listening on unix socket {} (mode {}).", config.socket_path, config.socket_mode);

        // Past max_clients a new consumer is shed with 503 rather than queued behind the
        // others, which is what the hand-rolled accept loop did with its own atomic counter.
        // An SSE stream holds its request open for as long as it lasts, so a concurrency
        // limit on requests is a limit on simultaneous consumers.
        let app = router(hub, stale_after).layer(
            ServiceBuilder::new()
                .layer(HandleErrorLayer::new(|error: BoxError| async move {
                    if error.is::<Overloaded>() {
                        (StatusCode::SERVICE_UNAVAILABLE, "too many clients\n")
                    } else {
                        (StatusCode::INTERNAL_SERVER_ERROR, "the status API failed\n")
                    }
                }))
                .layer(LoadShedLayer::new())
                .layer(ConcurrencyLimitLayer::new(config.max_clients)),
        );

        let runtime = tokio::runtime::Builder::new_multi_thread().worker_threads(API_WORKER_THREADS).enable_all().build()?;

        std::thread::Builder::new().name("api".to_owned()).spawn(move || {
            runtime.block_on(async move {
                let listener = match tokio::net::UnixListener::from_std(listener) {
                    Ok(listener) => listener,
                    Err(error) => {
                        tracing::error!("the status API could not start: {error}");
                        return;
                    }
                };

                let served = axum::serve(listener, app)
                    .with_graceful_shutdown(async move {
                        while !shutdown.load(Ordering::Relaxed) {
                            tokio::time::sleep(SHUTDOWN_POLL_INTERVAL).await;
                        }
                    })
                    .await;

                if let Err(error) = served {
                    tracing::error!("the status API stopped: {error}");
                }
            });
        })?;

        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::status::Snapshot;
    use axum::body::Body;
    use axum::http::Request;
    use futures_util::StreamExt;
    use http_body_util::BodyExt;
    use tower::ServiceExt;

    const FRESH: Duration = Duration::from_secs(3600);

    fn snapshot(timestamp: &str) -> Snapshot {
        Snapshot { timestamp_utc: timestamp.to_owned(), sensors: vec![], fans: vec![], drive_health: vec![], control_loop_healthy: true }
    }

    fn hub_with_snapshot() -> Arc<StatusHub> {
        let hub = Arc::new(StatusHub::new());
        hub.publish(snapshot("t"));
        hub
    }

    /// One request against the real router, as hyper would deliver it.
    async fn request(method: &str, path: &str, hub: Arc<StatusHub>) -> (StatusCode, String, Option<String>) {
        let response = router(hub, FRESH).oneshot(Request::builder().method(method).uri(path).body(Body::empty()).unwrap()).await.unwrap();

        let status = response.status();
        let content_type = response.headers().get(header::CONTENT_TYPE).map(|value| value.to_str().unwrap().to_owned());
        let body = String::from_utf8(response.into_body().collect().await.unwrap().to_bytes().to_vec()).unwrap();
        (status, body, content_type)
    }

    #[tokio::test]
    async fn status_returns_the_latest_snapshot_as_json() {
        let (status, body, content_type) = request("GET", "/status", hub_with_snapshot()).await;

        assert_eq!(status, StatusCode::OK);
        assert_eq!(content_type.as_deref(), Some("application/json"));
        assert!(body.contains(r#""timestampUtc":"t""#));
        assert!(body.ends_with(r#""controlLoopHealthy":true}"#));
    }

    #[tokio::test]
    async fn status_ignores_a_query_string() {
        assert_eq!(request("GET", "/status?pretty=1", hub_with_snapshot()).await.0, StatusCode::OK);
    }

    #[tokio::test]
    async fn status_is_503_before_the_first_poll() {
        let (status, body, _) = request("GET", "/status", Arc::new(StatusHub::new())).await;

        assert_eq!(status, StatusCode::SERVICE_UNAVAILABLE);
        assert!(body.contains("no poll has completed yet"));
    }

    #[tokio::test]
    async fn unknown_paths_are_404_and_writes_are_405() {
        let (status, body, _) = request("GET", "/nope", hub_with_snapshot()).await;
        assert_eq!(status, StatusCode::NOT_FOUND);
        assert!(body.contains("try /status or /events"));

        // The API is read-only: axum answers a method the route does not declare.
        assert_eq!(request("POST", "/status", hub_with_snapshot()).await.0, StatusCode::METHOD_NOT_ALLOWED);
    }

    #[tokio::test]
    async fn events_sends_the_current_snapshot_immediately_then_every_new_one() {
        let hub = hub_with_snapshot();
        let response =
            router(Arc::clone(&hub), FRESH).oneshot(Request::builder().uri("/events").body(Body::empty()).unwrap()).await.unwrap();

        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(response.headers().get(header::CONTENT_TYPE).unwrap(), "text/event-stream");

        let mut body = response.into_body().into_data_stream();
        let first = String::from_utf8(body.next().await.unwrap().unwrap().to_vec()).unwrap();
        // A new subscriber does not wait out a poll interval.
        assert!(first.starts_with("event: status\ndata: {"), "{first}");
        assert!(first.contains(r#""timestampUtc":"t""#));

        hub.publish(snapshot("t2"));
        let second = String::from_utf8(body.next().await.unwrap().unwrap().to_vec()).unwrap();
        assert!(second.contains(r#""timestampUtc":"t2""#), "{second}");
    }
}
