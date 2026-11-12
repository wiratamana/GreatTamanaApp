#if !CO_WORKER_USE
using System;
using System.Collections.Concurrent;
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
        private const int DefaultTimeoutSeconds = 300;   // same default as run_shell; there is no maximum

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
                "Output is streamed live to the chat window while the code runs. The final result is " +
                "everything printed (nothing is shown unless the code prints it). Only the Python " +
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
                         "processes). Defaults to 300 (5 minutes), same as run_shell. No maximum. Use a " +
                         "larger value for long builds started from Python.",
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

                // Same as run_shell: default 5 minutes, NO upper cap.
                int timeoutSeconds = GetInt(input, "timeout_seconds") ?? DefaultTimeoutSeconds;
                if (timeoutSeconds <= 0)
                    return $"Error: 'timeout_seconds' must be a positive number — got {timeoutSeconds}.";

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
                // THE one global proxy method. Disposed (in the finally below) after python has ended.
                using IDisposable proxyScope = GTA.Core.ProxyBridge.ApplyToStartInfo(psi);

                try
                {
                    using Process proc = Process.Start(psi)!;

                    // Both pipes are read in the background (chunks, so a line without '\n' is not lost).
                    // Everything is kept for the final result. The same chunks are queued for the LIVE view.
                    // The queue is drained on THIS thread, because ToolRegistry.ReportProgress is tied to
                    // the tool's own call-flow (same as run_shell / cmake / gcc).
                    var stdoutSb  = new StringBuilder();
                    var stderrSb  = new StringBuilder();
                    var liveQueue = new ConcurrentQueue<(bool IsErr, string Text)>();
                    Task outTask  = PumpPipe(proc.StandardOutput, stdoutSb, false, liveQueue);
                    Task errTask  = PumpPipe(proc.StandardError,  stderrSb, true,  liveQueue);

                    var liveOut = new LiveLineEmitter("");
                    var liveErr = new LiveLineEmitter("[stderr] ");
                    void Drain(bool final)
                    {
                        while (liveQueue.TryDequeue(out var item))
                            (item.IsErr ? liveErr : liveOut).Add(item.Text);
                        liveOut.Emit(final);
                        liveErr.Emit(final);
                    }

                    if (UseStdin)
                    {
                        try
                        {
                            proc.StandardInput.Write(code);
                            proc.StandardInput.Close();
                        }
                        catch (IOException) { /* python exited before reading all the code */ }
                    }

                    long timeoutMs = (long)timeoutSeconds * 1000;
                    var waitClock = Stopwatch.StartNew();
                    var stopToken = GTA.Core.ToolCancellation.Token;   // the user's STOP button
                    bool exited;
                    bool cancelled = false;
                    while (true)
                    {
                        exited = proc.WaitForExit(250);
                        Drain(false);
                        if (exited || waitClock.ElapsedMilliseconds >= timeoutMs) break;
                        if (stopToken.IsCancellationRequested) { cancelled = true; break; }
                    }

                    string timeoutNote = "";
                    if (!exited)
                    {
                        timeoutNote = cancelled
                            ? "\n\n⏹ STOPPED BY USER: python was killed (with its child processes) because the user pressed STOP. " +
                              "The output above is only what was printed before the kill. The code did NOT finish."
                            : $"\n\n⚠ TIMEOUT: python did not finish within {timeoutSeconds}s and was " +
                              "force-killed (with its child processes). Output below is what was printed " +
                              "before the kill. Pass a larger 'timeout_seconds' if the code is just slow.";
                        GTA.Core.ToolCancellation.KillTree(proc);
                    }

                    // A grandchild process can keep a pipe open; do not wait for it forever.
                    try { Task.WaitAll(new Task[] { outTask, errTask }, 3000); } catch { /* handled below */ }
                    Drain(true);

                    string stdout, stderr;
                    lock (stdoutSb) stdout = stdoutSb.ToString();
                    lock (stderrSb) stderr = stderrSb.ToString();
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

        /// <summary>Read a pipe to the end in the background. Every chunk goes to <paramref name="all"/> (final result) and to <paramref name="live"/> (live view).</summary>
        private static Task PumpPipe(
            StreamReader reader, StringBuilder all, bool isErr,
            ConcurrentQueue<(bool IsErr, string Text)> live)
        {
            return Task.Run(async () =>
            {
                var buf = new char[4096];
                try
                {
                    int n;
                    while ((n = await reader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                    {
                        string s = new string(buf, 0, n);
                        lock (all) all.Append(s);
                        live.Enqueue((isErr, s));
                    }
                }
                catch { /* pipe closed (process killed): the end of the output */ }
            });
        }

        /// <summary>
        /// Collects raw chunks and reports COMPLETE lines through ToolRegistry.ReportProgress (the live view
        /// in the chat window). Same rules as the live tail of run_shell: a partial line waits for its newline
        /// (unless it is very long or this is the final flush), and one message is capped to 8000 chars.
        /// Only used from the tool's own thread.
        /// </summary>
        private sealed class LiveLineEmitter
        {
            private const int MaxMessageChars = 8000;
            private readonly string _prefix;
            private readonly StringBuilder _pending = new();

            public LiveLineEmitter(string prefix) { _prefix = prefix; }

            public void Add(string chunk) => _pending.Append(chunk);

            public void Emit(bool final)
            {
                if (_pending.Length == 0) return;

                string text = _pending.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
                int lastNl  = text.LastIndexOf('\n');

                string complete;
                if (lastNl >= 0)
                {
                    complete = text.Substring(0, lastNl);
                    _pending.Clear();
                    _pending.Append(text, lastNl + 1, text.Length - lastNl - 1);
                }
                else if (final || _pending.Length > 2000)
                {
                    complete = text;   // very long line, or the very end of the output
                    _pending.Clear();
                }
                else
                {
                    return;            // wait for the rest of the line
                }

                if (final && _pending.Length > 0)
                {
                    complete += "\n" + _pending;
                    _pending.Clear();
                }
                if (complete.Length == 0) return;

                if (complete.Length > MaxMessageChars)
                    complete = "…" + complete.Substring(complete.Length - MaxMessageChars);

                if (_prefix.Length > 0)
                {
                    string[] parts = complete.Split('\n');
                    for (int i = 0; i < parts.Length; i++) parts[i] = _prefix + parts[i];
                    complete = string.Join("\n", parts);
                }

                try { ToolRegistry.ReportProgress(complete); }
                catch { /* live display only — never break the tool */ }
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
