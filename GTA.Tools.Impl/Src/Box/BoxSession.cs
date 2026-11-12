using GTA.Core;

namespace GTA.Tools.Impl.Box
{
    /// <summary>
    /// One shared, logged-in <see cref="BoxAdfsLoginClient"/> for the whole app.
    /// Login levels, cheapest first:
    ///   0. the client already in memory (same program run)
    ///   1. the saved Box session cookies in Credentials\box_session.json (gitignored), proven by one probe call: no password login
    ///   2. the full ADFS username+password login (the only step the server counts as a real login)
    /// A 401/403 first asks the server whether the session is really dead (probe). A genuine "no permission" 403
    /// therefore no longer causes a password login.
    /// </summary>
    internal static class BoxSession
    {
        /// <summary>Guards creation/replacement of the single shared client instance.</summary>
        private static readonly object _createLock = new();

        /// <summary>Guards the (slow, multi-hop) LoginAsync() call itself, so concurrent tool calls never race into two simultaneous ADFS logins.</summary>
        private static readonly SemaphoreSlim _loginGate = new(1, 1);

        private static BoxAdfsLoginClient? _client;

        private static Action<string>? _logger =
            msg => Console.Error.WriteLine($"[box_auto_login] {msg}");

        public static void SetLogger(Action<string>? logger) => _logger = logger;

        /// <summary>Where the Box session cookies are cached between program runs.</summary>
        private static string SessionFilePath => Path.Combine(CredentialManager.CredentialsFolder, "box_session.json");

        private static BoxAdfsLoginClient CreateClient() =>
            new(BoxApiClient.BoxHost, BoxApiClient.AccountHost, logger: _logger, sessionFilePath: SessionFilePath);

        /// <summary>Returns the shared client instance, creating it (but NOT logging in) on first call.</summary>
        public static BoxAdfsLoginClient GetClient()
        {
            lock (_createLock)
                return _client ??= CreateClient();
        }

        public static BoxAdfsLoginClient GetLoggedInClient(bool forceRelogin = false)
        {
            var client = GetClient();
            if (client.IsLoggedIn && !forceRelogin) return client;

            _loginGate.Wait();
            try
            {
                client = GetClient();
                if (client.IsLoggedIn && !forceRelogin) return client;

                if (forceRelogin && client.IsLoggedIn)
                {
                    // 401/403 can also be a genuine "no permission" for ONE item. Ask the server before throwing a good session away.
                    bool stillValid = client.ProbeSessionAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                    if (stillValid)
                    {
                        _logger?.Invoke("Box answered 401/403, but the session is still valid (probe OK). Keeping it, no new login.");
                        return client;
                    }
                }

                if (forceRelogin)
                {
                    lock (_createLock)
                    {
                        _client?.Dispose();
                        _client = CreateClient();
                        client = _client;
                    }
                    client.ClearSessionCache();
                }

                AccountCredential cred = CredentialManager.Load(AccountKind.PrimaryAccount);
                client.LoginAsync(cred.Id, cred.Password, forceFresh: forceRelogin).ConfigureAwait(false).GetAwaiter().GetResult();
                return client;
            }
            finally
            {
                _loginGate.Release();
            }
        }
    }
}
