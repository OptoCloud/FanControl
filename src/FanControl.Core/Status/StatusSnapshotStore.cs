namespace FanControl.Core.Status;

/// <summary>Thread-safe holder for the latest snapshot: written by the control loop, read by the status API.</summary>
public sealed class StatusSnapshotStore
{
    private volatile StatusSnapshot? _latest;

    public StatusSnapshot? Latest => _latest;

    public void Update(StatusSnapshot snapshot) => _latest = snapshot;
}
