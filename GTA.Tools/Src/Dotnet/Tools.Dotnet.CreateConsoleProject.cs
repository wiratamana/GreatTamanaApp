#if !CO_WORKER_USE
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsDotnet
    {
        [ToolTag("external", "build")]
        [FileMutating]
        [ToolInfo(
            name: "dotnet_create_console_project",
            description: "Create a new .NET console project using 'dotnet new console'. " +
                         "Creates the target directory if it doesn't exist already.")]
        [Parameter(index: 0, name: "output_path",
            type: "string",
            description: "Full absolute Windows path of the directory where the new console project should be created.",
            required: true)]
        [Parameter(index: 1, name: "project_name",
            type: "string",
            description: "Name of the new project (used for the .csproj file and default namespace). " +
                         "If omitted, defaults to the output directory's folder name.",
            required: false)]
        [Parameter(index: 2, name: "framework",
            type: "string",
            description: "Target framework moniker, e.g. 'net8.0', 'net10.0'. If omitted, uses the installed SDK's default.",
            required: false)]
        [Parameter(index: 3, name: "force",
            type: "boolean",
            description: "If true, overwrite existing files already present in the output directory. Defaults to false.",
            required: false)]
        public static object DotnetCreateConsoleProject(JsonObject input)
        {
            string? rawPath = input["output_path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rawPath))
                return "Error: 'output_path' parameter is missing or empty.";

            string outputPath = ToolsIOHelpers.CleanPath(rawPath);

            string? projectName = input["project_name"]?.GetValue<string>()?.Trim();
            string? framework   = input["framework"]?.GetValue<string>()?.Trim();
            bool    force       = ToolsIOHelpers.GetOptionalBool(input, "force", false);

            try
            {
                Directory.CreateDirectory(outputPath);
            }
            catch (Exception ex)
            {
                return $"Error: Could not create directory '{outputPath}': {ex.Message}";
            }

            var args = new List<string> { "new", "console", "-o", outputPath };
            if (!string.IsNullOrWhiteSpace(projectName)) { args.Add("-n"); args.Add(projectName!); }
            if (!string.IsNullOrWhiteSpace(framework))   { args.Add("-f"); args.Add(framework!); }
            if (force) args.Add("--force");

            string? launchErr = ToolsDotnetHelpers.RunDotnet(
                args, ToolsDotnetHelpers.DEFAULT_TIMEOUT_MS,
                out string stdout, out string stderr, out int exitCode, out bool timedOut);
            if (launchErr != null) return launchErr;

            if (timedOut)
                return $"Error: 'dotnet new console' timed out while scaffolding '{outputPath}'.";

            if (exitCode != 0)
            {
                string combinedErr = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\n" + stderr;
                return $"Error: Failed to create console project at '{outputPath}'.\n{combinedErr.Trim()}";
            }

            var sb = new StringBuilder();
            sb.AppendLine("Console project created successfully.");
            sb.AppendLine($"  Location: {outputPath}");
            if (!string.IsNullOrWhiteSpace(projectName)) sb.AppendLine($"  Name    : {projectName}");
            if (!string.IsNullOrWhiteSpace(framework))   sb.AppendLine($"  Target  : {framework}");
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                sb.AppendLine();
                sb.AppendLine(stdout.Trim());
            }
            return sb.ToString().TrimEnd();
        }
    }
}
#endif
