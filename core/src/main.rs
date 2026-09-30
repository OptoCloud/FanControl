//! vigil-core: vigil's orchestrator. Reads vigild and the UPS, records history in Postgres,
//! raises alerts, and streams the live state to vigil-web. Configured entirely from the
//! environment (see config.rs); runs until killed, and nothing in it needs a clean shutdown.

mod alerts;
mod config;
mod daemon;
mod db;
mod log;
mod notify;
mod nut;
mod runtime;
mod server;
mod sse;

use config::Config;
use runtime::{Input, Runtime};
use std::net::TcpListener;
use std::sync::Arc;
use std::sync::atomic::AtomicBool;
use std::sync::mpsc;

fn main() {
    let config = match Config::from_env() {
        Ok(config) => config,
        Err(error) => {
            log!(Error, "configuration: {error}");
            std::process::exit(2);
        }
    };

    let listener = match TcpListener::bind(&config.listen) {
        Ok(listener) => listener,
        Err(error) => {
            log!(Error, "cannot listen on {}: {error}", config.listen);
            std::process::exit(1);
        }
    };
    log!(Info, "vigil-core {}: live stream on http://{}/live", env!("CARGO_PKG_VERSION"), config.listen);

    let (inputs, receiver) = mpsc::channel();

    let (target, sender) = (config.daemon.clone(), inputs.clone());
    log!(Info, "vigild: {target:?}");
    std::thread::spawn(move || daemon::run(target, move |event| drop(sender.send(Input::Daemon(event)))));

    if let Some(nut) = config.nut.clone() {
        log!(Info, "ups: {}@{}:{}", nut.ups, nut.host, nut.port);
        let sender = inputs.clone();
        std::thread::spawn(move || {
            nut::run(nut, Arc::new(AtomicBool::new(false)), move |result| drop(sender.send(Input::Ups(Box::new(result)))))
        });
    }

    std::thread::spawn(move || server::serve(listener, inputs));
    Runtime::new(config).run(receiver);
}
