using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace GTA.App;

/// <summary>
/// Right-click menu for every image shown in the chat:
///   📋 Copy image          — puts the picture on the Windows clipboard (PNG + DIB, so Paint / Word / Teams / Paint.NET all accept it)
///   📂 Show in explorer    — opens Explorer with the image file selected
/// An image that only exists in memory (base64 tool result) is first written to %TEMP%\GTA_images so Explorer has a file to show.
/// </summary>
internal static class ImageContextMenu
{
    /// <summary>
    /// Adds the menu to image. Give filePath when the image comes from a file; give bytes (the encoded
    /// PNG/JPG/... data) when it only exists in memory. At least one of the two should be set.
    /// </summary>
    public static void Attach(Image image, string? filePath, byte[]? bytes = null)
    {
        var copyItem = new MenuItem { Header = "📋  Copy image" };
        copyItem.Click += (_, _) =>
        {
            try
            {
                byte[]? data = ReadEncodedBytes(filePath, bytes);
                if (data == null) return;
                IntPtr owner = TopLevel.GetTopLevel(image)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                WinClipboardImage.Set(data, owner);
            }
            catch { /* clipboard busy / image not decodable — nothing useful to tell the user */ }
        };

        var showItem = new MenuItem { Header = "📂  Show in explorer" };
        showItem.Click += (_, _) =>
        {
            try
            {
                string? path = filePath != null && File.Exists(filePath) ? filePath : SaveToTemp(bytes);
                if (path != null) ShowInExplorer(path);
            }
            catch { /* explorer could not be started */ }
        };

        var menu = new ContextMenu();
        menu.Items.Add(copyItem);
        menu.Items.Add(showItem);
        image.ContextMenu = menu;
    }

