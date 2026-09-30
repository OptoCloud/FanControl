//! The one connection vigil-core keeps to NUT's upsd: a plain TCP line protocol (RFC 9271).
//! Variable reads are anonymous on upsd, so no login is needed. It polls `LIST VAR <ups>` on
//! a fixed interval, the way upsmon does, on its own thread, reconnecting forever.

use crate::config::NutConfig;
use std::collections::BTreeMap;
use std::io::{self, BufRead, BufReader, Write};
use std::net::{TcpStream, ToSocketAddrs};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;
use vigil_protocol::{UpsReading, now_rfc3339};

const REQUEST_TIMEOUT: Duration = Duration::from_secs(10);
const MIN_RETRY: Duration = Duration::from_secs(1);
const MAX_RETRY: Duration = Duration::from_secs(30);

/// Polls until `stop` is set, handing every reading, or the reason there is none, to `on_result`.
/// An error arrives both when upsd can't be reached and when it answers ERR for the UPS
/// (DATA-STALE, DRIVER-NOT-CONNECTED: upsd is fine, but it has lost the UPS itself).
pub fn run(config: NutConfig, stop: Arc<AtomicBool>, on_result: impl Fn(Result<UpsReading, String>)) {
    let mut retry = MIN_RETRY;
    while !stop.load(Ordering::Relaxed) {
        let mut answered = false;
        let error = poll_until_broken(&config, &stop, &mut |result| {
            answered = true;
            on_result(result);
        });
        if stop.load(Ordering::Relaxed) {
            return;
        }

        on_result(Err(error.to_string()));
        if answered {
            retry = MIN_RETRY;
        }
        std::thread::sleep(retry);
        retry = (retry * 2).min(MAX_RETRY);
    }
}

fn poll_until_broken(config: &NutConfig, stop: &AtomicBool, on_result: &mut impl FnMut(Result<UpsReading, String>)) -> io::Error {
    let connect = || -> io::Result<TcpStream> {
        let address =
            (config.host.as_str(), config.port).to_socket_addrs()?.next().ok_or_else(|| io::Error::other("cannot resolve upsd"))?;
        let stream = TcpStream::connect_timeout(&address, REQUEST_TIMEOUT)?;
        stream.set_read_timeout(Some(REQUEST_TIMEOUT))?;
        Ok(stream)
    };
    let mut stream = match connect() {
        Ok(stream) => stream,
        Err(error) => return error,
    };
    let mut reader = match stream.try_clone() {
        Ok(clone) => BufReader::new(clone),
        Err(error) => return error,
    };

    loop {
        if stop.load(Ordering::Relaxed) {
            let _ = stream.write_all(b"LOGOUT\n");
            return io::Error::other("stopped");
        }

        match list_var(&mut stream, &mut reader, &config.ups) {
            Ok(Ok(variables)) => on_result(Ok(to_reading(&config.ups, &variables, now_rfc3339()))),
            Ok(Err(upsd_error)) => on_result(Err(format!("upsd: {upsd_error}"))),
            Err(error) if error.kind() == io::ErrorKind::WouldBlock || error.kind() == io::ErrorKind::TimedOut => {
                return io::Error::other(format!("no answer from upsd within {}s", REQUEST_TIMEOUT.as_secs()));
            }
            Err(error) => return error,
        }
        std::thread::sleep(config.poll_interval);
    }
}

/// One LIST VAR exchange: the variables, or upsd's own error word.
fn list_var(stream: &mut TcpStream, reader: &mut impl BufRead, ups: &str) -> io::Result<Result<BTreeMap<String, String>, String>> {
    stream.write_all(format!("LIST VAR {ups}\n").as_bytes())?;

    let mut lines = Vec::new();
    loop {
        let mut line = String::new();
        if reader.read_line(&mut line)? == 0 {
            return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "upsd closed the connection"));
        }
        let line = line.trim_end_matches(['\r', '\n']).to_owned();

        if let Some(error) = line.strip_prefix("ERR ") {
            return Ok(Err(error.to_owned()));
        }
        let done = line.starts_with("END LIST VAR");
        lines.push(line);
        if done {
            return Ok(Ok(parse_variables(&lines)));
        }
    }
}

/// The variables in a LIST VAR answer: lines of `VAR <ups> <name> "<value>"`, where the value
/// escapes `"` and `\` with a backslash. BEGIN/END and anything unexpected are skipped.
pub fn parse_variables(lines: &[String]) -> BTreeMap<String, String> {
    let mut variables = BTreeMap::new();
    for line in lines {
        let Some(rest) = line.strip_prefix("VAR ") else { continue };
        let mut parts = rest.splitn(3, ' ');
        let (Some(_ups), Some(name), Some(quoted)) = (parts.next(), parts.next(), parts.next()) else { continue };
        let Some(inner) = quoted.strip_prefix('"').and_then(|q| q.strip_suffix('"')) else { continue };

        let mut value = String::with_capacity(inner.len());
        let mut chars = inner.chars();
        while let Some(c) = chars.next() {
            value.push(if c == '\\' { chars.next().unwrap_or('\\') } else { c });
        }
        variables.insert(name.to_owned(), value);
    }
    variables
}

