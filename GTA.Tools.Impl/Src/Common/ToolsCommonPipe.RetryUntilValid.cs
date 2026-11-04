using System;
using System.Threading;

namespace GTA.Tools.Impl.Common
{
    public static partial class ToolsCommonPipe
    {
        public static (bool Success, T Result, string LastError) RetryUntilValid<T>(
            Func<T> attempt,
            Func<T, bool> isValid,
            Func<T, string> describeInvalid,
            int maxRetries = MaxDownloadRetries)
        {
            T      result    = default!;
            string lastError = "Unknown error.";

            for (int attemptNum = 1; attemptNum <= maxRetries; attemptNum++)
            {
                try
                {
                    result = attempt();

                    if (isValid(result))
                        return (true, result, string.Empty);

                    lastError = describeInvalid(result);
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }

                if (attemptNum < maxRetries)
                    Thread.Sleep(RetryBaseDelayMs * attemptNum);
            }

            return (false, result, lastError);
        }
    }
}
