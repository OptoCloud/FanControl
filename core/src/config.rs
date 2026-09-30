//! vigil-core's settings, all from the environment (/etc/vigil-core.env in production).

use std::time::Duration;

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
    /// Where the live stream for vigil-web listens. Loopback: vigil-web runs beside it.
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
            database_url: get("DATABASE_URL").unwrap_or_else(|| "postgres://vigil@localhost/vigil".to_owned()),
            persist_interval: Duration::from_secs_f64(number("PERSIST_INTERVAL_SECONDS", 10.0)?),
            raw_retention: Duration::from_secs_f64(number("RAW_RETENTION_DAYS", 30.0)? * 86_400.0),
            ntfy_url: get("NTFY_URL"),
            nut,
            ups_name,
            daemon_lost_after: Duration::from_secs(20),
            listen: format!("{}:{}", get("CORE_HOST").unwrap_or_else(|| "127.0.0.1".to_owned()), number("CORE_PORT", 3001.0)? as u16),
        })
    }
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
        assert_eq!(config.listen, "127.0.0.1:3001");
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
    fn rejects_nonsense() {
        assert!(config(&[("PERSIST_INTERVAL_SECONDS", "soon")]).is_err());
        assert!(config(&[("VIGILD_URL", "unix:/x")]).is_err());
    }
}