pub fn to_reading(name: &str, variables: &BTreeMap<String, String>, at: String) -> UpsReading {
    let number = |key: &str| variables.get(key).and_then(|value| value.trim().parse::<f64>().ok()).filter(|n| n.is_finite());

    let load = number("ups.load");
    let real_power = number("ups.realpower").or_else(|| Some((load? / 100.0 * number("ups.realpower.nominal")?).round()));

    let manufacturer = variables.get("device.mfr").or_else(|| variables.get("ups.mfr"));
    let model = variables.get("device.model").or_else(|| variables.get("ups.model")).map(|model| match manufacturer {
        Some(manufacturer) if !model.starts_with(manufacturer.as_str()) => format!("{manufacturer} {model}").trim().to_owned(),
        _ => model.trim().to_owned(),
    });

    UpsReading {
        timestamp_utc: at,
        name: name.to_owned(),
        model,
        status: variables.get("ups.status").map(|s| s.split_whitespace().map(str::to_owned).collect()).unwrap_or_default(),
        battery_charge: number("battery.charge"),
        battery_runtime_seconds: number("battery.runtime"),
        load,
        real_power,
        input_voltage: number("input.voltage"),
        output_voltage: number("output.voltage"),
        battery_voltage: number("battery.voltage"),
        variables: variables.clone(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Read;
    use std::net::TcpListener;
    use std::sync::mpsc;

    fn lines(text: &[&str]) -> Vec<String> {
        text.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn reads_var_lines_and_skips_the_framing() {
        let variables = parse_variables(&lines(&[
            "BEGIN LIST VAR apc",
            r#"VAR apc battery.charge "100""#,
            r#"VAR apc ups.status "OL CHRG""#,
            "END LIST VAR apc",
        ]));
        assert_eq!(variables.len(), 2);
        assert_eq!(variables["ups.status"], "OL CHRG");
    }

    #[test]
    fn unescapes_quotes_and_backslashes_and_keeps_empty_values() {
        let variables = parse_variables(&lines(&[r#"VAR apc device.model "Smart \"UPS\" C:\\1000""#, r#"VAR apc ups.test.result """#]));
        assert_eq!(variables["device.model"], r#"Smart "UPS" C:\1000"#);
        assert_eq!(variables["ups.test.result"], "");
    }

    #[test]
    fn picks_out_the_metrics_and_splits_the_status_flags() {
        let variables: BTreeMap<String, String> = [
            ("battery.charge", "87"),
            ("battery.runtime", "2310"),
            ("ups.load", "25"),
            ("ups.realpower.nominal", "670"),
            ("input.voltage", "231.4"),
            ("ups.status", "OB DISCHRG"),
            ("device.mfr", "American Power Conversion"),
            ("device.model", "Smart-UPS 1000"),
        ]
        .iter()
        .map(|(k, v)| (k.to_string(), v.to_string()))
        .collect();

        let reading = to_reading("apc", &variables, "t".to_owned());

        assert_eq!(reading.model.as_deref(), Some("American Power Conversion Smart-UPS 1000"));
        assert_eq!(reading.status, ["OB", "DISCHRG"]);
        assert_eq!((reading.battery_charge, reading.load, reading.real_power), (Some(87.0), Some(25.0), Some(168.0)));
        assert_eq!(reading.input_voltage, Some(231.4));
        assert_eq!(reading.output_voltage, None);
    }

    #[test]
    fn prefers_reported_real_power_and_leaves_the_unknown_empty() {
        let variables: BTreeMap<String, String> =
            [("ups.realpower", "150"), ("ups.load", "25"), ("ups.realpower.nominal", "670"), ("input.voltage", "n/a")]
                .iter()
                .map(|(k, v)| (k.to_string(), v.to_string()))
                .collect();
        let reading = to_reading("apc", &variables, "t".to_owned());
        assert_eq!((reading.real_power, reading.input_voltage, reading.model), (Some(150.0), None, None));
        assert!(reading.status.is_empty());
    }

    /// A one-UPS upsd answering every LIST VAR with `answer`, split in two writes.
    fn fake_upsd(answer: &'static str) -> u16 {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        std::thread::spawn(move || {
            for stream in listener.incoming() {
                let Ok(mut stream) = stream else { return };
                std::thread::spawn(move || {
                    let mut request = [0u8; 256];
                    while let Ok(read) = stream.read(&mut request) {
                        if read == 0 {
                            return;
                        }
                        let (first, second) = answer.split_at(answer.len() / 2);
                        let _ = stream.write_all(first.as_bytes());
                        std::thread::sleep(Duration::from_millis(5));
                        let _ = stream.write_all(second.as_bytes());
                    }
                });
            }
        });
        port
    }

    fn poll(port: u16, count: usize) -> Vec<Result<UpsReading, String>> {
        let config = NutConfig { host: "127.0.0.1".to_owned(), port, ups: "apc".to_owned(), poll_interval: Duration::from_millis(20) };
        let stop = Arc::new(AtomicBool::new(false));
        let (sender, receiver) = mpsc::channel();
        let stopper = Arc::clone(&stop);
        std::thread::spawn(move || run(config, stopper, move |result| drop(sender.send(result))));
        let results = receiver.iter().take(count).collect();
        stop.store(true, Ordering::Relaxed);
        results
    }

    #[test]
    fn polls_and_reports_readings_even_when_an_answer_arrives_in_pieces() {
        let port = fake_upsd("BEGIN LIST VAR apc\r\nVAR apc battery.charge \"100\"\nVAR apc ups.status \"OL\"\nEND LIST VAR apc\n");
        let results = poll(port, 2);
        let reading = results[1].as_ref().unwrap();
        assert_eq!((reading.battery_charge, reading.status.clone()), (Some(100.0), vec!["OL".to_owned()]));
    }

    #[test]
    fn reports_upsd_errors_and_keeps_polling() {
        let port = fake_upsd("ERR DATA-STALE\n");
        assert_eq!(poll(port, 2), vec![Err("upsd: DATA-STALE".to_owned()), Err("upsd: DATA-STALE".to_owned())]);
    }

    #[test]
    fn reports_an_unreachable_upsd() {
        let port = TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port();
        assert!(poll(port, 1)[0].is_err());
    }
}
