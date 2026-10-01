//! Runs an external tool (nvidia-smi, smartctl) and captures its stdout, with a hard
//! timeout. Both tools talk to hardware and can block indefinitely when that hardware
//! misbehaves (a GPU that fell off the bus, a drive stuck in error recovery); without a
//! timeout that hang propagates straight into whichever loop waited on it. On timeout the
//! child is killed rather than left behind.

use std::io::Read;
use std::process::{Child, Command, Stdio};
use std::sync::mpsc;
use std::time::{Duration, Instant};

/// How much stdout a tool may produce before its output is treated as unusable.
///
/// smartctl's JSON for one drive is a few kilobytes and nvidia-smi prints one line per GPU, so
/// this is three orders of magnitude of headroom. The cap is not about those: it is about a
/// tool that never stops writing (a driver stuck in a retry loop printing a warning per
/// attempt). Without it, `read_to_string` grows until the daemon is OOM-killed, which on this
/// host means every fan header is left on manual PWM. docs/SECURITY.md §4.1.
const MAX_OUTPUT_BYTES: u64 = 1024 * 1024;

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
    let stdout = child.stdout.take()?;
    let (sender, receiver) = mpsc::channel();
    std::thread::spawn(move || {
        let _ = sender.send(collect_bounded(stdout, MAX_OUTPUT_BYTES));
    });

    // None from the thread means the tool produced more than the cap; None from the channel
    // means it produced nothing in time. Either way the output is unusable and the child is
    // killed rather than left writing into a pipe nobody reads.
    let Ok(Some(stdout)) = receiver.recv_timeout(timeout) else {
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

/// Reads at most `max` bytes, or None if there were more than that. Lossy rather than strict
/// about UTF-8: a tool that emits one stray byte should cost its own field, not the whole poll.
fn collect_bounded(reader: impl Read, max: u64) -> Option<String> {
    let mut output = Vec::new();
    // One byte past the cap, so going over is detectable rather than silently truncated: a
    // truncated JSON document would parse as garbage and be reported as a real reading.
    let _ = reader.take(max + 1).read_to_end(&mut output);
    (output.len() as u64 <= max).then(|| String::from_utf8_lossy(&output).into_owned())
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
    fn collects_output_up_to_the_cap_and_rejects_anything_past_it() {
        use std::io::Cursor;

        assert_eq!(collect_bounded(Cursor::new(b"37\n".to_vec()), 1024).as_deref(), Some("37\n"));
        // Exactly at the cap is fine; one byte more is not.
        assert_eq!(collect_bounded(Cursor::new(vec![b'x'; 8]), 8).as_deref(), Some("xxxxxxxx"));
        assert_eq!(collect_bounded(Cursor::new(vec![b'x'; 9]), 8), None);
        assert_eq!(collect_bounded(Cursor::new(Vec::new()), 8).as_deref(), Some(""));
    }

    #[test]
    fn a_stray_non_utf8_byte_costs_that_byte_and_nothing_else() {
        use std::io::Cursor;

        // smartctl has been seen to put a device's raw model string straight into its JSON.
        let output = collect_bounded(Cursor::new(b"{\"model\":\"\xff\"}".to_vec()), 1024).unwrap();

        assert!(output.starts_with("{\"model\""), "{output}");
        assert!(output.contains('\u{fffd}'));
    }

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
