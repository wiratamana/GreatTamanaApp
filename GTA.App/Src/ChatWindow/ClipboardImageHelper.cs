using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;

namespace GTA.App;

internal static class ClipboardImageHelper
{
    /// <summary>Candidate clipboard format names for an already-encoded PNG.</summary>
    private static readonly string[] PngFormatCandidates =
        { "PNG", "image/png", "image/x-png" };

    private static readonly string[] DibFormatCandidates =
        { "DeviceIndependentBitmap", "Bitmap" };

    /// <summary>Tries, in priority order, to pull an image out of the clipboard's reported formats</summary>
    public static async Task<string?> TryExtractClipboardImageAsync(IClipboard clipboard, string saveDir)
    {
        string[] formats = await clipboard.GetFormatsAsync();

        string? pngFormat = FindFormat(formats, PngFormatCandidates);
        if (pngFormat != null)
        {
            byte[]? bytes = ExtractBytes(await clipboard.GetDataAsync(pngFormat));
            if (IsPngSignature(bytes))
                return SaveImage(bytes!, saveDir);
        }

        string? dibFormat = FindFormat(formats, DibFormatCandidates);
        if (dibFormat != null)
        {
            byte[]? dib = ExtractBytes(await clipboard.GetDataAsync(dibFormat));
            byte[]? png = ConvertDibToPng(dib);
            if (png != null)
                return SaveImage(png, saveDir);
        }

        return null;
    }

    /// <summary>Case-insensitive lookup of the first matching format name present in both lists.</summary>
    private static string? FindFormat(string[] available, string[] candidates)
    {
        foreach (var candidate in candidates)
            foreach (var fmt in available)
                if (string.Equals(fmt, candidate, StringComparison.OrdinalIgnoreCase))
                    return fmt;
        return null;
    }

    private static bool IsPngSignature(byte[]? bytes) =>
        bytes is { Length: > 8 } &&
        bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    /// <summary>Normalizes the various shapes clipboard data can arrive in (byte[] / Stream) into byte[].</summary>
    private static byte[]? ExtractBytes(object? data)
    {
        switch (data)
        {
            case null:            return null;
            case byte[] bytes:    return bytes;
            case MemoryStream ms: return ms.ToArray();
            case Stream s:
                using (var mem = new MemoryStream())
                {
                    s.CopyTo(mem);
                    return mem.ToArray();
                }
            default: return null;
        }
    }

    private static byte[]? ConvertDibToPng(byte[]? dib)
    {
        if (dib is not { Length: >= 40 }) return null;

        try
        {
            int biSize        = BitConverter.ToInt32(dib, 0);
            short biBitCount   = BitConverter.ToInt16(dib, 14);
            int biCompression  = BitConverter.ToInt32(dib, 16);
            int biClrUsed      = BitConverter.ToInt32(dib, 32);

            // Palette table (only present for <= 8 bits/pixel).
            int paletteSize = biBitCount <= 8
                ? (biClrUsed > 0 ? biClrUsed : (1 << biBitCount)) * 4
                : 0;

            // BI_BITFIELDS (3)
            int maskSize = (biCompression == 3 && biSize == 40 && biBitCount is 16 or 32) ? 12 : 0;

            int pixelOffset = 14 + biSize + maskSize + paletteSize;

            using var bmpStream = new MemoryStream();
            bmpStream.WriteByte((byte)'B');
            bmpStream.WriteByte((byte)'M');
            bmpStream.Write(BitConverter.GetBytes(14 + dib.Length), 0, 4); // bfSize
            bmpStream.Write(new byte[4], 0, 4);                            // bfReserved1/2
            bmpStream.Write(BitConverter.GetBytes(pixelOffset), 0, 4);     // bfOffBits
            bmpStream.Write(dib, 0, dib.Length);
            bmpStream.Position = 0;

            using var decoded = new Bitmap(bmpStream);
            using var pngStream = new MemoryStream();
            decoded.Save(pngStream);
            return pngStream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes PNG bytes to a timestamped "clipboard_*.png" file under <paramref name="dir"/>.</summary>
    private static string SaveImage(byte[] pngBytes, string dir)
    {
        Directory.CreateDirectory(dir);

        string fileName = $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
        string fullPath = Path.Combine(dir, fileName);
        File.WriteAllBytes(fullPath, pngBytes);
        return fullPath;
    }
}
