using System;
using System.Globalization;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.IbmCloud;
using IbmCloudEnvironmentInfo = GTA.Tools.Impl.IbmCloud.IbmCloudLogsApiClient.IbmCloudEnvironmentInfo;

namespace GTA.Tools
{
    internal static partial class ToolsIbmCloudApi
    {
        /// <summary>Read a string parameter from a tool's <see cref="JsonObject"/> input, tolerating non-string JSON values.</summary>
        internal static string? TryGetString(JsonObject input, string key)
        {
            JsonNode? node = input[key];
            if (node is null) return null;

            if (node is JsonValue value)
            {
                if (value.TryGetValue<string>(out string? s)) return s;
                if (value.TryGetValue<long>(out long l))      return l.ToString(CultureInfo.InvariantCulture);
                if (value.TryGetValue<double>(out double d))  return d.ToString(CultureInfo.InvariantCulture);
                if (value.TryGetValue<bool>(out bool b))      return b ? "true" : "false";
            }

            // Last resort — never throw, just stringify whatever is there.
            try { return node.ToString(); } catch { return null; }
        }

        /// <summary>Read an integer parameter, tolerating it arriving as a JSON number (int/long/double) OR a numeric string.</summary>
        internal static int? TryGetInt(JsonObject input, string key)
        {
            JsonNode? node = input[key];
            if (node is not JsonValue value) return null;

            if (value.TryGetValue<int>(out int i))       return i;
            if (value.TryGetValue<long>(out long l))     return unchecked((int)l);
            if (value.TryGetValue<double>(out double d)) return (int)d;

            if (value.TryGetValue<string>(out string? s) && !string.IsNullOrWhiteSpace(s))
            {
                s = s.Trim();
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedInt))
                    return parsedInt;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedD))
                    return (int)parsedD;
            }

            return null;
        }

        /// <summary>Convenience wrapper: TryGetInt with a fallback default.</summary>
        internal static int GetIntOrDefault(JsonObject input, string key, int defaultValue)
            => TryGetInt(input, key) ?? defaultValue;

        /// <summary>Read a 64-bit integer parameter (used for epoch-millisecond timestamps), tolerating it arriving as a JSON number OR a numeric string.</summary>
        internal static long? TryGetLong(JsonObject input, string key)
        {
            JsonNode? node = input[key];
            if (node is not JsonValue value) return null;

            if (value.TryGetValue<long>(out long l))     return l;
            if (value.TryGetValue<int>(out int i))       return i;
            if (value.TryGetValue<double>(out double d)) return (long)d;

            if (value.TryGetValue<string>(out string? s) && !string.IsNullOrWhiteSpace(s))
            {
                s = s.Trim();
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedLong))
                    return parsedLong;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedD))
                    return (long)parsedD;
            }

            return null;
        }

        /// <summary>Convenience wrapper: TryGetLong with a fallback default.</summary>
        internal static long GetLongOrDefault(JsonObject input, string key, long defaultValue)
            => TryGetLong(input, key) ?? defaultValue;

        /// <summary>Read a boolean parameter, tolerating it arriving as a JSON bool, a numeric 0/1, or a string ("true"/"false"/"1"/"0", case-insensitive).</summary>
        internal static bool? TryGetBool(JsonObject input, string key)
        {
            JsonNode? node = input[key];
            if (node is not JsonValue value) return null;

            if (value.TryGetValue<bool>(out bool b)) return b;
            if (value.TryGetValue<int>(out int i))   return i != 0;

            if (value.TryGetValue<string>(out string? s) && !string.IsNullOrWhiteSpace(s))
            {
                s = s.Trim();
                if (bool.TryParse(s, out bool parsedBool)) return parsedBool;
                if (s == "1") return true;
                if (s == "0") return false;
            }

            return null;
        }

        /// <summary>Convenience wrapper: TryGetBool with a fallback default.</summary>
        internal static bool GetBoolOrDefault(JsonObject input, string key, bool defaultValue)
            => TryGetBool(input, key) ?? defaultValue;

        /// <summary>Read the 'environment' parameter shared by every ibm_cloud_logs_* tool and resolve it to its configured tenant/region/team triple.</summary>
        internal static IbmCloudEnvironmentInfo ResolveEnvironmentParam(JsonObject input)
            => IbmCloudLogsApiClient.IbmCloudEnvironments.Resolve(TryGetString(input, "environment"));
    }
}
