namespace GTA.Tools.Impl.IbmCloud
{
    public static partial class IbmCloudLogsApiClient
    {
        /// <summary>Raw GET to any IBM Cloud Logs API URL, fully-formatted result.</summary>
        public static string RawGet(string url, IbmCloudEnvironmentInfo env, int timeoutMs)
            => FormatResponse($"GET {url} [{env.Key}]", Get(url, env, timeoutMs));

        /// <summary>Raw POST (caller-supplied JSON body) to any IBM Cloud Logs API URL, fully-formatted result.</summary>
        public static string RawPost(string url, string body, IbmCloudEnvironmentInfo env, int timeoutMs)
            => FormatResponse($"POST {url} [{env.Key}]", Post(url, body, env, timeoutMs));
    }
}
