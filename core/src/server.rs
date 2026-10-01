//! vigil-core's HTTP side, for vigil-web on the same machine:
//!
//!   GET /live     Server-Sent Events: the current state on connect, then every change
//!                 (snapshots, vigild reachability, drive health, the UPS, new events).
//!   GET /health   200 once running.
//!
//! It listens on loopback only, and vigil-core refuses to start on any other address unless
//! CORE_ALLOW_NON_LOOPBACK says so: actions will arrive here too, which is why nothing but
//! vigil-web beside it may reach it (docs/SECURITY.md §1.1).
//!
//! axum and hyper do the HTTP (ADR-015); what is vigil-core's here is the two routes and how a
//! subscriber is attached to the runtime that owns all the state.

use crate::runtime::Input;
use axum::Router;
use axum::error_handling::HandleErrorLayer;
use axum::extract::State;
use axum::http::StatusCode;
use axum::response::sse::{Event, KeepAlive, Sse};
use axum::response::{IntoResponse, Response};
use axum::routing::get;
use std::convert::Infallible;
use std::net::TcpListener;
use std::sync::mpsc::Sender;
use std::time::Duration;
use tokio::sync::mpsc;
use tower::limit::ConcurrencyLimitLayer;
use tower::load_shed::LoadShedLayer;
use tower::load_shed::error::Overloaded;
use tower::{BoxError, ServiceBuilder};
use tracing::error;

/// Declared once in vigil-protocol, because vigil-web's silence timeout is derived from it.
const KEEPALIVE_INTERVAL: Duration = Duration::from_secs(vigil_protocol::limits::STREAM_KEEPALIVE_SECONDS);

/// Only vigil-web connects, but a page left reloading can stack streams up briefly.
const MAX_CLIENTS: usize = 16;

/// Messages a subscriber may fall behind by before it is dropped. Unlike vigild's stream this
/// one carries events, which are not superseded by the next message, so the backlog is deep.
const SUBSCRIBER_BACKLOG: usize = 256;

/// Two workers is plenty for one browser relay's worth of mostly-idle streams.
const API_WORKER_THREADS: usize = 2;

#[derive(Clone)]
struct Api {
    inputs: Sender<Input>,
}

/// Takes the listener bound in main (so a port already in use is reported before anything
/// else starts) and serves it until the process ends.
pub fn serve(listener: TcpListener, inputs: Sender<Input>) {
    let app = router(inputs);

    if let Err(error) = listener.set_nonblocking(true) {
        error!("the live stream could not start: {error}");
        return;
    }

    let runtime = match tokio::runtime::Builder::new_multi_thread().worker_threads(API_WORKER_THREADS).enable_all().build() {
        Ok(runtime) => runtime,
        Err(error) => {
            error!("the live stream could not start: {error}");
            return;
        }
    };

    runtime.block_on(async move {
        let listener = match tokio::net::TcpListener::from_std(listener) {
            Ok(listener) => listener,
            Err(error) => return error!("the live stream could not start: {error}"),
        };
        if let Err(error) = axum::serve(listener, app).await {
            error!("the live stream stopped: {error}");
        }
    });
}

fn router(inputs: Sender<Input>) -> Router {
    Router::new()
        .route("/health", get(|| async { "ok\n" }))
        .route("/live", get(live))
        .fallback(|| async { (StatusCode::NOT_FOUND, "try /live or /health\n") })
        .with_state(Api { inputs })
        .layer(
            ServiceBuilder::new()
                .layer(HandleErrorLayer::new(|error: BoxError| async move {
                    if error.is::<Overloaded>() {
                        (StatusCode::SERVICE_UNAVAILABLE, "too many clients\n")
                    } else {
                        (StatusCode::INTERNAL_SERVER_ERROR, "the live stream failed\n")
                    }
                }))
                .layer(LoadShedLayer::new())
                .layer(ConcurrencyLimitLayer::new(MAX_CLIENTS)),
        )
}

/// Attaches a subscriber to the runtime and relays whatever it sends. The runtime pushes the
/// current state down the new channel before anything else, so a browser is never briefly blank.
async fn live(State(api): State<Api>) -> Response {
    let (sender, mut receiver) = mpsc::channel(SUBSCRIBER_BACKLOG);
    if api.inputs.send(Input::Subscribe(sender)).is_err() {
        return (StatusCode::SERVICE_UNAVAILABLE, "shutting down\n").into_response();
    }

    let stream = async_stream::stream! {
        while let Some(json) = receiver.recv().await {
            // Unnamed: the payload's own `type` field says which LiveMessage this is.
            yield Ok::<_, Infallible>(Event::default().data(&*json));
        }
    };

    Sse::new(stream).keep_alive(KeepAlive::new().interval(KEEPALIVE_INTERVAL).text("keepalive")).into_response()
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::body::Body;
    use axum::http::Request;
    use futures_util::StreamExt;
    use http_body_util::BodyExt;
    use std::sync::mpsc as runtime_channel;
    use tower::ServiceExt;

    /// One request against the real router, with a stand-in for the runtime on the other end.
    async fn request(path: &str) -> (StatusCode, String, runtime_channel::Receiver<Input>) {
        let (inputs, received) = runtime_channel::channel();
        let response = router(inputs).oneshot(Request::builder().uri(path).body(Body::empty()).unwrap()).await.unwrap();

        let status = response.status();
        let body = String::from_utf8(response.into_body().collect().await.unwrap().to_bytes().to_vec()).unwrap();
        (status, body, received)
    }

    #[tokio::test]
    async fn health_answers_and_unknown_paths_are_404() {
        let (status, body, _) = request("/health").await;
        assert_eq!(status, StatusCode::OK);
        assert_eq!(body, "ok\n");

        let (status, body, _) = request("/nope").await;
        assert_eq!(status, StatusCode::NOT_FOUND);
        assert!(body.contains("try /live or /health"));
    }

    #[tokio::test]
    async fn live_relays_what_the_runtime_sends_as_server_sent_events() {
        let (inputs, received) = runtime_channel::channel();
        let response = router(inputs).oneshot(Request::builder().uri("/live").body(Body::empty()).unwrap()).await.unwrap();

        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(response.headers().get(axum::http::header::CONTENT_TYPE).unwrap(), "text/event-stream");

        let Ok(Input::Subscribe(subscriber)) = received.recv() else { panic!("the runtime was never asked for a subscription") };
        subscriber.send(r#"{"type":"daemon","connected":true}"#.into()).await.unwrap();

        let mut body = response.into_body().into_data_stream();
        let first = String::from_utf8(body.next().await.unwrap().unwrap().to_vec()).unwrap();

        // Unnamed event: the payload's own `type` is what says which message this is.
        assert_eq!(first, "data: {\"type\":\"daemon\",\"connected\":true}\n\n");
    }

    #[tokio::test]
    async fn live_refuses_once_the_runtime_has_shut_down() {
        let (inputs, received) = runtime_channel::channel();
        drop(received);

        let response = router(inputs).oneshot(Request::builder().uri("/live").body(Body::empty()).unwrap()).await.unwrap();

        assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
    }
}
