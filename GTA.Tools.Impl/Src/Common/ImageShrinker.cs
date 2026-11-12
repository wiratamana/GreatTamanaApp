using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using GTA.Core;
using SkiaSharp;

namespace GTA.Tools.Impl.Common;

/// <summary>Result of <see cref="ImageShrinker.Shrink"/>.</summary>
public sealed class ShrinkResult
{
    /// <summary>The image to send. Same as the input when <see cref="Changed"/> is false.</summary>
    public byte[] Bytes     { get; set; } = Array.Empty<byte>();
    public string MediaType { get; set; } = "image/png";

    public bool Changed { get; set; }

    /// <summary>Original size in pixels (0 when the header could not be read).</summary>
    public int  OrigWidth  { get; set; }
    public int  OrigHeight { get; set; }
    public long OrigBytes  { get; set; }

    /// <summary>New size in pixels (same as the original when nothing was changed).</summary>
    public int  NewWidth  { get; set; }
    public int  NewHeight { get; set; }

    /// <summary>True when the image breaks the limits (too many pixels or too many bytes).</summary>
    public bool IsOversize { get; set; }

    /// <summary>Set when the image IS oversize but we could not fix it. Null otherwise.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Makes sure an image is small enough for the Azure / Anthropic API.
/// Why: the API refuses a request when one image is bigger than 3000 px (2000 px on the Anthropic API) in a request that
/// has many images, or when one image is bigger than 5 MB. One bad image fails the WHOLE request (HTTP 400), after the
/// whole body (can be 100+ MB) was uploaded. The model itself scales every image down to ~1568 px on the long side
/// anyway, so sending more pixels gives nothing: only a slow upload.
/// </summary>
public static class ImageShrinker
{
    /// <summary>Longest side we allow, in pixels. This is also the size the model works with internally.</summary>
    public const int MaxLongEdge = 1568;

    /// <summary>Biggest image file we allow, in bytes. Base64 adds 33%, so this stays under the 5 MB API limit.</summary>
    public const int MaxBytes = 3_500_000;

    /// <summary>Above this many pixels we do not even try to decode (a 4-byte-per-pixel bitmap would eat the RAM).</summary>
    private const long MaxDecodePixels = 150_000_000;

    /// <summary>
    /// Returns the image unchanged when it is already small enough. Otherwise returns a smaller PNG / JPEG.
    /// Never throws.
    /// </summary>
    public static ShrinkResult Shrink(byte[] bytes, string mediaType)
    {
        var result = new ShrinkResult
        {
            Bytes     = bytes,
            MediaType = mediaType,
            OrigBytes = bytes.LongLength,
        };

        try
        {
            int w, h;
            using (var codec = SKCodec.Create(new MemoryStream(bytes, writable: false)))
            {
                if (codec == null)
                {
                    // Not an image Skia can read. We can not judge it. Only the byte size is known.
                    result.IsOversize = bytes.Length > MaxBytes;
                    if (result.IsOversize)
                        result.Error = $"the image format is not readable and the file is {bytes.Length / 1024} KB (limit {MaxBytes / 1024} KB)";
                    return result;
                }
                w = codec.Info.Width;
                h = codec.Info.Height;
            }

            result.OrigWidth  = result.NewWidth  = w;
            result.OrigHeight = result.NewHeight = h;

            bool tooBig   = Math.Max(w, h) > MaxLongEdge;
            bool tooHeavy = bytes.Length > MaxBytes;
            result.IsOversize = tooBig || tooHeavy;
            if (!result.IsOversize) return result;

            if ((long)w * h > MaxDecodePixels)
            {
                result.Error = $"the image is {w}x{h} px, too big to resize safely";
                return result;
            }

            using SKBitmap? source = SKBitmap.Decode(bytes);
            if (source == null)
            {
                result.Error = "the image could not be decoded";
                return result;
            }

            double scale = tooBig ? (double)MaxLongEdge / Math.Max(w, h) : 1.0;
            var    owned = new List<SKBitmap>();
            try
            {
                SKBitmap current = source;
                if (scale < 1.0)
                {
                    current = Resize(source, scale) ?? throw new InvalidOperationException("resize returned nothing");
                    owned.Add(current);
                }

                bool useJpeg = mediaType == "image/jpeg";
                int  quality = 85;
                byte[]? encoded = null;

                for (int attempt = 0; attempt < 8; attempt++)
                {
                    encoded = Encode(current, useJpeg, quality);
                    if (encoded.Length <= MaxBytes) break;

                    if (!useJpeg)
                    {
                        // A PNG screenshot that is still too heavy: JPEG is much smaller. JPEG has no alpha: put it on white.
                        useJpeg = true;
                        current = Flatten(current);
                        owned.Add(current);
                    }
                    else if (quality > 60)
                    {
                        quality -= 15;
                    }
                    else
                    {
                        current = Resize(current, 0.75) ?? throw new InvalidOperationException("resize returned nothing");
                        owned.Add(current);
                    }
                }

                if (encoded == null || encoded.Length > MaxBytes)
                {
                    result.Error = "the image is still too big after resizing";
                    return result;
                }

                // Only the byte size was the problem and our re-encode made it bigger: keep the original.
                if (scale >= 1.0 && encoded.Length >= bytes.Length)
                {
                    result.Error = tooHeavy ? "the image could not be made smaller" : null;
                    return result;
                }

                result.Bytes     = encoded;
                result.MediaType = useJpeg ? "image/jpeg" : "image/png";
                result.NewWidth  = current.Width;
                result.NewHeight = current.Height;
                result.Changed   = true;
                return result;
            }
            finally
            {
                foreach (SKBitmap b in owned) b.Dispose();
            }
        }
        catch (Exception ex)
        {
            result.Error = $"resize failed: {ex.Message}";
            return result;
        }
    }