    /// <summary>Opens Explorer with file selected.</summary>
    public static void ShowInExplorer(string file)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName        = "explorer.exe",
            Arguments       = $"/select,\"{file}\"",
            UseShellExecute = false,
        });
    }

    /// <summary>Opens Explorer on a folder.</summary>
    public static void OpenFolder(string folder)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName        = "explorer.exe",
            Arguments       = $"\"{folder}\"",
            UseShellExecute = false,
        });
    }

    private static byte[]? ReadEncodedBytes(string? filePath, byte[]? bytes)
    {
        if (filePath != null && File.Exists(filePath)) return File.ReadAllBytes(filePath);
        return bytes;
    }

    private static string? SaveToTemp(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;

        string dir = Path.Combine(Path.GetTempPath(), "GTA_images");
        Directory.CreateDirectory(dir);

        string name = "image_" + Convert.ToHexString(SHA256.HashData(bytes))[..16] + GuessExtension(bytes);
        string path = Path.Combine(dir, name);
        if (!File.Exists(path))
            File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string GuessExtension(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)                  return ".jpg";
        if (b.Length > 4 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F')   return ".gif";
        if (b.Length > 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F') return ".webp";
        if (b.Length > 2 && b[0] == (byte)'B' && b[1] == (byte)'M')                        return ".bmp";
        return ".png";
    }

    // ── Windows clipboard (Avalonia 11 cannot put a bitmap on the clipboard by itself) ──────────

    private static class WinClipboardImage
    {
        private const uint CF_DIB         = 8;
        private const uint GMEM_MOVEABLE  = 0x0002;

        [DllImport("user32.dll", SetLastError = true)] private static extern bool   OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool   CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool   EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormatW(string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool   GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);

        /// <summary>Puts the encoded image (PNG / JPG / ...) on the clipboard as "PNG" (keeps transparency) and as CF_DIB.</summary>
        public static void Set(byte[] encoded, IntPtr owner)
        {
            using var bmp = new Bitmap(new MemoryStream(encoded));

            byte[] png = IsPng(encoded) ? encoded : EncodePng(bmp);
            byte[]? dib = BuildDib(bmp);

            // The clipboard can be locked by another program for a moment — retry a few times.
            bool opened = false;
            for (int i = 0; i < 10 && !(opened = OpenClipboard(owner)); i++)
                System.Threading.Thread.Sleep(30);
            if (!opened) return;

            try
            {
                EmptyClipboard();
                if (dib != null) Put(CF_DIB, dib);
                Put(RegisterClipboardFormatW("PNG"), png);
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static void Put(uint format, byte[] data)
        {
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
            if (h == IntPtr.Zero) return;

            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) { GlobalFree(h); return; }
            Marshal.Copy(data, 0, p, data.Length);
            GlobalUnlock(h);

            // After a successful SetClipboardData the system owns the memory; on failure we must free it.
            if (SetClipboardData(format, h) == IntPtr.Zero)
                GlobalFree(h);
        }

        private static bool IsPng(byte[] b) =>
            b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

        private static byte[] EncodePng(Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms);
            return ms.ToArray();
        }

        /// <summary>Builds a bottom-up 32-bit BITMAPINFOHEADER + pixels block (CF_DIB), transparent areas flattened onto white.</summary>
        private static byte[]? BuildDib(Bitmap bmp)
        {
            int w = bmp.PixelSize.Width;
            int h = bmp.PixelSize.Height;
            if (w <= 0 || h <= 0) return null;

            var fmt = bmp.Format;
            bool bgra = fmt == PixelFormat.Bgra8888;
            bool rgba = fmt == PixelFormat.Rgba8888;
            if (!bgra && !rgba) return null; // unusual format — the PNG entry still works

            int stride = w * 4;
            var src = new byte[stride * h];
            var pin = GCHandle.Alloc(src, GCHandleType.Pinned);
            try
            {
                bmp.CopyPixels(new PixelRect(0, 0, w, h), pin.AddrOfPinnedObject(), src.Length, stride);
            }
            finally
            {
                pin.Free();
            }

            bool premul = bmp.AlphaFormat == AlphaFormat.Premul;

            const int header = 40;
            var dib = new byte[header + src.Length];
            BitConverter.GetBytes(header).CopyTo(dib, 0);          // biSize
            BitConverter.GetBytes(w).CopyTo(dib, 4);               // biWidth
            BitConverter.GetBytes(h).CopyTo(dib, 8);               // biHeight (positive = bottom-up)
            BitConverter.GetBytes((short)1).CopyTo(dib, 12);       // biPlanes
            BitConverter.GetBytes((short)32).CopyTo(dib, 14);      // biBitCount
            BitConverter.GetBytes(0).CopyTo(dib, 16);              // biCompression = BI_RGB
            BitConverter.GetBytes(src.Length).CopyTo(dib, 20);     // biSizeImage

            for (int y = 0; y < h; y++)
            {
                int srcRow = y * stride;
                int dstRow = header + (h - 1 - y) * stride;
                for (int x = 0; x < w; x++)
                {
                    int s = srcRow + x * 4;
                    int d = dstRow + x * 4;

                    byte c0 = src[s], c1 = src[s + 1], c2 = src[s + 2], a = src[s + 3];
                    byte b = bgra ? c0 : c2;
                    byte r = bgra ? c2 : c0;
                    byte g = c1;

                    if (a < 255)
                    {
                        int inv = 255 - a;
                        if (premul)
                        {
                            b = (byte)Math.Min(255, b + inv);
                            g = (byte)Math.Min(255, g + inv);
                            r = (byte)Math.Min(255, r + inv);
                        }
                        else
                        {
                            b = (byte)((b * a + 255 * inv) / 255);
                            g = (byte)((g * a + 255 * inv) / 255);
                            r = (byte)((r * a + 255 * inv) / 255);
                        }
                    }

                    dib[d]     = b;
                    dib[d + 1] = g;
                    dib[d + 2] = r;
                    dib[d + 3] = 255;
                }
            }

            return dib;
        }
    }
}
