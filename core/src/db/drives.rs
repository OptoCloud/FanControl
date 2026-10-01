//! A drive's last GOOD health result, plus a history row whenever something moved.
//!
//! vigild reports only what its latest poll saw and never wakes a sleeping drive, so a drive
//! is regularly unavailable with nothing to say. This table is what survives those gaps.

use super::Result;
use postgres::Client;
use vigil_protocol::{DriveState, rfc3339_from_millis};

const SELECT_DRIVES: &str = include_str!("../../sql/select_drives.sql");
const UPSERT_DRIVE: &str = include_str!("../../sql/upsert_drive.sql");
const INSERT_HEALTH_HISTORY: &str = include_str!("../../sql/insert_drive_health_history.sql");

pub fn load_drives(client: &mut Client) -> Result<Vec<DriveState>> {
    let rows = client.query(SELECT_DRIVES, &[])?;
    Ok(rows
        .iter()
        .map(|row| DriveState {
            wwn: row.get(0),
            port: row.get(1),
            passed: row.get(2),
            reallocated_sector_count: row.get::<_, Option<i64>>(3).map(|n| n as u64),
            pending_sector_count: row.get::<_, Option<i64>>(4).map(|n| n as u64),
            power_on_hours: row.get::<_, Option<i64>>(5).map(|n| n as u64),
            source_path: row.get(6),
            as_of: rfc3339_from_millis(row.get(7)),
        })
        .collect())
}

/// Stores a drive's new last-good state, and a history row if `changed` (anything but power-on
/// hours moved).
pub fn save_drive(client: &mut Client, drive: &DriveState, changed: bool) -> Result<()> {
    // The columns are bigint; the protocol counts are u64 because SMART reports them unsigned.
    let big = |value: Option<u64>| value.map(|n| n as i64);
    client.execute(
        UPSERT_DRIVE,
        &[
            &drive.wwn,
            &drive.port,
            &drive.passed,
            &big(drive.reallocated_sector_count),
            &big(drive.pending_sector_count),
            &big(drive.power_on_hours),
            &drive.source_path,
            &drive.as_of,
        ],
    )?;

    if changed {
        client.execute(
            INSERT_HEALTH_HISTORY,
            &[
                &drive.as_of,
                &drive.wwn,
                &drive.passed,
                &big(drive.reallocated_sector_count),
                &big(drive.pending_sector_count),
                &big(drive.power_on_hours),
            ],
        )?;
    }
    Ok(())
}
