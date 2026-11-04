#if !CO_WORKER_USE
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace GTA.Tools
{
    internal static class ToolsExternalPython
    {
        private const int DefaultTimeoutSeconds = 60;
        private const int MaxTimeoutSeconds     = 900;

        // true = send the code over stdin, false = temp .py file. See the header comment.
        private static readonly bool UseStdin = true;

        [RecordToLog]
        [ToolTag("external", "python")]
        [ToolInfo(
            name: "inline_python",
            description:
                "Run a Python snippet given directly as a string and return its output (stdout, and stderr " +
                "if there is any). No file needs to be written first. Use it for JSON parsing, math, text " +
                "processing, reading/writing files, and for HTTP calls from Python (urllib / http.client — " +
                "loopback calls to the editor at 127.0.0.1 work, the corporate proxy is bypassed for " +
                "loopback). For simple calls to the editor's HTTP server prefer http_request (it starts no " +
                "process at all). Typical combination: http_request with save_to_file, then inline_python " +
                "to parse that file. The code runs as 'python -u -' (code sent on stdin), with UTF-8 stdio. " +
                "Print what you want to see — nothing is shown unless the code prints it. Only the Python " +
                "standard library is guaranteed (numpy etc. may be missing). The same PowerShell hard wall " +
                "as run_shell applies to the code text (no -EncodedCommand, -WindowStyle Hidden, " +
                "-ExecutionPolicy Bypass, Invoke-RestMethod/Invoke-WebRequest/WebClient). A non-zero exit " +
                "code is reported as '[exit code N]'. Output larger than ~200000 tokens is cut to head+tail " +
                "and the full text is saved to a temp file.")]
        [Parameter(index: 0, name: "code",
            type: "string",
            description: "The Python source code to run (can be multi-line).",
            required: true)]
        [Parameter(index: 1, name: "timeout_seconds",
            type: "integer",
            description: "Optional. Max seconds to wait before force-killing python (and its child " +
                         "processes). Default 60, maximum 900.",
            required: false)]
        [Parameter(index: 2, name: "working_directory",
            type: "string",
            description: "Optional working directory for the script (relative file paths in the code " +
                         "resolve against it). Defaults to the user's temp folder.",
            required: false)]
        [Parameter(index: 3, name: "python_path",
            type: "string",
            description: "Optional full path to python.exe. Defaults to 'python' (found through PATH).",
            required: false)]
        public static object InlinePython(JsonObject input)
        {
            try
            {
                string? code = GetString(input, "code");
                if (string.IsNullOrWhiteSpace(code))
                    return "Error: 'code' is missing or empty.";

                // Same hard wall as run_shell (Python could otherwise sneak the patterns in).
                string? blocked = ToolsExternal.CheckPowerShellText(code, null, "inline_python");
                if (blocked != null)
                    return blocked;

                int timeoutSeconds = GetInt(input, "timeout_seconds") ?? DefaultTimeoutSeconds;
                if (timeoutSeconds <= 0)
                    return $"Error: 'timeout_seconds' must be a positive number — got {timeoutSeconds}.";
                if (timeoutSeconds > MaxTimeoutSeconds) timeoutSeconds = MaxTimeoutSeconds;

                string? rawWorkDir = GetString(input, "working_directory");
                string workDir = string.IsNullOrWhiteSpace(rawWorkDir)
                    ? Path.GetTempPath()
                    : rawWorkDir.Trim().Trim('"');
                if (!Directory.Exists(workDir))
                    return $"Error: working_directory '{workDir}' does not exist.";

                string? rawPython = GetString(input, "python_path");
                string pythonExe = string.IsNullOrWhiteSpace(rawPython) ? "python" : rawPython.Trim().Trim('"');

                var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                var psi = new ProcessStartInfo
                {
                    FileName               = pythonExe,
                    WorkingDirectory       = workDir,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    RedirectStandardInput  = UseStdin,
                    StandardOutputEncoding = utf8,
                    StandardErrorEncoding  = utf8,
                };
                if (UseStdin) psi.StandardInputEncoding = utf8;

                psi.ArgumentList.Add("-u");   // unbuffered: output is not lost if python is killed

                string? scriptPath = null;
                if (UseStdin)
                {
                    psi.ArgumentList.Add("-");
                }
                else
                {
                    string dir = Path.Combine(Path.GetTempPath(), "gta_inline_py");
                    Directory.CreateDirectory(dir);
                    scriptPath = Path.Combine(dir, $"inline_{Guid.NewGuid():N}.py");
                    File.WriteAllText(scriptPath, code, utf8);
                    psi.ArgumentList.Add(scriptPath);
                }

                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                psi.EnvironmentVariables["PYTHONUTF8"]       = "1";
                foreach (var kv in ToolsExternal.BuildProxyEnvironmentVariables())
                    psi.EnvironmentVariables[kv.Key] = kv.Value;

                try
                {
                    using Process proc = Process.Start(psi)!;

                    // Read both pipes while we write stdin, so a chatty script cannot deadlock.
                    Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
                    Task<string> errTask = proc.StandardError.ReadToEndAsync();

                    if (UseStdin)
                    {
                        try
                        {
                            proc.StandardInput.Write(code);
                            proc.StandardInput.Close();
                        }
                        catch (IOException) { /* python exited before reading all the code */ }
                    }

                    bool exited = proc.WaitForExit(timeoutSeconds * 1000);
                    string timeoutNote = "";
                    if (!exited)
                    {
                        timeoutNote = $"\n\n⚠ TIMEOUT: python did not finish within {timeoutSeconds}s and was " +
                                      "force-killed (with its child processes). Output below is what was printed " +
                                      "before the kill. Pass a larger 'timeout_seconds' if the code is just slow.";
                        try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                        try { proc.WaitForExit(5000); } catch { /* best effort */ }
                    }

                    // A grandchild process can keep a pipe open; do not wait for it forever.
                    try { Task.WaitAll(new Task[] { outTask, errTask }, 3000); } catch { /* handled below */ }
                    string stdout = outTask.IsCompletedSuccessfully ? outTask.Result : "";
                    string stderr = errTask.IsCompletedSuccessfully ? errTask.Result : "";
                    string pipeNote = (outTask.IsCompleted && errTask.IsCompleted) ? "" :
                        "\n\n(note: a child process still holds the output pipe, so the output may be incomplete)";

                    string result = string.IsNullOrWhiteSpace(stderr)
                        ? stdout
                        : $"STDOUT:\n{stdout}\nSTDERR:\n{stderr}";

                    if (exited && proc.ExitCode != 0)
                        result += $"\n[exit code {proc.ExitCode}]";
                    result += pipeNote + timeoutNote;

                    if (string.IsNullOrWhiteSpace(result))
                        result = "(no output, exit code 0 — the code printed nothing; use print() to see values)";

                    return ToolsExternal.ApplyOutputTokenBudget(result);
                }
                catch (Win32Exception ex)
                {
                    return $"Error: could not start '{pythonExe}': {ex.Message} (Win32 error {ex.NativeErrorCode}). " +
                           "If python is not on PATH, pass 'python_path' with the full path to python.exe. " +
                           "If the error is 'Access denied', endpoint security may be blocking the launch.";
                }
                finally
                {
                    if (scriptPath != null)
                    {
                        try { File.Delete(scriptPath); } catch { /* best effort */ }
                    }
                }
            }
            catch (Exception ex)
            {
                return $"Error: inline_python failed unexpectedly: {ex.GetType().FullName}: {ex.Message}";
            }
        }

        private static string? GetString(JsonObject input, string key)
        {
            try
            {
                JsonNode? node = input[key];
                if (node is null) return null;
                if (node is JsonValue v && v.TryGetValue(out string? s)) return s;
                return node.ToJsonString();
            }
            catch
            {
                return null;
            }
        }

        private static int? GetInt(JsonObject input, string key)
        {
            try
            {
                JsonNode? node = input[key];
                if (node is JsonValue v)
                {
                    if (v.TryGetValue(out int i)) return i;
                    if (v.TryGetValue(out string? s) && int.TryParse(s, out int parsed)) return parsed;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
#endif
