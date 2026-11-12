
using GTA.Core;

namespace GTA.Tools.Impl.Teams;

/// <summary>
/// One shared, logged-in <see cref="TeamsSessionClient"/> for the whole app (same idea as BoxSession).
/// Account = PrimaryAccount from init.ini (cred_primaryaccount_id / cred_primaryaccount_password).
/// Token caches (next to the exe, folder Credentials is gitignored):
///   teams_token_cache.json : the chat token + service map (about 1 hour). A hit = no login, no bootstrap.
///   teams_session.json     : the AAD session with the refresh_token (up to 24 hours). A hit = no password login.
/// The password (ADFS) login is the LAST resort, so the server sees as few real logins as possible.
/// </summary>
internal static class TeamsSession
{
    /// <summary>Guards the (slow, multi-hop) login, so two tool calls never start two logins at the same time.</summary>
    private static readonly SemaphoreSlim _loginGate = new(1, 1);

    private static TeamsSessionClient? _client;

    private static Action<string>? _logger =
        msg => Console.Error.WriteLine($"[teams] {msg}");

    public static void SetLogger(Action<string>? logger) => _logger = logger;

    private enum LoginLevel
    {
        /// <summary>Normal: reuse the client in memory, else the token cache, else the AAD session, else a password login.</summary>
        Normal,

        /// <summary>The server rejected the token: skip the token cache, renew with the refresh_token (no password).</summary>
        Renew,

        /// <summary>Renewing did not help: delete the caches and do a real password login.</summary>
        FullLogin,
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the logged-in client. On HTTP 401 it renews the token WITHOUT the password
    /// (refresh_token) and retries once. Only if that is rejected too, it does a full password login and retries once more.
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<TeamsSessionClient, Task<T>> action)
    {
        TeamsSessionClient client = await GetLoggedInClientAsync(LoginLevel.Normal);
        try
        {
            return await action(client);
        }
        catch (InvalidOperationException ex) when (IsUnauthorized(ex))
        {
            _logger?.Invoke("Teams answered 401 (token not accepted). Renewing the token with the refresh_token (no password) and retrying once.");
        }

        client = await GetLoggedInClientAsync(LoginLevel.Renew);
        try
        {
            return await action(client);
        }
        catch (InvalidOperationException ex) when (IsUnauthorized(ex))
        {
            _logger?.Invoke("Teams answered 401 again. Doing a full password login and retrying one last time.");
        }

        client = await GetLoggedInClientAsync(LoginLevel.FullLogin);
        return await action(client);
    }

    /// <summary>
    /// True when the token was not accepted: HTTP 401, or Microsoft says the refresh_token is dead
    /// (AADSTS700084 = single-page-app refresh token is older than 24 hours, 70043 / 70008 = expired or revoked grant).
    /// Both are solved by renewing the sign-in. Other 400 errors (for example "scope not consented") are NOT retried.
    /// </summary>
    private static bool IsUnauthorized(InvalidOperationException ex)
        => ex.Message.Contains("(status 401", StringComparison.Ordinal)
           || ex.Message.Contains("AADSTS700084", StringComparison.Ordinal)
           || ex.Message.Contains("AADSTS70043", StringComparison.Ordinal)
           || ex.Message.Contains("AADSTS70008", StringComparison.Ordinal);

    private static async Task<TeamsSessionClient> GetLoggedInClientAsync(LoginLevel level)
    {
        TeamsSessionClient? current = _client;
        if (current != null && current.IsLoggedIn && level == LoginLevel.Normal) return current;

        await _loginGate.WaitAsync();
        try
        {
            current = _client;
            if (current != null && current.IsLoggedIn && level == LoginLevel.Normal) return current;

            current?.Dispose();
            _client = null;

            AccountCredential cred = CredentialManager.Load(AccountKind.PrimaryAccount);
            var fresh = new TeamsSessionClient(cred.Id, cred.Password, logger: _logger);
            if (level == LoginLevel.FullLogin) fresh.ClearCachedSession();
            await fresh.LoginAsync(forceFreshLogin: level == LoginLevel.FullLogin, skipTokenCache: level == LoginLevel.Renew);

            _client = fresh;
            return fresh;
        }
        finally
        {
            _loginGate.Release();
        }
    }
}
