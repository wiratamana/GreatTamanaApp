#if !CO_WORKER_USE
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsDotnet
    {
        [ToolTag("external", "build")]
        [FileMutating]
        [ToolInfo(
            name: "dotnet_build",
            description: "Build a .NET project using 'dotnet build'. " +
                         "On success returns the output artifact path(s) and warning count. " +
                         "On failure returns the exact compiler error messages so you can fix them.")]
        [Parameter(index: 0, name: "project_path",
            type: "string",
            description: "Full absolute path to the .csproj / .sln file, " +
                         "or the project directory containing it.",
            required: true)]
        [Parameter(index: 1, name: "build_config",
            type: "string",
            description: "Build configuration: 'Release' or 'Debug'. Defaults to 'Release'.",
            required: false)]
        [Parameter(index: 2, name: "output",
            type: "string",
            description: "Custom output directory for build artifacts. " +
                         "If omitted the default bin/<config>/<tfm>/ path inside the project is used.",
            required: false)]
        public static object DotnetBuild(JsonObject input)
        {
            string? err = ToolsDotnetHelpers.ResolveProjectPath(input, out string projectPath);
            if (err != null) return err;

            string buildConfig = ToolsDotnetHelpers.ResolveBuildConfig(input);

            string? outputPath = input["output"]?.GetValue<string>()?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(outputPath))
                outputPath = null;

            var args = new List<string> { "build", projectPath, "-c", buildConfig, "-v", "minimal" };
            if (outputPath != null)
            {
                args.Add("-o");
                args.Add(outputPath);
            }

            string? launchErr = ToolsDotnetHelpers.RunDotnet(
                args, ToolsDotnetHelpers.DEFAULT_TIMEOUT_MS,
                out string stdout, out string stderr, out int exitCode, out bool timedOut);
            if (launchErr != null) return launchErr;

            return ToolsDotnetHelpers.FormatBuildResult(
                projectPath, buildConfig, exitCode, stdout, stderr, timedOut);
        }
    }
}
#endif
