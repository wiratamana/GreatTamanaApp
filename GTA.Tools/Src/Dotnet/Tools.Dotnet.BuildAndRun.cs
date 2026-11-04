#if !CO_WORKER_USE
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsDotnet
    {
        [ToolTag("external", "build", "run")]
        [FileMutating]
        [ToolInfo(
            name: "dotnet_build_and_run",
            description: "Build a .NET console project with 'dotnet build', and — only if the build succeeds — " +
                         "immediately run it (via 'dotnet run --no-build') and capture its output. " +
                         "If the build fails, returns the build errors and does NOT attempt to run. " +
                         "Since the run step is synchronous, a timeout is always enforced on it.")]
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
            description: "Maximum time in milliseconds to wait for the RUN step to finish before it is killed. " +
                         "Defaults to 60000 (1 minute). Does not apply to the build step, which uses a longer " +
                         "internal timeout.",
            required: false)]
        public static object DotnetBuildAndRun(JsonObject input)
        {
            string? err = ToolsDotnetHelpers.ResolveProjectPath(input, out string projectPath);
            if (err != null) return err;

            string buildConfig = ToolsDotnetHelpers.ResolveBuildConfig(input);

            string? timeoutErr = ToolsDotnetHelpers.ResolveTimeoutMs(
                input, ToolsDotnetHelpers.DEFAULT_RUN_TIMEOUT_MS, out int runTimeoutMs);
            if (timeoutErr != null) return timeoutErr;

            // ── Step 1: build ──────────────────────────────────────────────────
            var buildArgs = new List<string> { "build", projectPath, "-c", buildConfig, "-v", "minimal" };
            string? buildLaunchErr = ToolsDotnetHelpers.RunDotnet(
                buildArgs, ToolsDotnetHelpers.DEFAULT_TIMEOUT_MS,
                out string buildStdout, out string buildStderr, out int buildExitCode, out bool buildTimedOut);
            if (buildLaunchErr != null) return buildLaunchErr;

            string buildResult = ToolsDotnetHelpers.FormatBuildResult(
                projectPath, buildConfig, buildExitCode, buildStdout, buildStderr, buildTimedOut);

            if (buildTimedOut || buildExitCode != 0)
                return buildResult + "\n\n(Run step skipped — the build did not succeed.)";

            // ── Step 2: run (skip rebuilding — step 1 already produced fresh binaries) ──
            string? programArgs = input["arguments"]?.GetValue<string>();

            var runArgs = new List<string> { "run", "--project", projectPath, "-c", buildConfig, "--no-build" };
            if (!string.IsNullOrWhiteSpace(programArgs))
            {
                runArgs.Add("--");
                foreach (string a in programArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    runArgs.Add(a);
            }

            string? runLaunchErr = ToolsDotnetHelpers.RunDotnet(
                runArgs, runTimeoutMs,
                out string runStdout, out string runStderr, out int runExitCode, out bool runTimedOut);
            if (runLaunchErr != null) return runLaunchErr;

            string runResult = ToolsDotnetHelpers.FormatRunResult(
                projectPath, buildConfig, runExitCode, runStdout, runStderr, runTimedOut);

            var sb = new StringBuilder();
            sb.AppendLine(buildResult);
            sb.AppendLine();
            sb.AppendLine("──────────── run ────────────");
            sb.AppendLine();
            sb.AppendLine(runResult);
            return sb.ToString().TrimEnd();
        }
    }
}
#endif
