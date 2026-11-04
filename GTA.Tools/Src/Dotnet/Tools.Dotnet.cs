#if !CO_WORKER_USE
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Dotnet;

namespace GTA.Tools
{
    internal static class ToolsDotnetHelpers
    {
        /// <summary>Default timeout for build / project-scaffolding child processes (10 minutes).</summary>
        internal const int DEFAULT_TIMEOUT_MS = DotnetProcessUtils.DEFAULT_TIMEOUT_MS;

        /// <summary>Default timeout for `dotnet run` when the caller doesn't specify one (1 minute).</summary>
        internal const int DEFAULT_RUN_TIMEOUT_MS = DotnetProcessUtils.DEFAULT_RUN_TIMEOUT_MS;

        // ── Path validation (JsonObject-based — stays here) ──────────────────

        /// <summary>Resolve+validate the required "project_path" input — accepts either a .csproj/.sln FILE or a project DIRECTORY containing one.</summary>
        internal static string? ResolveProjectPath(JsonObject input, out string projectPath)
        {
            projectPath = string.Empty;

            string? raw = input["project_path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(raw))
                return "Error: 'project_path' parameter is missing or empty.";

            string path = ToolsIOHelpers.CleanPath(raw);
            if (!File.Exists(path) && !Directory.Exists(path))
                return $"Error: Project path not found: '{path}'.";

            projectPath = path;
            return null;
        }

        /// <summary>Read the optional "build_config" input, defaulting to "Release".</summary>
        internal static string ResolveBuildConfig(JsonObject input)
        {
            string? cfg = input["build_config"]?.GetValue<string>()?.Trim();
            return string.IsNullOrWhiteSpace(cfg) ? "Release" : cfg!;
        }

        /// <summary>Read an optional non-negative "timeout_ms" input.</summary>
        internal static string? ResolveTimeoutMs(JsonObject input, int defaultValue, out int timeoutMs)
        {
            timeoutMs = defaultValue;
            JsonNode? node = input["timeout_ms"];
            if (node == null) return null;

            bool parsed = false;
            try { timeoutMs = node.GetValue<int>(); parsed = true; }
            catch { /* not a plain integer — try other representations below */ }

            if (!parsed)
            {
                try
                {
                    double asDouble = node.GetValue<double>();
                    timeoutMs = (int)asDouble;
                    parsed = true;
                }
                catch { /* not a plain number either — try string below */ }
            }

            if (!parsed)
            {
                string? asString = null;
                try { asString = node.GetValue<string>(); } catch { /* not a string node */ }

                if (asString != null &&
                    int.TryParse(asString.Trim(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int fromString))
                {
                    timeoutMs = fromString;
                    parsed = true;
                }
            }

            if (!parsed)
                return "Error: 'timeout_ms' must be a valid integer.";

            if (timeoutMs <= 0)
                return $"Error: 'timeout_ms' must be > 0, but got {timeoutMs}.";

            return null;
        }

        // ── Process launch / result formatting (forwarded to DotnetProcessUtils) ──

        /// <summary>See <see cref="DotnetProcessUtils.RunDotnet"/>.</summary>
        internal static string? RunDotnet(
            List<string> args,
            int timeoutMs,
            out string stdout,
            out string stderr,
            out int exitCode,
            out bool timedOut)
            => DotnetProcessUtils.RunDotnet(args, timeoutMs, out stdout, out stderr, out exitCode, out timedOut);

        /// <summary>See <see cref="DotnetProcessUtils.FormatBuildResult"/>.</summary>
        internal static string FormatBuildResult(
            string projectPath, string buildConfig,
            int exitCode, string stdout, string stderr, bool timedOut)
            => DotnetProcessUtils.FormatBuildResult(projectPath, buildConfig, exitCode, stdout, stderr, timedOut);

        /// <summary>See <see cref="DotnetProcessUtils.FormatRunResult"/>.</summary>
        internal static string FormatRunResult(
            string projectPath, string buildConfig,
            int exitCode, string stdout, string stderr, bool timedOut)
            => DotnetProcessUtils.FormatRunResult(projectPath, buildConfig, exitCode, stdout, stderr, timedOut);
    }
}
#endif
