using System;
using System.IO;
using System.Text.RegularExpressions;

namespace GTA.XlsxLib
{
    public partial class XlsxExtractorTool
    {
        // 1 point = 12,700 EMU  (per OOXML / ISO 29500 spec)
        private const long EmuPerPoint = 12700L;
        // 1 pixel (96 DPI) = 9,525 EMU
        private const long EmuPerPixel = 9525L;

        /// <summary>Converts points to EMU. 1 Point = 12,700 EMU.</summary>
        private static long PointsToEmu(double points)
            => (long)Math.Round(points * EmuPerPoint);

        /// <summary>Removes characters that are illegal in XML 1.0.</summary>
        private static string SanitizeForXml(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return Regex.Replace(text, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F\uFFFE\uFFFF]", " ");
        }

        /// <summary>Maps image file extension to MIME type.</summary>
        private static string ImageExtToMediaType(string ext)
            => ext.ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif"            => "image/gif",
                ".bmp"            => "image/bmp",
                ".tif" or ".tiff" => "image/tiff",
                _                 => "image/png"
            };

        private static string GetImageExtension(string formatName)
            => formatName.ToLowerInvariant() switch
            {
                "jpeg" => ".jpg", "png"  => ".png", "gif"  => ".gif",
                "tiff" => ".tif", "bmp"  => ".bmp", "emf"  => ".emf",
                "wmf"  => ".wmf", "pcx"  => ".pcx", "pict" => ".pict",
                _      => ".png"
            };

        private static string MakeSafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private static string GetColumnName(int index)
        {
            string name = string.Empty;
            while (index > 0)
            {
                int mod = (index - 1) % 26;
                name  = Convert.ToChar(65 + mod).ToString() + name;
                index = (index - mod) / 26;
            }
            return name;
        }

        private static bool IsExcelTrue(object obj)
        {
            if (obj == null || obj is DBNull) return false;
            if (obj is bool b) return b;
            return obj.ToString() == "True";
        }

        private static bool IsNotExcelNone(object obj)
        {
            if (obj == null || obj is DBNull) return false;
            if (double.TryParse(obj.ToString(), out double val))
                return val != -4142;
            return false;
        }

        private static string GetHexColor(object colorObj, string defaultHex)
        {
            if (colorObj == null || colorObj is DBNull) return defaultHex;
            if (double.TryParse(colorObj.ToString(), out double colorVal))
            {
                if (colorVal < 0) return defaultHex;
                int c = (int)colorVal;
                return $"#{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}";
            }
            return defaultHex;
        }
    }
}
