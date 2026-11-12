using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GTA.Tools;

public static class ImageEvidenceStore
{
    // ── Marker format ─────────────────────────────────────────────────────────
    // [[SAVED_IMAGE path="C:\...\evidence\img_xxxxxxxx.png" type="image/png" size="842 KB"]]
    private const string MarkerToken = "[[SAVED_IMAGE ";

    private static readonly Regex MarkerRegex = new(
        "\\[\\[SAVED_IMAGE path=\"(?<path>[^\"]+)\"(?:\\s+type=\"(?<type>[^\"]*)\")?(?:\\s+size=\"(?<size>[^\"]*)\")?\\]\\]",
        RegexOptions.Compiled);

    /// <summary>Fast substring check — true if <paramref name="text"/> contains at least one saved-image marker.</summary>
    public static bool ContainsMarker(string? text) =>
        !string.IsNullOrEmpty(text) && text.IndexOf(MarkerToken, StringComparison.Ordinal) >= 0;

    /// <summary>One parsed marker occurrence.</summary>
    public readonly record struct SavedImageRef(string Path, string MediaType, string SizeText);

    /// <summary>Extract every saved-image marker found in <paramref name="text"/>, in order.</summary>
    public static IEnumerable<SavedImageRef> FindAll(string text)
    {
        foreach (Match m in MarkerRegex.Matches(text))
        {
            yield return new SavedImageRef(
                m.Groups["path"].Value,
                m.Groups["type"].Success ? m.Groups["type"].Value : "image",
                m.Groups["size"].Success ? m.Groups["size"].Value : "");
        }
    }

    /// <summary>Build the marker text for a single saved image file.</summary>
    public static string BuildMarkerText(string path, string mediaType, long byteSize) =>
        $"{MarkerToken}path=\"{path}\" type=\"{mediaType}\" size=\"{FormatBytes(byteSize)}\"]]\n" +
        "(Image content removed from this text to save tokens — the original image was saved " +
        "to the file path above. Use a file tool such as load_image or read_file_base64 to view it again if needed.)";

    public static string? TrySaveBase64ToFile(
        string base64Data, string? mediaTypeHint, string evidenceDir, out long byteSize)
    {
        byteSize = 0;
        try
        {
            string clean = base64Data.Replace("\n", "").Replace("\r", "").Replace(" ", "");
            byte[] bytes = Convert.FromBase64String(clean);

            string? magic = ImageUtils.DetectImageMagic(bytes);
            string ext = ExtFromMediaType(mediaTypeHint) ?? ExtFromMediaType(magic) ?? "png";

            Directory.CreateDirectory(evidenceDir);
            string fileName = $"img_{Guid.NewGuid():N}.{ext}";
            string fullPath = Path.Combine(evidenceDir, fileName);
            File.WriteAllBytes(fullPath, bytes);

            byteSize = bytes.LongLength;
            return fullPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Saves every base64 image in <paramref name="rawResult"/> to a file and returns the text with a small
    /// "[[SAVED_IMAGE …]]" marker instead. Tools in <see cref="ImageUtils.AlwaysKeepImageTools"/> keep their image
    /// for the MODEL (the agent loop sends the raw result), so they are only left alone when
    /// <paramref name="forDisplay"/> is false. With <paramref name="forDisplay"/> = true (the chat window, the saved
    /// display record and the journal) the image is ALWAYS replaced by the marker: the window shows it from the file.
    /// Without this, every load_image screenshot was kept as a multi-MB string in memory and in session.json twice.
    /// </summary>
    public static string SaveAndRedact(string rawResult, string evidenceDir, string? toolName = null, bool forDisplay = false)
    {
        if (string.IsNullOrEmpty(rawResult)) return rawResult;
        if (!forDisplay && ImageUtils.ShouldAlwaysKeepImage(toolName)) return rawResult;

        try
        {
            string trimmed = rawResult.TrimStart();

            // ── Case 1: JSON content-block array/object (Anthropic format) ──────
            if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
            {
                JsonNode? node = JsonNode.Parse(rawResult);
                bool wasObject = node is JsonObject;

                JsonArray? blocks = node switch
                {
                    JsonArray arr  => arr,                                   // mutate in place
                    JsonObject obj => new JsonArray { obj.DeepClone() },      // clone → single-item array
                    _ => null,
                };

                if (blocks != null)
                {
                    bool changed = false;

                    for (int i = 0; i < blocks.Count; i++)
                    {
                        JsonNode? item = blocks[i];
                        if (item?["type"]?.GetValue<string>() != "image") continue;

                        JsonNode? source = item["source"];
                        if (source?["type"]?.GetValue<string>() != "base64") continue;

                        string? data      = source["data"]?.GetValue<string>();
                        string  mediaType = source["media_type"]?.GetValue<string>() ?? "image/png";
                        if (string.IsNullOrEmpty(data)) continue;

                        string? savedPath = TrySaveBase64ToFile(data, mediaType, evidenceDir, out long size);
                        if (savedPath == null) continue;

                        blocks[i] = new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = BuildMarkerText(savedPath, mediaType, size),
                        };
                        changed = true;
                    }

                    if (changed)
                        return wasObject ? blocks[0]!.ToJsonString() : blocks.ToJsonString();
                }

                // Valid JSON but no image blocks found — return unchanged.
                return rawResult;
            }

            // ── Case 2: bare base64 image string (no JSON wrapper) ───────────────
            if (ImageUtils.IsLikelyBase64(rawResult))
            {
                string clean = rawResult.Replace("\n", "").Replace("\r", "").Replace(" ", "");
                byte[] bytes = Convert.FromBase64String(clean);
                string? magic = ImageUtils.DetectImageMagic(bytes);
                if (magic != null)
                {
                    string? savedPath = TrySaveBase64ToFile(clean, magic, evidenceDir, out long size);
                    if (savedPath != null)
                        return BuildMarkerText(savedPath, magic, size);
                }
            }
        }
        catch
        {
            // Any parse/decode failure → leave the original result untouched.
        }

        return rawResult;
    }

    public static string TruncateForDisplay(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;
        if (ContainsMarker(text)) return text;

        string trimmed = text.TrimStart();
        if (trimmed.StartsWith("[") || trimmed.StartsWith("{")) return text;

        return text[..maxChars] + $"\n… (+{text.Length - maxChars} chars)";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? ExtFromMediaType(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType)) return null;
        return mediaType.ToLowerInvariant() switch
        {
            "image/png"  => "png",
            "image/jpeg" => "jpg",
            "image/jpg"  => "jpg",
            "image/gif"  => "gif",
            "image/webp" => "webp",
            _            => null,
        };
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        return $"{mb:0.##} MB";
    }
}
