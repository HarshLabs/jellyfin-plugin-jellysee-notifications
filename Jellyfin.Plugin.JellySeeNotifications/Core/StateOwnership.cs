namespace Jellyfin.Plugin.JellySeeNotifications.Core;

// Kept by the supervisor even when a Journal constructor never returns an instance.
internal sealed class StateOwnership
{
    private StateIoWorker? worker;
    internal void Attach(StateIoWorker value) => worker = value;
    internal bool CanRestart => worker is null || worker.CanRestart;
}
