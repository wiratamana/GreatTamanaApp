#if !CO_WORKER_USE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using GTA.Core;

namespace GTA.Tools.Impl.External
{
    /// <summary>Result of <see cref="ExternalProcessUtils.RunCmdCaptured"/>.</summary>
    public sealed record CapturedCmdResult(bool Exited, int ExitCode, string Stdout, string Stderr, bool Cancelled = false);

    /// <summary>
    /// The ONE place that runs a command through cmd.exe, captures stdout/stderr and streams it live.
    /// Used by run_shell, gcc and cmake so they all behave the same (live output, timeout kill, encoding).
    /// It also applies the corporate-proxy setup (ProxyBridge.ApplyToStartInfo) in ONE place, so every caller
    /// gets it automatically. Pass useProxy: false only for purely offline tools (gcc).
    /// </summary>
    public static class ExternalProcessUtils
    {
        /// <summary>Default timeout for a single command (5 minutes) — same default as run_shell.</summary>
        public const int DEFAULT_TIMEOUT_MS = 300_000;

        /// <summary>
        /// Run '&lt;exeName&gt; &lt;arguments&gt;' via cmd.exe and return the formatted text.
        /// Output is streamed live through <paramref name="onProgress"/> (if given).
        /// </summary>
        public static string RunCommand(
            string exeName,
            string arguments,
            string? workingDirectory,
            int timeoutMs,
            IDictionary<string, string>? extraEnvironmentVariables = null,
            Action<string>? onProgress = null,
            bool useProxy = true)
        {
            string innerCmd = string.IsNullOrWhiteSpace(arguments) ? exeName : $"{exeName} {arguments}";

            try
            {
                CapturedCmdResult r = RunCmdCaptured(
                    innerCmd, exeName, workingDirectory, timeoutMs, extraEnvironmentVariables, onProgress, useProxy);

                string timeoutNote = "";
                if (r.Cancelled)
                {
                    timeoutNote = StoppedByUserNote(exeName);
                }
                else if (!r.Exited)
                {
                    timeoutNote =
                        $"\n\n⚠ TIMEOUT: '{exeName}' did not finish within {timeoutMs / 1000}s and was " +
                        "force-killed (including any child/grandchild processes). Output below is " +
                        "whatever was captured before the kill. Pass a larger timeout_seconds if this " +
                        "command is just slow (e.g. a big compile/clone), not stuck.";
                }

                string result = string.IsNullOrWhiteSpace(r.Stderr)
                    ? r.Stdout
                    : $"STDOUT:\n{r.Stdout}\nSTDERR:\n{r.Stderr}";

                return result + (r.Exited ? FormatExitNote(r.ExitCode) : "") + timeoutNote;
            }
            catch (Exception ex)
            {
                return $"Error: Failed to launch '{exeName}': {ex.Message}\n" +
                       $"Make sure '{exeName}' is installed and available on PATH.";
            }
        }

