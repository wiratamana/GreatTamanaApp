using System.Text.RegularExpressions;

namespace GTA.App;

/// <summary>Masks common secret patterns in text that is written to log files.</summary>
public static class SecretRedactor
{
    private const string Mask = "***";

    private static readonly (Regex Pattern, string Replacement)[] Rules =
    {
        (new Regex(@"(?i)(bearer\s+)[A-Za-z0-9\-._~+/=]{8,}", RegexOptions.Compiled), "$1" + Mask),
        (new Regex(@"(?i)(x-api-key|api-key|authorization|cookie)(""?\s*[:=]\s*""?)[^\s"",;]{6,}", RegexOptions.Compiled), "$1$2" + Mask),
        (new Regex(@"(?i)([?&](?:key|api_key|apikey|token|access_token)=)[^&\s""']+", RegexOptions.Compiled), "$1" + Mask),
        (new Regex(@"eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]*", RegexOptions.Compiled), Mask),
        (new Regex(@"(?i)(password|passwd|pwd)(""?\s*[:=]\s*""?)[^\s"",;&]+", RegexOptions.Compiled), "$1$2" + Mask),
        (new Regex(@"\b(?:github_pat_|ghp_|gho_|ghs_)[A-Za-z0-9_]{10,}", RegexOptions.Compiled), Mask),
        (new Regex(@"\bAIza[0-9A-Za-z_\-]{20,}", RegexOptions.Compiled), Mask),
    };

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        string result = text;
        foreach (var (pattern, replacement) in Rules)
            result = pattern.Replace(result, replacement);
        return result;
    }
}
