//! vigil-core's settings, all from the environment (/etc/vigil-core.env in production).

use std::time::Duration;
use vigil_protocol::limits;

#[derive(Debug, Clone, PartialEq)]
pub enum DaemonTarget {
    /// vigild's unix socket, bind-mounted into the container.
    Socket(String),
    /// host:port of a TCP stand-in; only for development against dev/mock-vigild.mjs.
    Tcp(String),
}

#[derive(Debug, Clone, PartialEq)]
pub struct NutConfig {
    pub host: String,
    pub port: u16,
    /// The UPS's name on upsd ([apc] in ups.conf).
    pub ups: String,
    pub poll_interval: Duration,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Config {
    pub daemon: DaemonTarget,
    pub database_url: String,
    /// vigild publishes every 2s. That resolution matters live, not in history.
    pub persist_interval: Duration,
    pub raw_retention: Duration,
    pub ntfy_url: Option<String>,
    /// None when NUT_HOST is unset: the UPS is left out entirely.
    pub nut: Option<NutConfig>,
    /// The UPS name its history is stored under, configured or not.
    pub ups_name: String,
    /// How long vigild has to be unreachable before that counts as an incident.
    pub daemon_lost_after: Duration,
    /// Where the live stream for vigil-web listens. Loopback unless CORE_ALLOW_NON_LOOPBACK
    /// says otherwise; see `listen_address`.
    pub listen: String,
}

impl Config {
    pub fn from_env() -> Result<Self, String> {
        Self::from_lookup(|key| std::env::var(key).ok().filter(|value| !value.is_empty()))
    }

