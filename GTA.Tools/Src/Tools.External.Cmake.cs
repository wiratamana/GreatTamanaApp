#if !CO_WORKER_USE
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using GTA.Core;
using GTA.Tools.Impl.External;

namespace GTA.Tools
{
    internal static class ToolsExternalCmake
    {
        [RecordToLog]
        [ToolTag("external", "cmake")]
        [FileMutating]
        [ToolInfo(
            name: "cmake",
            description: "Run CMake locally (configure via \"-S <src> -B <build>\", build via " +
                         "\"--build <dir>\", install via \"--install <dir>\", etc.) using the raw " +
                         "command-line arguments you supply. NETWORK: every invocation gets the " +
                         "corporate proxy injected by default via HTTP_PROXY/HTTPS_PROXY environment " +
                         "variables (TLS certificate verification stays ON). KNOWN LIMITATION: a step that " +
                         "downloads something via a tool with no TLS-bypass knob (e.g. plain curl) MAY " +
                         "still fail with a TLS/SSL error against this proxy's inspection certificate — " +
                         "a known, accepted risk, not a tool malfunction.")]
        [Parameter(index: 0, name: "arguments",
            type: "string",
            description: "Raw command-line arguments to pass to cmake, exactly as you would type them " +
                         "after \"cmake\", e.g. \"-S . -B build -G Ninja\" or \"--build build --config Release\".",
            required: true)]
        [Parameter(index: 1, name: "working_directory",
            type: "string",
            description: "Optional. Full absolute Windows directory to run cmake from. Defaults to " +
                         "this process's current directory.",
            required: false)]
        [Parameter(index: 2, name: "timeout_seconds",
            type: "integer",
            description: "Optional. Max seconds to wait before force-killing cmake (and its entire " +
                         "child-process tree). Defaults to 300 (5 minutes) — use a larger value for a " +
                         "slow clone/download.",
            required: false)]
        public static object Cmake(JsonObject input)
        {
            string? arguments = input["arguments"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(arguments))
                return "Error: 'arguments' parameter is missing or empty. Supply the raw cmake " +
                       "command-line arguments, e.g. \"-S . -B build\".";

            string? workingDirectory = input["working_directory"]?.GetValue<string>()?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                workingDirectory = null;
            }
            else if (!Directory.Exists(workingDirectory))
            {
                return $"Error: working_directory '{workingDirectory}' does not exist.";
            }

            int timeoutSeconds = ToolsIOHelpers.GetOptionalInt(input["timeout_seconds"]) ?? 300;
            if (timeoutSeconds <= 0) timeoutSeconds = 300;

            // Proxy is applied inside ExternalProcessUtils (ProxyBridge.ApplyToStartInfo, the one global method).
            return ExternalProcessUtils.RunCommand(
                "cmake", arguments, workingDirectory, timeoutSeconds * 1000,
                null, ToolRegistry.ReportProgress);
        }
    }
}
#endif
