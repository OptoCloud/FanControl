//! Thin abstraction over sysfs file access so hwmon/PWM logic can be unit-tested against
//! an in-memory fake instead of a real Linux /sys tree.
//!
//! Paths are plain strings joined with '/', never `std::path`: sysfs is always
//! POSIX-style, and this daemon is built and tested on Windows while only running on Linux.

use std::io;

pub trait SysFs: Send + Sync {
    /// File contents, trimmed. None if the file is missing or unreadable, which is common
    /// for racing sysfs attributes (e.g. the temperature of a drive that just spun down).
    fn read(&self, path: &str) -> Option<String>;

    fn write(&self, path: &str, contents: &str) -> io::Result<()>;

    fn file_exists(&self, path: &str) -> bool;

    /// Full paths of the directories (or symlinks to directories) directly under `path`,
    /// sorted. Empty if `path` doesn't exist.
    fn list_dirs(&self, path: &str) -> Vec<String>;
}

pub fn join(segments: &[&str]) -> String {
    segments.join("/")
}

pub fn file_name(path: &str) -> &str {
    path.rsplit('/').next().unwrap_or(path)
}

/// Real sysfs implementation: every member is a thin std::fs wrapper.
pub struct LinuxSysFs;

impl SysFs for LinuxSysFs {
    fn read(&self, path: &str) -> Option<String> {
        std::fs::read_to_string(path).ok().map(|s| s.trim().to_owned())
    }

    fn write(&self, path: &str, contents: &str) -> io::Result<()> {
        std::fs::write(path, contents)
    }

    fn file_exists(&self, path: &str) -> bool {
        std::path::Path::new(path).is_file()
    }

    fn list_dirs(&self, path: &str) -> Vec<String> {
        let Ok(entries) = std::fs::read_dir(path) else {
            return Vec::new();
        };

        // is_dir() follows symlinks, which matters: /sys/class/hwmon/hwmonN are all links.
        let mut dirs: Vec<String> = entries
            .flatten()
            .filter(|entry| entry.path().is_dir())
            .filter_map(|entry| entry.file_name().into_string().ok())
            .map(|name| format!("{path}/{name}"))
            .collect();
        dirs.sort();
        dirs
    }
}

#[cfg(test)]
pub mod fake {
    use super::SysFs;
    use std::collections::{BTreeSet, HashMap, HashSet};
    use std::io;
    use std::sync::Mutex;

    /// In-memory sysfs double, since a real /sys/class/hwmon only exists on Linux.
    #[derive(Default)]
    pub struct FakeSysFs {
        files: Mutex<HashMap<String, String>>,
        dirs: Mutex<BTreeSet<String>>,
        failing_writes: Mutex<HashSet<String>>,
    }

    impl FakeSysFs {
        pub fn new() -> Self {
            Self::default()
        }

        pub fn with_file(self, path: &str, contents: &str) -> Self {
            self.set(path, contents);
            self
        }

        pub fn with_dir(self, path: &str) -> Self {
            self.add_dir(path);
            self
        }

        pub fn set(&self, path: &str, contents: &str) {
            self.files.lock().unwrap().insert(path.to_owned(), contents.to_owned());
            if let Some((parent, _)) = path.rsplit_once('/') {
                self.add_dir(parent);
            }
        }

        pub fn remove(&self, path: &str) {
            self.files.lock().unwrap().remove(path);
        }

        /// Makes every later write to `path` fail, the way a real sysfs write can (EIO, EINVAL).
        pub fn fail_writes_to(&self, path: &str) {
            self.failing_writes.lock().unwrap().insert(path.to_owned());
        }

        pub fn get(&self, path: &str) -> String {
            self.files.lock().unwrap().get(path).cloned().unwrap_or_else(|| panic!("no fake file at {path}"))
        }

        fn add_dir(&self, path: &str) {
            let mut dirs = self.dirs.lock().unwrap();
            let mut current = path;
            while !current.is_empty() {
                dirs.insert(current.to_owned());
                current = current.rsplit_once('/').map_or("", |(parent, _)| parent);
            }
        }
    }

    impl SysFs for FakeSysFs {
        fn read(&self, path: &str) -> Option<String> {
            self.files.lock().unwrap().get(path).cloned()
        }

        fn write(&self, path: &str, contents: &str) -> io::Result<()> {
            if self.failing_writes.lock().unwrap().contains(path) {
                return Err(io::Error::other("simulated sysfs write failure"));
            }

            self.files.lock().unwrap().insert(path.to_owned(), contents.to_owned());
            Ok(())
        }

        fn file_exists(&self, path: &str) -> bool {
            self.files.lock().unwrap().contains_key(path)
        }

        fn list_dirs(&self, path: &str) -> Vec<String> {
            let prefix = format!("{}/", path.trim_end_matches('/'));
            self.dirs
                .lock()
                .unwrap()
                .iter()
                .filter(|dir| dir.strip_prefix(&prefix).is_some_and(|rest| !rest.contains('/')))
                .cloned()
                .collect()
        }
    }
}
