using Npgsql;
using NpgsqlTypes;
using Vigil.Core.Protocol;

namespace Vigil.Core.Data;

/// <summary>
/// A drive's last GOOD health result, plus a history row whenever something moved.
/// </summary>
/// <remarks>
/// vigild reports only what its latest poll saw and never wakes a sleeping drive, so a drive is
/// regularly unavailable with nothing to say. This table is what survives those gaps.
/// </remarks>
public sealed class DriveStore(VigilDataSources databases)
{
    private static readonly string SelectDrives = SqlText.Load("select_drives");
    private static readonly string UpsertDrive = SqlText.Load("upsert_drive");
    private static readonly string InsertHealthHistory = SqlText.Load("insert_drive_health_history");

    public async Task<List<DriveState>> LoadAsync(CancellationToken cancellationToken)
    {
        var drives = new List<DriveState>();

        await using var command = databases.Reader.CreateCommand(SelectDrives);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            drives.Add(new DriveState
            {
                Wwn = reader.GetString(0),
                Port = reader.IsDBNull(1) ? null : reader.GetString(1),
                Passed = reader.IsDBNull(2) ? null : reader.GetBoolean(2),
                ReallocatedSectorCount = Count(reader, 3),
                PendingSectorCount = Count(reader, 4),
                PowerOnHours = Count(reader, 5),
                SourcePath = reader.GetString(6),
                // Epoch milliseconds, formatted here, so no date handling crosses the boundary
                // as text in either direction.
                AsOf = Rfc3339.From(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7))),
            });
        }

        return drives;
    }

    /// <summary>
    /// Stores a drive's new last-good state, and a history row if <paramref name="changed"/>
    /// (anything but power-on hours moved).
    /// </summary>
    public async Task SaveAsync(DriveState drive, bool changed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drive);

        await using var connection = await databases.Writer.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Both statements or neither: a history row without the state it describes, or a state
        // whose change was never recorded, would each be a lie about what the drive did.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var upsert = new NpgsqlCommand(UpsertDrive, connection, transaction))
        {
            AddDriveParameters(upsert, drive, includePortAndPath: true);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (changed)
        {
            await using var history = new NpgsqlCommand(InsertHealthHistory, connection, transaction);
            history.Parameters.Add(Text(drive.AsOf));
            history.Parameters.Add(Text(drive.Wwn));
            history.Parameters.Add(Nullable(drive.Passed, NpgsqlDbType.Boolean));
            history.Parameters.Add(Big(drive.ReallocatedSectorCount));
            history.Parameters.Add(Big(drive.PendingSectorCount));
            history.Parameters.Add(Big(drive.PowerOnHours));
            await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddDriveParameters(NpgsqlCommand command, DriveState drive, bool includePortAndPath)
    {
        command.Parameters.Add(Text(drive.Wwn));
        if (includePortAndPath)
        {
            command.Parameters.Add(Nullable(drive.Port, NpgsqlDbType.Text));
        }

        command.Parameters.Add(Nullable(drive.Passed, NpgsqlDbType.Boolean));
        command.Parameters.Add(Big(drive.ReallocatedSectorCount));
        command.Parameters.Add(Big(drive.PendingSectorCount));
        command.Parameters.Add(Big(drive.PowerOnHours));
        if (includePortAndPath)
        {
            command.Parameters.Add(Text(drive.SourcePath));
            command.Parameters.Add(Text(drive.AsOf));
        }
    }

    /// <summary>SMART reports these unsigned; the columns are bigint.</summary>
    private static ulong? Count(NpgsqlDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : (ulong)reader.GetInt64(column);

    private static NpgsqlParameter Big(ulong? value) =>
        new() { Value = value is null ? DBNull.Value : (long)value.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    private static NpgsqlParameter Text(string value) => new() { Value = value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Nullable<T>(T? value, NpgsqlDbType type) =>
        new() { Value = value is null ? DBNull.Value : value, NpgsqlDbType = type };
}
