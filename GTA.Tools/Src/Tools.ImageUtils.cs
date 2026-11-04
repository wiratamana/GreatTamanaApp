namespace GTA.Tools;

public static class ImageUtils
{
    /// <summary>Tool names whose image content must NEVER be collapsed into a path-only "[[SAVED_IMAGE ...]]" marker — see class-level comment.</summary>
    public static readonly System.Collections.Generic.HashSet<string> AlwaysKeepImageTools =
        new(System.StringComparer.OrdinalIgnoreCase) { "load_image", "gte_send_request" };

    public static bool ShouldAlwaysKeepImage(string? toolName) =>
        !string.IsNullOrEmpty(toolName) && AlwaysKeepImageTools.Contains(toolName);

    /// <summary>Resolve the correct Anthropic-style "media_type" for a decoded image buffer.</summary>
    public static string ResolveMediaType(byte[] bytes, string filePathForFallback) =>
        DetectImageMagic(bytes) ?? ToolsIOHelpers.GetMediaType(filePathForFallback);

    public static bool IsLikelyBase64(string s)
    {
        if (s.Length < 100) return false;

        int sampleLen  = Math.Min(s.Length, 400);
        int validCount = 0;

        for (int i = 0; i < sampleLen; i++)
        {
            char c = s[i];
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=' ||
                c == '\n' || c == '\r' || c == ' ')
            {
                validCount++;
            }
        }

        return (double)validCount / sampleLen > 0.95;
    }

    public static string? DetectImageMagic(byte[] bytes)
    {
        if (bytes.Length < 4) return null;

        // PNG: 89 50 4E 47 (\x89PNG)
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";

        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";

        // GIF87a / GIF89a: 47 49 46 38 (GIF8)
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            return "image/gif";

        // WEBP: RIFF????WEBP (bytes 0-3 = RIFF, bytes 8-11 = WEBP)
        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return "image/webp";

        return null;
    }
}
