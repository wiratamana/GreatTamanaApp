namespace GTA.Tools.Impl.Box;

/// <summary>
/// Runs one Box write action (upload / create folder / delete) on the shared logged-in client.
///   * Runs on a thread-pool thread, so it can never deadlock on a UI synchronization context.
///   * If the session looks expired (401 / 403 / not logged in / no csrf), logs in again and tries ONCE more.
///     A genuine "no permission" 403 also triggers this, so to stop a storm of logins (one per file in a folder
///     upload) the re-login is only done when the last one is older than <see cref="ReloginCooldown"/>.
///   * Network errors are NEVER retried here: the action may already have worked, and a blind retry could
///     create a duplicate or fail with 409. The error says so.
/// </summary>
internal static class BoxClientRunner
{
    private static readonly TimeSpan ReloginCooldown = TimeSpan.FromSeconds(60);
    private static DateTime _lastReloginUtc = DateTime.MinValue;
    private static readonly object _gate = new();

    public static T Run<T>(Func<BoxAdfsLoginClient, Task<T>> action) => Task.Run(() =>
    {
        try
        {
            return action(BoxSession.GetLoggedInClient()).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsAuthError(ex.Message) && TryClaimRelogin())
        {
            return action(BoxSession.GetLoggedInClient(forceRelogin: true)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (BoxApiClient.IsNetErr(ex.Message))
        {
            throw new InvalidOperationException(
                "Network error: " + ex.Message +
                " -- the action MAY already have happened in Box. Check Box first, then try again.", ex);
        }
    }).GetAwaiter().GetResult();

    private static bool TryClaimRelogin()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _lastReloginUtc < ReloginCooldown) return false;
            _lastReloginUtc = DateTime.UtcNow;
            return true;
        }
    }

    private static bool IsAuthError(string message) =>
        message.Contains("HTTP 401") ||
        message.Contains("HTTP 403") ||
        message.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("csrf-token", StringComparison.OrdinalIgnoreCase);
}
