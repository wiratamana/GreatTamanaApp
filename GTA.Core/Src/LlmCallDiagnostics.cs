namespace GTA.Core
{
    public static class LlmCallDiagnostics
    {
        public static bool LooksLikeContextLengthError(string? errorText)
        {
            if (string.IsNullOrEmpty(errorText)) return false;
            string t = errorText.ToLowerInvariant();

            return t.Contains("prompt is too long")
                || t.Contains("context_length_exceeded")
                || t.Contains("maximum context length")
                || t.Contains("context length exceeded")
                || t.Contains("input is too long")
                || t.Contains("too many tokens")
                || t.Contains("exceeds the maximum number of tokens")
                || t.Contains("request payload size exceeds the limit")
                || t.Contains("exceeds the maximum context length")
                || (t.Contains("token") && t.Contains("maximum") &&
                    (t.Contains(">") || t.Contains("exceed")));
        }
    }
}
