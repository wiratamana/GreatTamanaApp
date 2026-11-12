#if !CO_WORKER_USE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static class ToolsExternalProcess
    {
        // ── run_app_background ────────────────────────────────────────────────

        [RecordToLog]
        [ToolTag("external", "process")]
        [ToolInfo(
            name: "run_app_background",
            description: "Launch a local Windows executable (or any file with a registered default " +
                         "app) in the BACKGROUND — detached and NON-BLOCKING — and return immediately " +
                         "with its process ID, WITHOUT waiting for it to exit. Equivalent in spirit to " +
                         "running 'start \"\" \"<path>\"' from cmd.exe, but implemented via a direct " +
                         "process launch so the exact spawned process's PID is captured reliably (a " +
                         "plain cmd 'start' launcher exits immediately and does not reliably expose the " +
                         "PID of what it actually spawned). Use this instead of run_shell whenever you " +
                         "need to start a long-running or GUI app (a game, a local server, any program " +
                         "that keeps running) and then keep doing OTHER things while it stays up — e.g. " +
                         "sending it network requests, taking a screenshot of its window, or polling a " +
                         "health-check endpoint — without your tool call blocking forever waiting for " +
                         "that app to close on its own. When you're done with it, terminate it with " +
                         "stop_app_background, using the PID this tool returned (or its image_name). " +
                         "HARD-BLOCKED (the app is NOT started, an error comes back): anything inside the Windows " +
                         "system folder (C:\\Windows, System32, ...), shells and script hosts (cmd.exe, powershell.exe, " +
                         "pwsh.exe, bash.exe, wscript.exe, mshta.exe, rundll32.exe ...) wherever they are stored, and " +
                         "script/installer/shortcut files (.bat .cmd .ps1 .vbs .js .msi .lnk ...), and Python " +
                         "(python.exe, pythonw.exe, py.exe, .py files) — to run Python use inline_python, or run it " +
                         "manually with run_shell. To run a command or build, use run_shell, cmake or gcc instead.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the executable (or other launchable file) to " +
                         "run, e.g. C:\\...\\GreatTamanaEngine.exe.",
            required: true)]
        [Parameter(index: 1, name: "arguments",
            type: "string",
            description: "Optional command-line arguments to pass to the executable, exactly as they'd " +
                         "be typed after the exe name (e.g. \"--fullscreen --level 3\").",
            required: false)]
        [Parameter(index: 2, name: "working_directory",
            type: "string",
            description: "Optional working directory for the launched process. Defaults to the folder " +
                         "containing 'path'.",
            required: false)]
        public static object RunAppBackground(JsonObject input)
        {
            string? rawPath = input["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rawPath))
                return "Error: 'path' parameter is missing or empty.";

            string path = rawPath.Trim().Trim('"');

            // Special case: Python interpreters get their own, more helpful message.
            string? pythonBlocked = FindPythonLaunch(path);
            if (pythonBlocked != null)
                return pythonBlocked;

            // Hard wall: never launch system programs, shells, script hosts or script files.
            string? blockedReason = FindForbiddenLaunch(path, input["arguments"]?.GetValue<string>());
            if (blockedReason != null)
                return blockedReason;

            if (!File.Exists(path))
            {
                if (Directory.Exists(path))
                    return $"Error: '{path}' is a directory, not an executable/launchable file.";
                return $"Error: File not found at '{path}'. Provide the full absolute path to the " +
                       "executable (or other launchable file) to run.";
            }

            string? arguments = input["arguments"]?.GetValue<string>();
            string? rawWorkDir = input["working_directory"]?.GetValue<string>();
            string? workingDirectory = string.IsNullOrWhiteSpace(rawWorkDir) ? null : rawWorkDir.Trim().Trim('"');

            if (workingDirectory != null && !Directory.Exists(workingDirectory))
                return $"Error: working_directory '{workingDirectory}' does not exist.";

            try
            {
                var psi = new ProcessStartInfo(path)
                {
                    UseShellExecute  = true,
                    WorkingDirectory = workingDirectory ?? (Path.GetDirectoryName(path) ?? string.Empty)
                };

                if (!string.IsNullOrWhiteSpace(arguments))
                    psi.Arguments = arguments;

                Process? proc = Process.Start(psi);
                if (proc == null)
                    return $"Error: Process.Start returned null for '{path}' — the process may not " +
                           "have actually launched.";

                int    pid       = proc.Id;
                string imageName = Path.GetFileName(path);

                var sb = new StringBuilder();
                sb.Append($"Launched successfully in the background (non-blocking): '{path}'");
                if (!string.IsNullOrWhiteSpace(arguments)) sb.Append($" {arguments}");
                sb.AppendLine();
                sb.AppendLine($"Process ID (PID): {pid}");
                sb.AppendLine($"Image name: {imageName}");
                sb.Append("The app is now running independently of this tool call — use " +
                           $"stop_app_background(pid: {pid}) (or image_name: \"{imageName}\") to close it later.");
                return sb.ToString();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return $"Error: Failed to launch '{path}': {ex.Message} (Win32 error code {ex.NativeErrorCode}).";
            }
            catch (Exception ex)
            {
                return $"Error: Failed to launch '{path}': {ex.Message}";
            }
        }

        // ── launch guard ──────────────────────────────────────────────────────
        // Python interpreters / launchers: python.exe, pythonw.exe, python3.exe, python3.12.exe, py.exe, pyw.exe.
        // Blocked by FILE NAME (also scoop shims, venv copies, junction targets). Python scripts (.py/.pyw)
        // are blocked too, because Windows would open them with the registered Python interpreter.
        private static readonly System.Text.RegularExpressions.Regex PythonFileNameRegex = new(
            @"^(pythonw?(\d+(\.\d+)*)?|pyw?)\.exe$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static readonly HashSet<string> PythonScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".py", ".pyw", ".pyz"
        };

        /// <summary>Returns the "Python not allowed" error text if the target is a Python interpreter or script, otherwise null.</summary>
        private static string? FindPythonLaunch(string path)
        {
            var candidates = new List<string> { path };
            try
            {
                string full = Path.GetFullPath(path);
                candidates.Add(full);
                string? canonical = GetCanonicalPath(full);
                if (canonical != null) candidates.Add(canonical);
            }
            catch (Exception)
            {
                // Invalid path: the generic guard / File.Exists check will report it.
            }

            foreach (string candidate in candidates)
            {
                string fileName = Path.GetFileName(candidate);
                string ext      = Path.GetExtension(candidate);
                if (PythonFileNameRegex.IsMatch(fileName) || PythonScriptExtensions.Contains(ext))
                    return BuildPythonBlockedMessage(path);
            }

            return null;
        }

        private static string BuildPythonBlockedMessage(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Error: run_app_background does NOT run Python (python.exe / pythonw.exe / py.exe / .py files). The app was NOT started.");
            sb.AppendLine($"Target: {path}");
            sb.AppendLine("This tool is only for your own app, game or local server (for example GreatTamanaEngine.exe).");
            sb.AppendLine("Do NOT retry with another path, a copy or a renamed file — that is also blocked on purpose.");
            sb.AppendLine("Instead, use ONE of these:");
            sb.AppendLine("- inline_python: run a Python snippet given as a string (best for short scripts and file edits).");
            sb.Append("- run_shell: run Python manually, for example: python \"C:\\path\\to\\script.py\" args");

            try
            {
                GTA.Core.SessionFileLogger.Log("TOOL_CALL",
                    $"run_app_background BLOCKED (python): '{path}'");
            }
            catch { /* logging must never break the tool */ }

            return sb.ToString();
        }


        // Shells, script hosts and "living off the land" programs. Blocked by FILE NAME, so a copy of
        // them outside C:\Windows (e.g. Git's bash.exe, PowerShell 7 pwsh.exe) is blocked too.
        private static readonly HashSet<string> BlockedFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "cmd.exe", "powershell.exe", "powershell_ise.exe", "pwsh.exe", "conhost.exe",
            "bash.exe", "sh.exe", "zsh.exe", "wsl.exe", "wslhost.exe",
            "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe",
            "regsvcs.exe", "regasm.exe", "installutil.exe", "msiexec.exe", "msbuild.exe",
            "certutil.exe", "bitsadmin.exe", "wmic.exe", "forfiles.exe", "pcalua.exe",
            "reg.exe", "regedit.exe", "sc.exe", "schtasks.exe", "taskkill.exe", "net.exe", "net1.exe",
            "netsh.exe", "takeown.exe", "icacls.exe", "cacls.exe", "runas.exe", "psexec.exe", "psexec64.exe",
            "curl.exe", "wget.exe", "ftp.exe", "tftp.exe", "ssh.exe", "scp.exe", "telnet.exe"
        };

        // Script / installer / shortcut files that Windows would hand to a shell or script host.
        private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
            ".hta", ".msi", ".msp", ".reg", ".lnk", ".url", ".scr", ".com", ".pif", ".cpl", ".inf", ".gadget"
        };

        /// <summary>Returns the "BLOCKED" error text if this launch is not allowed, otherwise null.</summary>
        private static string? FindForbiddenLaunch(string path, string? arguments)
        {
            var reasons = new List<string>();

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return BuildBlockedMessage(path, new List<string> { "the path is not a valid Windows path." });
            }

            // Check the path as typed AND the real target (resolves symlinks, junctions, 8.3 short names).
            var candidates = new List<string> { full };
            string? canonical = GetCanonicalPath(full);
            if (canonical != null && !canonical.Equals(full, StringComparison.OrdinalIgnoreCase))
                candidates.Add(canonical);

            var windowsDirs = new List<string>();
            foreach (string? d in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                Environment.GetEnvironmentVariable("SystemRoot"),
                Environment.GetEnvironmentVariable("windir"),
            })
            {
                if (!string.IsNullOrWhiteSpace(d))
                    windowsDirs.Add(d.TrimEnd('\\', '/'));
            }

            foreach (string candidate in candidates)
            {
                string fileName = Path.GetFileName(candidate);
                string ext      = Path.GetExtension(candidate);

                foreach (string dir in windowsDirs)
                {
                    if (candidate.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        reasons.Add($"'{candidate}' is inside the Windows system folder ({dir}). " +
                                    "Programs of the operating system are never allowed to be launched by this tool.");
                        break;
                    }
                }

                if (BlockedFileNames.Contains(fileName))
                    reasons.Add($"'{fileName}' is a shell / script host / system-admin program. " +
                                "It can run any command, so it is not allowed (this is a bypass of run_shell's checks).");

                if (BlockedExtensions.Contains(ext))
                    reasons.Add($"'{ext}' files are scripts, installers or shortcuts that Windows runs through a " +
                                "shell or script host. They are not allowed.");
            }

            // Same PowerShell / web-request rules as run_shell, applied to the arguments.
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                string? psHit = ToolsExternal.CheckPowerShellText(arguments, null, "run_app_background");
                if (psHit != null)
                    reasons.Add("the arguments contain a forbidden pattern: " + psHit);
            }

            if (reasons.Count == 0) return null;

            return BuildBlockedMessage(path, reasons.Distinct().ToList());
        }

        private static string BuildBlockedMessage(string path, List<string> reasons)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Error: run_app_background BLOCKED this launch. The app was NOT started. This is NOT ALLOWED.");
            sb.AppendLine($"Target: {path}");
            foreach (string r in reasons)
                sb.AppendLine($"- Not allowed: {r}");
            sb.AppendLine("This tool is only for your own app, game or local server (for example GreatTamanaEngine.exe). " +
                          "Do NOT retry with another path, a copy or a renamed file — that is also blocked on purpose.");
            sb.Append("Instead: to run a command or a build, use run_shell (or the cmake / gcc tools). " +
                      "To run a Python script, use inline_python or run_shell.");

            try
            {
                GTA.Core.SessionFileLogger.Log("TOOL_CALL",
                    $"run_app_background BLOCKED: '{path}' ({reasons.Count} reason(s))");
            }
            catch { /* logging must never break the tool */ }

            return sb.ToString();
        }

        /// <summary>Real final path of an existing file (follows symlinks / junctions, expands 8.3 names). Null if unavailable.</summary>
        private static string? GetCanonicalPath(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                var sb = new StringBuilder(1024);
                uint len = GetFinalPathNameByHandleW(fs.SafeFileHandle, sb, (uint)sb.Capacity, 0);
                if (len == 0 || len >= sb.Capacity) return null;

                string result = sb.ToString();
                if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    return @"\\" + result.Substring(8);
                if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
                    return result.Substring(4);
                return result;
            }
            catch
            {
                return null;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        // ── stop_app_background ───────────────────────────────────────────────

        [RecordToLog]
        [ToolTag("external", "process")]
        [ToolInfo(
            name: "stop_app_background",
            description: "Forcefully terminate a process previously launched via run_app_background " +
                         "(or any other running process on this machine), so an app left running in " +
                         "the background can be closed. Identify the target EITHER by 'pid' (the exact " +
                         "process ID returned by run_app_background — the most reliable option, always " +
                         "prefer this when you have it) OR by 'image_name' (the .exe file name, e.g. " +
                         "\"GreatTamanaEngine.exe\" — terminates ALL currently-running processes with " +
                         "that image name, so only use this when you don't have the PID or intentionally " +
                         "want to close every instance). Kills the entire process tree of each match.")]
        [Parameter(index: 0, name: "pid",
            type: "integer",
            description: "Process ID (PID) to terminate, exactly as returned by run_app_background. " +
                         "Preferred over image_name when known.",
            required: false)]
        [Parameter(index: 1, name: "image_name",
            type: "string",
            description: "Executable file name to terminate ALL currently-running matching processes " +
                         "of, e.g. \"GreatTamanaEngine.exe\". Only used if 'pid' is not supplied.",
            required: false)]
        public static object StopAppBackground(JsonObject input)
        {
            int?    pid       = ToolsIOHelpers.GetOptionalInt(input["pid"]);
            string? imageName = input["image_name"]?.GetValue<string>()?.Trim().Trim('"');

            if (pid == null && string.IsNullOrWhiteSpace(imageName))
                return "Error: Provide either 'pid' or 'image_name' to identify which process to stop.";

            if (pid != null)
                return StopByPid(pid.Value);

            return StopByImageName(imageName!);
        }

        private static string StopByPid(int pid)
        {
            Process proc;
            try
            {
                proc = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return $"Error: No running process found with PID {pid} — it may have already exited.";
            }
            catch (Exception ex)
            {
                return $"Error: Failed to look up process PID {pid}: {ex.Message}";
            }

            using (proc)
            {
                string name = SafeGetProcessName(proc);
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                    return $"Stopped process PID {pid} ({name}) successfully.";
                }
                catch (InvalidOperationException)
                {
                    // Already exited between the lookup and the Kill call.
                    return $"Process PID {pid} ({name}) was already exited — nothing to stop.";
                }
                catch (Exception ex)
                {
                    return $"Error: Failed to stop process PID {pid} ({name}): {ex.Message}";
                }
            }
        }

        private static string StopByImageName(string imageName)
        {
            string nameNoExt = Path.GetFileNameWithoutExtension(imageName);

            Process[] matches;
            try
            {
                matches = Process.GetProcessesByName(nameNoExt);
            }
            catch (Exception ex)
            {
                return $"Error: Failed to look up processes named '{imageName}': {ex.Message}";
            }

            if (matches.Length == 0)
                return $"No running process found with image name '{imageName}'.";

            var stopped = new List<string>();
            var failed  = new List<string>();

            foreach (Process proc in matches)
            {
                using (proc)
                {
                    int matchedPid = proc.Id;
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(5000);
                        stopped.Add(matchedPid.ToString());
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"PID {matchedPid}: {ex.Message}");
                    }
                }
            }

            var sb = new StringBuilder();
            if (stopped.Count > 0)
                sb.AppendLine($"Stopped {stopped.Count} process(es) matching '{imageName}': PID(s) {string.Join(", ", stopped)}.");
            if (failed.Count > 0)
                sb.AppendLine($"Failed to stop {failed.Count} process(es): {string.Join("; ", failed)}");

            return sb.ToString().TrimEnd();
        }

        private static string SafeGetProcessName(Process proc)
        {
            try { return proc.ProcessName; } catch { return "(unknown)"; }
        }
    }
}
#endif
