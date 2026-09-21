//! Runs an external tool (nvidia-smi, smartctl) and captures its stdout, with a hard
//! timeout. Both tools talk to hardware and can block indefinitely when that hardware
//! misbehaves (a GPU that fell off the bus, a drive stuck in error recovery); without a
//! timeout that hang propagates straight into whichever loop waited on it. On timeout the
//! child is killed rather than left behind.

use std::io::Read;
use std::process::{Child, Command, Stdio};
use std::sync::mpsc;
use std::time::{Duration, Instant};

#[derive(Debug)]
pub struct ProcessOutput {
    /// None if the process was killed by a signal.
    pub exit_code: Option<i32>,
    pub stdout: String,
}

/// None if the tool couldn't be started (missing, not executable) or didn't finish within `timeout`.
pub fn run(program: &str, args: &[&str], timeout: Duration) -> Option<ProcessOutput> {
    let deadline = Instant::now() + timeout;
    let mut child = Command::new(program).args(args).stdin(Stdio::null()).stdout(Stdio::piped()).spawn().ok()?;

    // Reading happens on its own thread because a blocking read has no timeout. If the
    // child hangs, killing it closes the pipe and that ends the thread.
    let mut stdout = child.stdout.take()?;
    let (sender, receiver) = mpsc::channel();
    std::thread::spawn(move || {
        let mut output = String::new();
        let _ = stdout.read_to_string(&mut output);
        let _ = sender.send(output);
    });

    let Ok(stdout) = receiver.recv_timeout(timeout) else {
        kill(&mut child);
        return None;
    };

    // stdout closing normally coincides with exit, but isn't the same thing.
    loop {
        match child.try_wait() {
            Ok(Some(status)) => return Some(ProcessOutput { exit_code: status.code(), stdout }),
            Ok(None) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(5)),
            _ => {
                kill(&mut child);
                return None;
            }
        }
    }
}

fn kill(child: &mut Child) {
    let _ = child.kill();

    // Reap it so it doesn't linger as a zombie, but never block on that: a process stuck
    // in uninterruptible I/O doesn't die until the kernel lets go of it.
    for _ in 0..100 {
        if !matches!(child.try_wait(), Ok(None)) {
            return;
        }
        std::thread::sleep(Duration::from_millis(10));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// cargo is the one executable guaranteed to exist wherever these tests can run at all.
    const CARGO: &str = env!("CARGO");

    #[test]
    fn returns_none_when_the_executable_does_not_exist() {
        assert!(run("definitely-not-a-real-tool-4f1c", &[], Duration::from_secs(5)).is_none());
    }

    #[test]
    fn captures_stdout_and_exit_code() {
        let output = run(CARGO, &["--version"], Duration::from_secs(60)).unwrap();

        assert_eq!(output.exit_code, Some(0));
        assert!(output.stdout.starts_with("cargo"));
    }

    #[test]
    fn reports_a_failing_exit_code() {
        let output = run(CARGO, &["definitely-not-a-subcommand-4f1c"], Duration::from_secs(60)).unwrap();

        assert_ne!(output.exit_code, Some(0));
    }

    #[test]
    fn kills_the_process_and_returns_none_on_timeout() {
        // No process can start, run and exit inside a millisecond, so this always times out.
        let started = Instant::now();

        assert!(run(CARGO, &["--version"], Duration::from_millis(1)).is_none());
        assert!(started.elapsed() < Duration::from_secs(20));
    }
}
