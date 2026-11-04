using GTA.Core;

namespace GTA.Tools.Impl.Box
{
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

        private static BoxAdfsLoginClient CreateClient() =>
            new(BoxApiClient.BoxHost, BoxApiClient.AccountHost, logger: _logger);

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

                if (forceRelogin)
                {
                    lock (_createLock)
                    {
                        _client?.Dispose();
                        _client = CreateClient();
                        client = _client;
                    }
                }

                AccountCredential cred = CredentialManager.Load(AccountKind.PrimaryAccount);
                client.LoginAsync(cred.Id, cred.Password).ConfigureAwait(false).GetAwaiter().GetResult();
                return client;
            }
            finally
            {
                _loginGate.Release();
            }
        }
    }
}
