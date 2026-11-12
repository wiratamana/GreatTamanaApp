#if !CO_WORKER_USE
using System.IO;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.External;

namespace GTA.Tools
{
    internal static class ToolsExternalGcc
    {
        [RecordToLog]
        [ToolTag("external", "gcc")]
        [FileMutating]
        [ToolInfo(
            name: "gcc",
            description: "Compile C/C++ source code locally using GCC/G++ (the GNU Compiler Collection) " +
                         "installed on this machine. Runs 'gcc.exe' (or 'g++.exe' when use_gpp is true) " +
                         "with the raw command-line arguments you supply, and returns its STDOUT/STDERR " +
                         "(compiler errors/warnings included) so failures can be diagnosed and fixed. " +
                         "This is a purely LOCAL/OFFLINE operation — it never touches the network, " +
                         "Wi-Fi, or proxy settings (unlike the 'cmake' tool).")]
        [Parameter(index: 0, name: "arguments",
            type: "string",
            description: "Raw command-line arguments to pass to gcc/g++, exactly as you would type them " +
                         "after \"gcc\", e.g. \"-o output.exe main.c -Wall -O2\".",
            required: true)]
        [Parameter(index: 1, name: "working_directory",
            type: "string",
            description: "Optional. Full absolute Windows directory to run gcc/g++ from, so relative " +
                         "source/include/output paths in 'arguments' resolve correctly. Defaults to this " +
                         "process's current directory.",
            required: false)]
        [Parameter(index: 2, name: "use_gpp",
            type: "boolean",
            description: "Optional. If true, runs 'g++.exe' (the C++ front-end) instead of the default " +
                         "'gcc.exe' (C front-end). Defaults to false.",
            required: false)]
        [Parameter(index: 3, name: "timeout_seconds",
            type: "integer",
            description: "Optional. Max seconds to wait before force-killing the compile (and its entire " +
                         "child-process tree). Defaults to 300 (5 minutes). Use a larger value for a big " +
                         "translation unit / whole-program build.",
            required: false)]
        public static object Gcc(JsonObject input)
        {
            string? arguments = input["arguments"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(arguments))
                return "Error: 'arguments' parameter is missing or empty. Supply the raw gcc/g++ " +
                       "command-line arguments, e.g. \"-o output.exe main.c -Wall\".";

            string? workingDirectory = input["working_directory"]?.GetValue<string>()?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                workingDirectory = null;
            }
            else if (!Directory.Exists(workingDirectory))
            {
                return $"Error: working_directory '{workingDirectory}' does not exist.";
            }

            bool useGpp = ToolsIOHelpers.GetOptionalBool(input, "use_gpp", false);
            int timeoutSeconds = ToolsIOHelpers.GetOptionalInt(input["timeout_seconds"]) ?? 300;
            if (timeoutSeconds <= 0) timeoutSeconds = 300;

            string exeName = useGpp ? "g++" : "gcc";

            // gcc is purely offline: useProxy:false (no relay started, no proxy variables).
            return ExternalProcessUtils.RunCommand(
                exeName, arguments, workingDirectory, timeoutSeconds * 1000,
                null, ToolRegistry.ReportProgress, useProxy: false);
        }
    }
}
#endif