        /// <summary>
        /// Core runner: 'cmd.exe /d /q /c chcp 65001&gt;nul &amp;&amp; &lt;command&gt;' with stdout/stderr redirected
        /// to temp files. The files are tailed while the command runs and every new line is sent to
        /// <paramref name="onProgress"/> (stderr lines get the "[stderr] " prefix). On timeout the whole
        /// process tree is killed. Throws if the process cannot be started.
        /// </summary>
        public static CapturedCmdResult RunCmdCaptured(
            string command,
            string tempPrefix,
            string? workingDirectory,
            long timeoutMs,
            IDictionary<string, string>? extraEnvironmentVariables = null,
            Action<string>? onProgress = null,
            bool useProxy = true)
        {
            string fullCmd = $"/d /q /c chcp 65001>nul && {command}";

            string safePrefix = string.Concat(tempPrefix.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            string tempOut = Path.Combine(Path.GetTempPath(), $"{safePrefix}_out_{Guid.NewGuid():N}.txt");
            string tempErr = Path.Combine(Path.GetTempPath(), $"{safePrefix}_err_{Guid.NewGuid():N}.txt");

            try
            {
                string redirectedCmd = $"{fullCmd} > \"{tempOut}\" 2> \"{tempErr}\"";

                var psi = new ProcessStartInfo("cmd.exe", redirectedCmd)
                {
                    RedirectStandardOutput = false,
                    RedirectStandardError  = false,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };

                if (!string.IsNullOrWhiteSpace(workingDirectory))
                    psi.WorkingDirectory = workingDirectory;

                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                psi.EnvironmentVariables["PYTHONUTF8"]       = "1";

                // THE one global proxy method (HTTP(S)_PROXY + tool adapters such as Scoop).
                // Disposed when this method ends = after the command has finished.
                using IDisposable? proxyScope = useProxy
                    ? ProxyBridge.ApplyToStartInfo(psi, command)
                    : null;

                if (extraEnvironmentVariables != null)
                {
                    foreach (var kv in extraEnvironmentVariables)
                        psi.EnvironmentVariables[kv.Key] = kv.Value;
                }

                using Process proc = Process.Start(psi)!;

                var liveOut = new LiveFileTail(tempOut, "", onProgress);
                var liveErr = new LiveFileTail(tempErr, "[stderr] ", onProgress);
                var waitClock = Stopwatch.StartNew();
                CancellationToken stopToken = ToolCancellation.Token;   // the user's STOP button
                bool exited;
                bool cancelled = false;
                while (true)
                {
                    exited = proc.WaitForExit(250);
                    liveOut.Pump();
                    liveErr.Pump();
                    if (exited || waitClock.ElapsedMilliseconds >= timeoutMs) break;
                    if (stopToken.IsCancellationRequested) { cancelled = true; break; }
                }

                if (exited)
                {
                    liveOut.Pump(final: true);
                    liveErr.Pump(final: true);
                }
                else
                {
                    // Timeout OR the user pressed STOP: kill cmd.exe and everything it started.
                    ToolCancellation.KillTree(proc);
                    liveOut.Pump(final: true);   // show what was printed right before the kill
                    liveErr.Pump(final: true);
                }

                int exitCode = 0;
                if (exited)
                {
                    try { exitCode = proc.ExitCode; } catch { /* best effort */ }
                }

                return new CapturedCmdResult(exited, exitCode, ReadCapturedFile(tempOut), ReadCapturedFile(tempErr), cancelled);
            }
            finally
            {
                TryDeleteFile(tempOut);
                TryDeleteFile(tempErr);
            }
        }

        /// <summary>Note appended to the output of a command that was killed because the user pressed STOP.</summary>
        public static string StoppedByUserNote(string what) =>
            $"\n\n⏹ STOPPED BY USER: '{what}' was killed (with all its child processes) because the user pressed STOP. " +
            "The output above is only what was printed before the kill. The command did NOT finish, " +
            "so check the current state before you repeat or continue it.";

        /// <summary>Text appended to a result when the exit code is non-zero (empty output alone cannot show a killed/blocked command).</summary>
        public static string FormatExitNote(int exitCode)
        {
            if (exitCode == 0) return "";
            string note = $"\n[exit code {exitCode} (0x{exitCode:X8})]";
            if (unchecked((uint)exitCode) == 0xC0000022u)
                note += " Access denied: a security tool may have blocked a program started by this command.";
            return note;
        }

        // ── Live tail of a temp output file ───────────────────────────────────────

        private sealed class LiveFileTail
        {
            // If more than this has piled up between two polls (a very chatty command),
            // skip ahead: the live view only needs the latest output anyway.
            private const long MaxBacklogBytes = 128 * 1024;
            private const int  MaxMessageChars = 8000;

            private readonly string _path;
            private readonly string _prefix;
            private readonly Action<string>? _sink;
            private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
            private readonly StringBuilder _partial = new();
            private long _offset;

            public LiveFileTail(string path, string prefix, Action<string>? sink)
            {
                _path   = path;
                _prefix = prefix;
                _sink   = sink;
            }

            /// <summary>Read newly appended bytes and report every COMPLETE new line. With <paramref name="final"/> = true a trailing partial line is reported too.</summary>
            public void Pump(bool final = false)
            {
                if (_sink == null) return;

                try
                {
                    if (File.Exists(_path))
                    {
                        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);

                        long length = fs.Length;
                        if (length > _offset)
                        {
                            string skipNote = "";
                            if (length - _offset > MaxBacklogBytes)
                            {
                                long skipped = length - _offset - MaxBacklogBytes;
                                _offset = length - MaxBacklogBytes;
                                _decoder.Reset();
                                _partial.Clear();
                                skipNote = $"… (live view skipped {skipped / 1024} KB of output)\n";
                            }

                            fs.Seek(_offset, SeekOrigin.Begin);
                            var buf = new byte[length - _offset];
                            int n = fs.Read(buf, 0, buf.Length);
                            if (n > 0)
                            {
                                _offset += n;
                                var chars = new char[_decoder.GetCharCount(buf, 0, n)];
                                int c = _decoder.GetChars(buf, 0, n, chars, 0);
                                _partial.Append(skipNote);
                                _partial.Append(chars, 0, c);
                            }
                        }
                    }

                    if (_partial.Length == 0) return;

                    string text = _partial.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
                    int lastNl  = text.LastIndexOf('\n');

                    string complete;
                    if (lastNl >= 0)
                    {
                        complete = text.Substring(0, lastNl);
                        _partial.Clear();
                        _partial.Append(text, lastNl + 1, text.Length - lastNl - 1);
                    }
                    else if (final || _partial.Length > 2000)
                    {
                        complete = text;     // long line without newline, or the very end
                        _partial.Clear();
                    }
                    else
                    {
                        return;              // wait for the rest of the line
                    }

                    if (final && _partial.Length > 0)
                    {
                        complete += "\n" + _partial;
                        _partial.Clear();
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

                    _sink(complete);
                }
                catch
                {
                    // Live display only — never let it break the command.
                }
            }
        }

        // ── Captured file reading (UTF-8, falls back to the OEM code page) ───────

        private static string ReadCapturedFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";

                byte[] bytes;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var ms = new MemoryStream())
                {
                    fs.CopyTo(ms);
                    bytes = ms.ToArray();
                }

