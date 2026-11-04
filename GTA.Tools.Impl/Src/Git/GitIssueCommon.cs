#if !CO_WORKER_USE
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using GTA.Core;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {

        private static HttpClient CreateProxiedHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                Proxy          = SystemProxy.CreateWebProxy(), // Windows system proxy + current Windows login (SSO)
                UseProxy       = true,
                DefaultProxyCredentials = CredentialCache.DefaultCredentials,
                ConnectTimeout = TimeSpan.FromSeconds(AppConfig.CONNECT_TIMEOUT_SECONDS),
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        private static HttpClient CreateGithubHttpClient(string githubToken)
        {
            var http = CreateProxiedHttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GTA-GitTool/1.0");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", githubToken);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            return http;
        }

        private static string? ResolveIssueOwnerRepo(
            string repositoryPath,
            string? repoOwner,
            string? repoName,
            string remoteName,
            out string owner,
            out string repo)
        {
            owner = repoOwner ?? string.Empty;
            repo = repoName ?? string.Empty;

            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            {
                if (!TryResolveOwnerRepoFromRemote(repositoryPath, remoteName, out string detectedOwner, out string detectedRepo, out string detectError))
                {
                    return $"Error: could not determine the GitHub owner/repository to target ({detectError}). Either fix the local remote, or supply 'repoOwner'/'repoName' explicitly. Nothing was changed and no online operation was attempted.";
                }

                if (string.IsNullOrWhiteSpace(owner)) owner = detectedOwner;
                if (string.IsNullOrWhiteSpace(repo)) repo = detectedRepo;
            }

            return null;
        }
    }
}
#endif
