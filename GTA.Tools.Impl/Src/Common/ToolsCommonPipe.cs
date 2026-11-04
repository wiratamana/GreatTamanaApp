using System;
using System.IO;
using System.Text.Json;

namespace GTA.Tools.Impl.Common
{
    public static partial class ToolsCommonPipe
    {
        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented               = false,
        };

        public const int MaxDownloadRetries = 10;
        public const int RetryBaseDelayMs   = 1_500;

        public static readonly string[] NetErrorKeywords =
        {
            "強制的に切断",
            "forcibly closed",
            "接続はリモート",
            "An existing connection",
            "connection reset",
            "connection was reset",
            "ReceiveFailure",
            "SendFailure",
            "ConnectFailure",
            "Connection refused",
            "remote party closed",
            "The remote end closed",
            "SocketException",
            "timed out",
        };

        public static bool IsNetErr(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            foreach (string kw in NetErrorKeywords)
                if (message.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
