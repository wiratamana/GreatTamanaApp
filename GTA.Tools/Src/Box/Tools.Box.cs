using GTA.Tools.Impl.Box;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        internal static string BoxHost      => BoxApiClient.BoxHost;
        internal static string InternalBase => BoxApiClient.InternalBase;

        internal readonly struct BoxResult
        {
            public bool    Ok       { get; init; }
            public int     Status   { get; init; }
            public string? Body     { get; init; }
            public string? Error    { get; init; }

            public static BoxResult Success(int status, string? body)
                => new() { Ok = true, Status = status, Body = body };

            public static BoxResult Failure(string error, int status = 0, string? body = null)
                => new() { Ok = false, Status = status, Error = error, Body = body };
        }

        private static BoxResult FromImpl(BoxApiClient.BoxApiResult r) =>
            r.Ok ? BoxResult.Success(r.Status, r.Body) : BoxResult.Failure(r.Error ?? "Unknown error", r.Status, r.Body);

        internal static BoxResult BoxGet(
            string  url,
            string? bearer    = null,
            int     timeoutMs = 30_000)
            => FromImpl(BoxApiClient.Get(url, bearer, timeoutMs));

        internal static BoxResult BoxPost(
            string  url,
            string? body      = null,
            string? bearer    = null,
            int     timeoutMs = 30_000)
            => FromImpl(BoxApiClient.Post(url, body, bearer, timeoutMs));

        internal static void EnsureBoxTabActive() => BoxApiClient.EnsureBoxTabActive();

        internal static (string? Base64, string? Error) BoxFetchBase64(
            string  fileId,
            int     timeoutMs,
            string? sharedLinkUrl = null)
            => BoxApiClient.FetchBase64(fileId, timeoutMs, sharedLinkUrl);

        internal static bool IsNetErr(string message) => BoxApiClient.IsNetErr(message);

        internal static string FormatResponse(string label, BoxResult result)
        {
            if (!result.Ok)
                return FormatError(label, result);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"✅ {label}");
            sb.AppendLine($"HTTP Status: {result.Status}");
            if (!string.IsNullOrEmpty(result.Body))
            {
                sb.AppendLine("--- Response Body ---");
                sb.AppendLine(result.Body);
            }
            return sb.ToString().TrimEnd();
        }

        internal static string FormatError(string label, BoxResult result)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"❌ {label}");
            sb.AppendLine($"Error: {result.Error}");
            if (result.Status > 0) sb.AppendLine($"HTTP Status: {result.Status}");
            if (!string.IsNullOrEmpty(result.Body))
            {
                sb.AppendLine("--- Response Body ---");
                sb.AppendLine(result.Body);
            }
            return sb.ToString().TrimEnd();
        }
    }
}
