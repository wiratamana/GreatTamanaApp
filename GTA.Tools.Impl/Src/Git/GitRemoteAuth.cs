#if !CO_WORKER_USE
using System;
using System.Text;
using GTA.Core;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        private static string[] BuildTokenAuthArgs(string token)
        {
            string headerValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{token}"));
            return new[] { "-c", $"http.extraheader=AUTHORIZATION: basic {headerValue}" };
        }

        /// <summary>
        /// Builds one-shot "-c" arguments that route a single git invocation through the Windows system proxy.
        /// git cannot do NTLM single sign-on itself, so it talks to the local relay (ProxyBridge) which logs in
        /// with the current Windows account. Returns no arguments when Windows uses no proxy (direct connection).
        /// </summary>
        private static string[] BuildGitProxyArgs()
        {
            string? localProxyUrl = ProxyBridge.LocalProxyUrl;
            if (localProxyUrl == null)
                return Array.Empty<string>();

            return new[]
            {
                "-c", $"http.proxy={localProxyUrl}"
            };
        }

        private static bool RemoteExists(string repositoryPath, string remoteName)
        {
            try
            {
                string listResult = RunGitCommand(repositoryPath, "remote");
                foreach (string rawLine in listResult.Split('\n'))
                {
                    string line = rawLine.Trim().TrimEnd('\r');
                    if (string.Equals(line, remoteName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
#endif
