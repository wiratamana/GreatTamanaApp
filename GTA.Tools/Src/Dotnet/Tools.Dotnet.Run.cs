#if !CO_WORKER_USE
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsDotnet
    {
        [ToolTag("external", "run")]
        [ToolInfo(
            name: "dotnet_run",
            description: "Run a .NET console project using 'dotnet run' and capture its console output. " +
                         "Since this call is synchronous, a timeout is always enforced — if the program " +
                         "does not exit within 'timeout_ms' it is forcibly killed and whatever output was " +
                         "captured so far is returned. Do NOT use this against a project that is already " +
                         "running elsewhere / meant to run indefinitely — it will simply time out and be killed.")]
        [Parameter(index: 0, name: "project_path",
            type: "string",
            description: "Full absolute path to the .csproj file, or the project directory containing it.",
            required: true)]
        [Parameter(index: 1, name: "arguments",
            type: "string",
            description: "Optional command-line arguments to pass to the running program (space-separated).",
            required: false)]
        [Parameter(index: 2, name: "build_config",
            type: "string",
            description: "Build configuration: 'Release' or 'Debug'. Defaults to 'Release'.",
            required: false)]
        [Parameter(index: 3, name: "timeout_ms",
            type: "integer",
            description: "Maximum time in milliseconds to wait for the program to exit before it is killed. " +
                         "Defaults to 60000 (1 minute).",
            required: false)]
        public static object DotnetRun(JsonObject input)
        {
            string? err = ToolsDotnetHelpers.ResolveProjectPath(input, out string projectPath);
            if (err != null) return err;

            string buildConfig = ToolsDotnetHelpers.ResolveBuildConfig(input);

            string? timeoutErr = ToolsDotnetHelpers.ResolveTimeoutMs(
                input, ToolsDotnetHelpers.DEFAULT_RUN_TIMEOUT_MS, out int timeoutMs);
            if (timeoutErr != null) return timeoutErr;

            string? programArgs = input["arguments"]?.GetValue<string>();

            var args = new List<string> { "run", "--project", projectPath, "-c", buildConfig };
            if (!string.IsNullOrWhiteSpace(programArgs))
            {
                args.Add("--");
                foreach (string a in programArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    args.Add(a);
            }

            string? launchErr = ToolsDotnetHelpers.RunDotnet(
                args, timeoutMs,
                out string stdout, out string stderr, out int exitCode, out bool timedOut);
            if (launchErr != null) return launchErr;

            return ToolsDotnetHelpers.FormatRunResult(
                projectPath, buildConfig, exitCode, stdout, stderr, timedOut);
        }
    }
}
#endif