                if (bytes.Length == 0) return "";

                string utf8Text = Encoding.UTF8.GetString(bytes);

                if (utf8Text.IndexOf('\uFFFD') < 0)
                    return utf8Text;

                try
                {
                    string oemText = DecodeUsingSystemOemCodePage(bytes);
                    if (!string.IsNullOrEmpty(oemText))
                        return oemText;
                }
                catch
                {
                    // Best effort — fall through to the lossy UTF-8 text below.
                }

                return utf8Text;
            }
            catch (Exception ex)
            {
                return $"[Error reading captured output from '{path}': {ex.Message}]";
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MultiByteToWideChar(
            uint codePage, uint dwFlags, byte[] lpMultiByteStr, int cbMultiByte,
            char[]? lpWideCharStr, int cchWideChar);

        private static string DecodeUsingSystemOemCodePage(byte[] bytes)
        {
            uint oemCodePage = GetOEMCP();
            int charsNeeded = MultiByteToWideChar(oemCodePage, 0, bytes, bytes.Length, null, 0);
            if (charsNeeded <= 0) return string.Empty;

            char[] buffer = new char[charsNeeded];
            int charsWritten = MultiByteToWideChar(oemCodePage, 0, bytes, bytes.Length, buffer, charsNeeded);
            return new string(buffer, 0, charsWritten);
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
    }
}
#endif
