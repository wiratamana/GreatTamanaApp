
using GTA.Core;

namespace GTA.Tools.Impl.Outlook;

/// <summary>
/// One shared, logged-in <see cref="OutlookMailboxClient"/> for the whole app (same idea as TeamsSession / BoxSession).
/// Account = PrimaryAccount from init.ini (cred_primaryaccount_id / cred_primaryaccount_password) -- the same
/// account the IBM Cloud / Jira auto-login already uses to read its OTP mails.
/// The token cache is Credentials\outlook_session.json next to the exe (gitignored), shared with those auto-login clients.
/// </summary>
internal static class OutlookSession
{
    /// <summary>Guards the (slow, multi-hop) login, so two tool calls never start two logins at the same time.</summary>
    private static readonly SemaphoreSlim _loginGate = new(1, 1);

    private static OutlookMailboxClient? _client;

    private static Action<string>? _logger =
        msg => Console.Error.WriteLine($"[outlook] {msg}");

    public static void SetLogger(Action<string>? logger) => _logger = logger;

    /// <summary>Runs <paramref name="action"/> on the logged-in client. On HTTP 401 it signs in again (full login) and retries once.</summary>
    public static async Task<T> RunAsync<T>(Func<OutlookMailboxClient, Task<T>> action)
    {
        OutlookMailboxClient client = await GetLoggedInClientAsync(forceFreshLogin: false);
        try
        {
            return await action(client);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("(status 401", StringComparison.Ordinal))
        {
            _logger?.Invoke("Outlook answered 401 (token not accepted). Signing in again and retrying once.");
            client = await GetLoggedInClientAsync(forceFreshLogin: true);
            return await action(client);
        }
    }

    private static async Task<OutlookMailboxClient> GetLoggedInClientAsync(bool forceFreshLogin)
    {
        OutlookMailboxClient? current = _client;
        if (current != null && current.IsLoggedIn && !forceFreshLogin) return current;

        await _loginGate.WaitAsync();
        try
        {
            current = _client;
            if (current != null && current.IsLoggedIn && !forceFreshLogin) return current;

            current?.Dispose();
            _client = null;

            AccountCredential cred = CredentialManager.Load(AccountKind.PrimaryAccount);
            var fresh = new OutlookMailboxClient(cred.Id, cred.Password, logger: _logger);
            if (forceFreshLogin) fresh.ClearCachedSession();
            await fresh.LoginAsync(forceFreshLogin);

            _client = fresh;
            return fresh;
        }
        finally
        {
            _loginGate.Release();
        }
    }
}
