//! vigil-core: vigil's orchestrator. Reads vigild and the UPS, records history in Postgres,
//! raises alerts, and streams the live state to vigil-web. Configured entirely from the
//! environment (see config.rs); runs until killed, and nothing in it needs a clean shutdown.

// A panic in a test IS the failure report; the lint is aimed at production paths.
#![cfg_attr(test, allow(clippy::unwrap_used, clippy::expect_used))]

mod alerts;
mod config;
mod daemon;
mod db;
mod ntfy;
mod nut;
mod runtime;
mod server;
mod timescale;

/// Overridden by RUST_LOG. Info is the right default: vigil-core logs state changes, not polls.
const DEFAULT_LOG_FILTER: &str = "info";

use config::Config;
use runtime::{Input, Runtime};
use std::net::TcpListener;
use std::sync::Arc;
use std::sync::atomic::AtomicBool;
use std::sync::mpsc;
use tracing::{error, info};

fn main() {
    // First, before anything can want to log: tracing drops every record silently until a
    // subscriber is installed.
    vigil_logging::init(DEFAULT_LOG_FILTER);

    let config = match Config::from_env() {
        Ok(config) => config,
        Err(error) => {
            error!("configuration: {error}");
            std::process::exit(2);
        }
    };

    let listener = match TcpListener::bind(&config.listen) {
        Ok(listener) => listener,
        Err(error) => {
            error!("cannot listen on {}: {error}", config.listen);
            std::process::exit(1);
        }
    };
    info!("vigil-core {}: live stream on http://{}/live", env!("CARGO_PKG_VERSION"), config.listen);

    let (inputs, receiver) = mpsc::channel();

    let (target, sender) = (config.daemon.clone(), inputs.clone());
    info!("vigild: {target:?}");
    std::thread::spawn(move || daemon::run(target, move |event| drop(sender.send(Input::Daemon(event)))));

    if let Some(nut) = config.nut.clone() {
        info!("ups: {}@{}:{}", nut.ups, nut.host, nut.port);
        let sender = inputs.clone();
        std::thread::spawn(move || {
            nut::run(nut, Arc::new(AtomicBool::new(false)), move |result| drop(sender.send(Input::Ups(Box::new(result)))))
        });
    }

    std::thread::spawn(move || server::serve(listener, inputs));
    Runtime::new(config).run(receiver);
}
