#if !CO_WORKER_USE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using GTA.Core;

namespace GTA.Tools.Impl.Dotnet
{
    public static class DotnetProcessUtils
    {
        /// <summary>Default timeout for build / project-scaffolding child processes (10 minutes).</summary>
        public const int DEFAULT_TIMEOUT_MS = 10 * 60 * 1000;

        /// <summary>Default timeout for `dotnet run` when the caller doesn't specify one (1 minute).</summary>
        public const int DEFAULT_RUN_TIMEOUT_MS = 60 * 1000;

        public static string? RunDotnet(
            List<string> args,
            int timeoutMs,
            out string stdout,
            out string stderr,
            out int exitCode,
            out bool timedOut)
        {
            stdout   = string.Empty;
            stderr   = string.Empty;
            exitCode = -1;
            timedOut = false;

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8
            };
            foreach (string a in args)
                psi.ArgumentList.Add(a);

            // Proxy: ONE global method (Windows system proxy, current Windows login via local relay;
            // direct connection when Windows uses no proxy). Disposed when this method returns.
            using IDisposable proxyScope = ProxyBridge.ApplyToStartInfo(psi);

            try
            {
                using Process proc = Process.Start(psi)!;

                // ✅ Read both streams concurrently to prevent buffer-fill deadlock.
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                // Waits in small steps: kills the whole tree on timeout AND when the user presses STOP.
                bool exited = ToolCancellation.WaitForExitOrKill(proc, timeoutMs, out bool cancelled);
                if (!exited)
                    timedOut = true;

                // Drain whatever output is available — streams close once the
                // process (or its killed tree) exits, so these complete either way.
                try { stdout = stdoutTask.Result; } catch { }
                try { stderr = stderrTask.Result; } catch { }

                if (cancelled)
                {
                    string partial = (stdout + "\n" + stderr).Trim();
                    if (partial.Length > 4000) partial = "…" + partial[^4000..];
                    return $"⏹ STOPPED BY USER: 'dotnet {string.Join(' ', args)}' was killed (with all its child " +
                           "processes) because the user pressed STOP. It did NOT finish. Check the current state " +
                           "before you repeat it." +
                           (partial.Length > 0 ? $"\nOutput before the kill:\n{partial}" : "");
                }
                exitCode = timedOut ? -1 : proc.ExitCode;
                return null;
            }
            catch (Exception ex)
            {
                return $"Error: Failed to launch 'dotnet {string.Join(' ', args)}': {ex.Message}\n" +
                        "Make sure the .NET SDK is installed and 'dotnet' is in PATH.";
            }
        }

        public static string FormatBuildResult(
            string projectPath, string buildConfig,
            int exitCode, string stdout, string stderr, bool timedOut)
        {
            if (timedOut)
            {
                var timeoutSb = new StringBuilder();
                timeoutSb.AppendLine("Build TIMED OUT — process was killed.");
                timeoutSb.AppendLine($"  Config  : {buildConfig}");
                timeoutSb.AppendLine($"  Project : {projectPath}");
                return timeoutSb.ToString().TrimEnd();
            }

            string combined = string.IsNullOrWhiteSpace(stderr)
                ? stdout
                : stdout + "\n" + stderr;

            string[] lines = combined.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (exitCode == 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Build succeeded.");
                sb.AppendLine($"  Config  : {buildConfig}");
                sb.AppendLine($"  Project : {projectPath}");

                foreach (string line in lines)
                {
                    string t = line.Trim();
                    if (t.Contains(" -> "))
                        sb.AppendLine($"  Artifact: {t}");
                }

                foreach (string line in lines)
                {
                    string t = line.Trim();
                    if (t.EndsWith("Warning(s)"))
                    {
                        sb.AppendLine($"  {t}");
                        break;
                    }
                }

                return sb.ToString().TrimEnd();
            }

            var failSb = new StringBuilder();
            failSb.AppendLine("Build FAILED.");
            failSb.AppendLine($"  Config  : {buildConfig}");
            failSb.AppendLine($"  Project : {projectPath}");
            failSb.AppendLine();

            var errorLines = new List<string>();
            foreach (string line in lines)
            {
                string t = line.Trim();
                if (t.Contains(": error ")                                       ||
                    t.Contains(": Error ")                                       ||
                    t.StartsWith("error ",   StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("MSBUILD : error", StringComparison.OrdinalIgnoreCase))
                {
                    errorLines.Add(t);
                }
            }

            var warnLines = new List<string>();
            foreach (string line in lines)
            {
                string t = line.Trim();
                if (t.Contains(": warning "))
                    warnLines.Add(t);
            }

            if (errorLines.Count > 0)
            {
                failSb.AppendLine($"Errors ({errorLines.Count}):");
                foreach (string e in errorLines)
                    failSb.AppendLine($"  {e}");
            }

            if (warnLines.Count > 0)
            {
                failSb.AppendLine();
                failSb.AppendLine($"Warnings ({warnLines.Count}):");
                foreach (string w in warnLines)
                    failSb.AppendLine($"  {w}");
            }

            if (errorLines.Count == 0 && warnLines.Count == 0)
            {
                failSb.AppendLine("Raw build output:");
                failSb.AppendLine(combined.Trim());
            }

            return failSb.ToString().TrimEnd();
        }

        public static string FormatRunResult(
            string projectPath, string buildConfig,
            int exitCode, string stdout, string stderr, bool timedOut)
        {
            var sb = new StringBuilder();

            if (timedOut)
            {
                sb.AppendLine("Run TIMED OUT — process was killed.");
                sb.AppendLine($"  Config  : {buildConfig}");
                sb.AppendLine($"  Project : {projectPath}");
                sb.AppendLine();
                sb.AppendLine("Partial output captured before the process was killed:");
            }
            else
            {
                sb.AppendLine(exitCode == 0
                    ? "Run finished successfully (exit code 0)."
                    : $"Run finished with a non-zero exit code ({exitCode}).");
                sb.AppendLine($"  Config  : {buildConfig}");
                sb.AppendLine($"  Project : {projectPath}");
            }

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                sb.AppendLine();
                sb.AppendLine("STDOUT:");
                sb.AppendLine(stdout.Trim());
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                sb.AppendLine();
                sb.AppendLine("STDERR:");
                sb.AppendLine(stderr.Trim());
            }

            return sb.ToString().TrimEnd();
        }
    }
}
#endif
