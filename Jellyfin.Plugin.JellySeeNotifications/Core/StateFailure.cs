namespace Jellyfin.Plugin.JellySeeNotifications.Core;

// Only StateDirectory.Replace marks Windows MoveFileEx access-denied failures:
// they can be transient destination locks. Do not retry arbitrary access denial.
internal sealed class StateReplacementException(Exception inner) : IOException("State replacement unavailable", inner);

internal static class StateFailure
{
    // Unwrap only our own wrappers. A timeout/cancelled worker is terminal even
    // if some unrelated inner exception happens to contain a sharing code.
    internal static Exception Cause(Exception error) => (error is StatePersistenceException or StateReplacementException) && error.InnerException is { } inner ? Cause(inner) : error;
    internal static bool IsSharing(Exception error) => error switch
    {
        StatePersistenceException { InnerException: { } inner } => IsSharing(inner),
        StateReplacementException { InnerException: UnauthorizedAccessException { HResult: unchecked((int)0x80070005) } } => true,
        IOException io => io.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021),
        _ => false
    };
}