    private static SKBitmap? Resize(SKBitmap src, double scale)
    {
        int nw = Math.Max(1, (int)Math.Round(src.Width  * scale));
        int nh = Math.Max(1, (int)Math.Round(src.Height * scale));
        return src.Resize(
            new SKImageInfo(nw, nh, src.ColorType, src.AlphaType),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    private static SKBitmap Flatten(SKBitmap src)
    {
        var flat = new SKBitmap(src.Width, src.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(flat);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(src, 0, 0);
        return flat;
    }

    private static byte[] Encode(SKBitmap bmp, bool jpeg, int quality)
    {
        using SKImage image = SKImage.FromBitmap(bmp);
        using SKData  data  = jpeg
            ? image.Encode(SKEncodedImageFormat.Jpeg, quality)
            : image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>
/// Last line of defence: runs over the WHOLE chat history right before a request goes to the API and shrinks every
/// image that is too big. It catches images from every source (load_image, Box / Jira tools, user attachments,
/// injections, an old session that was loaded from disk). The history is changed in place, so every image is
/// checked and shrunk only once, and the next requests are smaller too.
/// </summary>
public static class ImageHistoryGuard
{
    // Remembers the (image "source") nodes that were already checked. Weak: does not keep removed messages alive.
    private static readonly ConditionalWeakTable<JsonNode, object> Checked = new();
    private static readonly object Marker = new();

    /// <summary>Returns how many images were changed or replaced. Never throws.</summary>
    public static int ShrinkOversizedImages(JsonArray messages, IAgentLogger? logger)
    {
        int changed = 0;
        try
        {
            foreach (JsonNode? message in messages)
            {
                if (message is JsonObject mo && mo["content"] is JsonArray content)
                    changed += ScanBlocks(content, logger);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning($"[image guard] scan failed: {ex.Message}");
        }
        return changed;
    }

    private static int ScanBlocks(JsonArray blocks, IAgentLogger? logger)
    {
        int changed = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is not JsonObject block) continue;

            string? type = block["type"] is JsonValue tv && tv.TryGetValue(out string? t) ? t : null;
            if (type == "image")
                changed += ProcessImage(blocks, i, block, logger);
            else if (type == "tool_result" && block["content"] is JsonArray inner)
                changed += ScanBlocks(inner, logger);
        }
        return changed;
    }

    private static int ProcessImage(JsonArray parent, int index, JsonObject block, IAgentLogger? logger)
    {
        if (block["source"] is not JsonObject source) return 0;
        if (Checked.TryGetValue(source, out _)) return 0;
        Checked.Add(source, Marker);   // checked once, whatever the outcome

        if (!TryGetString(source, "type", out string? srcType) || srcType != "base64") return 0;
        if (!TryGetString(source, "data", out string? data) || string.IsNullOrEmpty(data)) return 0;
        TryGetString(source, "media_type", out string? media);
        media ??= "image/png";

        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); }
        catch (FormatException) { return 0; }

        ShrinkResult r = ImageShrinker.Shrink(bytes, media);

        if (r.Changed)
        {
            source["data"]       = Convert.ToBase64String(r.Bytes);
            source["media_type"] = r.MediaType;
            logger?.LogInfo(
                $"[image guard] image shrunk: {r.OrigWidth}x{r.OrigHeight} px / {r.OrigBytes / 1024} KB  ->  " +
                $"{r.NewWidth}x{r.NewHeight} px / {r.Bytes.Length / 1024} KB.");
            return 1;
        }

        if (r.IsOversize && r.Error != null)
        {
            // We can not fix it, and the API would refuse the WHOLE request because of this one image.
            // Better to drop the image and tell the model, than to lose the whole turn.
            var replacement = new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"[An image was removed from the history: {r.Error}. The API only accepts images up to " +
                           $"{ImageShrinker.MaxLongEdge} px (long side) and {ImageShrinker.MaxBytes / 1024} KB. " +
                           "Make a smaller version of the image and load it again if you still need it.]",
            };
            if (block["cache_control"] is JsonNode cc)
                replacement["cache_control"] = cc.DeepClone();
            parent[index] = replacement;
            logger?.LogWarning($"[image guard] image REMOVED from the request: {r.Error}.");
            return 1;
        }

        return 0;
    }

    private static bool TryGetString(JsonObject obj, string key, out string? value)
    {
        value = null;
        if (obj[key] is JsonValue v && v.TryGetValue(out string? s)) { value = s; return true; }
        return false;
    }
}