    pub fn from_lookup(get: impl Fn(&str) -> Option<String>) -> Result<Self, String> {
        let number = |key: &str, default: f64| -> Result<f64, String> {
            match get(key) {
                Some(value) => value
                    .trim()
                    .parse::<f64>()
                    .ok()
                    .filter(|n| n.is_finite() && *n > 0.0)
                    .ok_or(format!("{key} must be a positive number, got '{value}'")),
                None => Ok(default),
            }
        };

        let daemon = match (get("VIGILD_SOCKET"), get("VIGILD_URL")) {
            (Some(path), _) => DaemonTarget::Socket(path),
            (None, Some(url)) => {
                DaemonTarget::Tcp(host_port(&url).ok_or(format!("VIGILD_URL must look like http://host:port, got '{url}'"))?)
            }
            (None, None) => DaemonTarget::Tcp("127.0.0.1:5178".to_owned()),
        };

        let ups_name = get("NUT_UPS").unwrap_or_else(|| "apc".to_owned());
        let nut = match get("NUT_HOST") {
            Some(host) => Some(NutConfig {
                host,
                port: number("NUT_PORT", 3493.0)? as u16,
                ups: ups_name.clone(),
                poll_interval: Duration::from_secs_f64(number("NUT_POLL_SECONDS", 5.0)?),
            }),
            None => None,
        };

        Ok(Self {
            daemon,
            database_url: get("DATABASE_URL").unwrap_or_else(|| limits::DATABASE_URL_DEFAULT.to_owned()),
            persist_interval: Duration::from_secs_f64(number("PERSIST_INTERVAL_SECONDS", 10.0)?),
            raw_retention: Duration::from_secs_f64(number("RAW_RETENTION_DAYS", 30.0)? * 86_400.0),
            ntfy_url: get("NTFY_URL"),
            nut,
            ups_name,
            daemon_lost_after: Duration::from_secs(20),
            listen: listen_address(
                get("CORE_HOST"),
                number("CORE_PORT", f64::from(limits::CORE_DEFAULT_PORT))? as u16,
                get("CORE_ALLOW_NON_LOOPBACK"),
            )?,
        })
    }
}

/// The address the live stream binds, refusing anything but loopback unless the operator has
/// explicitly opted out.
///
/// vigil-core is read-only today, but actions are meant to arrive here (see server.rs), and a
/// single typo in CORE_HOST would be the difference between "vigil-web beside it can reach
/// this" and "the whole network can". A config value that decides a bind address is validated
/// before it reaches the syscall: docs/SECURITY.md §1.1.
fn listen_address(host: Option<String>, port: u16, allow_non_loopback: Option<String>) -> Result<String, String> {
    let host = host.unwrap_or_else(|| "127.0.0.1".to_owned());
    if !is_loopback(&host) && !is_true(allow_non_loopback.as_deref()) {
        return Err(format!(
            "CORE_HOST is '{host}', which is not loopback. vigil-core must not be reachable from \
             the network: run vigil-web beside it and leave CORE_HOST unset (127.0.0.1). If you \
             really mean to expose it, set CORE_ALLOW_NON_LOOPBACK=1 and read docs/SECURITY.md §1 first."
        ));
    }
    Ok(format!("{host}:{port}"))
}

/// True only for an address that cannot be routed to from another host. An empty host and
/// "0.0.0.0"/"[::]" mean *every* interface, so they are deliberately not loopback.
fn is_loopback(host: &str) -> bool {
    let host = host.trim().trim_start_matches('[').trim_end_matches(']');
    if host == "localhost" {
        return true;
    }
    match host.parse::<std::net::IpAddr>() {
        Ok(address) => address.is_loopback(),
        // Not an address we can reason about (a hostname, or nonsense): assume the worst.
        Err(_) => false,
    }
}

fn is_true(value: Option<&str>) -> bool {
    matches!(value.map(str::trim), Some("1" | "true" | "yes"))
}

/// "http://127.0.0.1:5178/" to "127.0.0.1:5178".
fn host_port(url: &str) -> Option<String> {
    let rest = url.strip_prefix("http://")?;
    let authority = rest.split('/').next()?;
    authority.contains(':').then(|| authority.to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    fn config(pairs: &[(&str, &str)]) -> Result<Config, String> {
        let env: HashMap<String, String> = pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect();
        Config::from_lookup(|key| env.get(key).cloned())
    }

    #[test]
    fn defaults_to_the_mock_daemon_and_no_ups() {
        let config = config(&[]).unwrap();

        assert_eq!(config.daemon, DaemonTarget::Tcp("127.0.0.1:5178".to_owned()));
        assert_eq!(config.nut, None);
        assert_eq!(config.persist_interval, Duration::from_secs(10));
        assert_eq!(config.listen, format!("127.0.0.1:{}", limits::CORE_DEFAULT_PORT));
    }

    #[test]
    fn the_socket_wins_over_a_url_and_nut_host_enables_the_ups() {
        let config =
            config(&[("VIGILD_SOCKET", "/mnt/vigil/vigild.sock"), ("VIGILD_URL", "http://x:1"), ("NUT_HOST", "10.0.0.4")]).unwrap();

        assert_eq!(config.daemon, DaemonTarget::Socket("/mnt/vigil/vigild.sock".to_owned()));
        let nut = config.nut.unwrap();
        assert_eq!((nut.host.as_str(), nut.port, nut.ups.as_str()), ("10.0.0.4", 3493, "apc"));
    }

    #[test]
    fn binds_loopback_by_default_and_refuses_a_routable_address() {
        assert_eq!(listen_address(None, 3001, None).unwrap(), "127.0.0.1:3001");
        for loopback in ["127.0.0.1", "127.0.0.53", "::1", "[::1]", "localhost", " 127.0.0.1 "] {
            assert!(listen_address(Some(loopback.to_owned()), 1, None).is_ok(), "{loopback}");
        }

        // Every one of these would put vigil-core on the network.
        for routable in ["0.0.0.0", "::", "[::]", "", "10.0.0.130", "192.168.1.5", "vigil.lan"] {
            let error = listen_address(Some(routable.to_owned()), 1, None).unwrap_err();
            assert!(error.contains("CORE_ALLOW_NON_LOOPBACK"), "{routable}: {error}");
        }
    }

    #[test]
    fn a_routable_address_needs_an_explicit_opt_in() {
        assert_eq!(listen_address(Some("0.0.0.0".to_owned()), 3001, Some("1".to_owned())).unwrap(), "0.0.0.0:3001");
        assert_eq!(listen_address(Some("0.0.0.0".to_owned()), 3001, Some("true".to_owned())).unwrap(), "0.0.0.0:3001");

        // Anything else is not consent.
        for not_consent in ["0", "false", "", "maybe"] {
            assert!(listen_address(Some("0.0.0.0".to_owned()), 1, Some(not_consent.to_owned())).is_err(), "{not_consent}");
        }
    }

    #[test]
    fn rejects_nonsense() {
        assert!(config(&[("PERSIST_INTERVAL_SECONDS", "soon")]).is_err());
        assert!(config(&[("VIGILD_URL", "unix:/x")]).is_err());
        assert!(config(&[("CORE_HOST", "0.0.0.0")]).is_err());
    }
}
