using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using GTA.Core; // CredentialManager, AccountCredential, AccountKind
using GTA.Tools.Impl.Common; // IbmCloudAutoLoginClient

namespace GTA.Tools.Impl.IbmCloud
{
    public static partial class IbmCloudLogsApiClient
    {
        private static readonly object AutoLoginLock = new();

        private static readonly Dictionary<string, IbmCloudAutoLoginClient> _autoLoginClients =
            new(StringComparer.OrdinalIgnoreCase);

        private static Action<string>? _autoLoginLogger =
            msg => Console.Error.WriteLine($"[ibm_cloud_auto_login] {msg}");

        public static void SetAutoLoginLogger(Action<string>? logger) => _autoLoginLogger = logger;

        private static IbmCloudAutoLoginClient GetOrCreateAutoLoginClient(IbmCloudEnvironmentInfo env)
        {
            lock (AutoLoginLock)
            {
                if (_autoLoginClients.TryGetValue(env.Key, out var existing))
                    return existing;

                AccountCredential ibmCloudCred = CredentialManager.Load(AccountKind.MailAccount);
                AccountCredential outlookCred  = CredentialManager.Load(AccountKind.PrimaryAccount);

                var client = new IbmCloudAutoLoginClient(
                    ibmEmail: ibmCloudCred.Id,
                    ibmPassword: ibmCloudCred.Password,
                    outlookEmail: outlookCred.Id,
                    outlookPassword: outlookCred.Password,
                    coralogixTokenFilePath: Path.Combine(
                        CredentialManager.CredentialsFolder, $"coralogix_token_{env.Key}.json"),
                    logger: _autoLoginLogger)
                {
                    CoralogixRegion            = env.Region,
                    CoralogixServiceInstanceId = env.TenantId,
                    CoralogixTeamId            = int.TryParse(env.TeamId, out int teamId) ? teamId : 0,
                };

                _autoLoginClients[env.Key] = client;
                return client;
            }
        }

        public const int AutoLoginMintMaxAttempts   = 3;
        public const int AutoLoginMintRetryDelayMs  = 2_000;

        public static string GetAutomatedCoralogixToken(IbmCloudEnvironmentInfo env, bool forceRefresh)
        {
            IbmCloudAutoLoginClient client = GetOrCreateAutoLoginClient(env);

            for (int attempt = 1; attempt <= AutoLoginMintMaxAttempts; attempt++)
            {
                try
                {
                    CoralogixToken token = client.GetCoralogixTokenAsync(forceRefresh)
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                    return token.Token;
                }
                catch (Exception ex) when (attempt < AutoLoginMintMaxAttempts && IsTransientNetworkException(ex))
                {
                    int delayMs = AutoLoginMintRetryDelayMs * attempt;
                    _autoLoginLogger?.Invoke(
                        $"[ibm_cloud_auto_login] transient network error while minting/validating the " +
                        $"Coralogix token for environment \"{env.Key}\" (attempt {attempt}/{AutoLoginMintMaxAttempts}): " +
                        $"{FullExceptionMessage(ex)} — retrying in {delayMs}ms...");
                    Thread.Sleep(delayMs);
                }
            }

            throw new InvalidOperationException(
                "GetAutomatedCoralogixToken: exhausted all attempts without returning or throwing — unreachable.");
        }
    }
}
