using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using GTA.Core;
#if !CO_WORKER_USE
using GTA.Tools.Impl.External;
#endif
using GTA.Tools.Impl.Common;

namespace GTA.Tools
{
    internal static class ToolsExternal
    {
#if !CO_WORKER_USE // run_shell section: removed in the co-worker build
        private const int RunShellMaxTokens = 200_000;
        // ── run_shell ─────────────────────────────────────────────────────────────

        [RecordToLog]
        [ToolTag("external", "shell")]
        [ToolInfo(
            name: "run_shell",
            description: "Execute a Windows cmd.exe command on the local machine and return its output. " +
                         "Use Windows cmd syntax (e.g. dir, mkdir, copy, del, type, echo). " +
                         "This is NOT Linux bash — do not use Linux commands. In particular, this " +
                         "machine's cmd.exe has NO 'tail'/'head'/'grep'/'less'/'wc'/'sed'/'awk' etc. " +
                         "available — piping into 'tail' (e.g. \"... | tail -n 60\") FAILS with cmd's own " +
                         "\"'tail' is not recognized as an internal or external command...\" error, which " +
                         "often renders as GARBLED/MOJIBAKE text on screen — that is EXPECTED (cmd prints " +
                         "that error using the system's local codepage, not a tool malfunction), and simply " +
                         "means the command you piped into doesn't exist here. To limit/filter output " +
                         "instead, use 'findstr' (substring search) or PowerShell, e.g. " +
                         "powershell -Command \"Get-Content 'file.txt' -Tail 60\" for the last N lines. " +
                         "NETWORK: every command gets the corporate proxy injected by default via " +
                         "HTTP_PROXY/HTTPS_PROXY environment variables. TLS certificate verification stays ON. " +
                         "KNOWN LIMITATION: some external tools (e.g. pip, curl) have no " +
                         "way to be told to trust this proxy's TLS-inspection (MITM) certificate and MAY " +
                         "fail with a TLS/SSL error — this is a known, accepted risk, not a tool " +
                         "malfunction. OUTPUT SIZE GUARD: if the combined result is estimated (approximate " +
                         "LLM tokens) to exceed 200,000 tokens, the FULL output is saved to a temp .txt " +
                         "file and only a head+tail preview (which fits the budget) is returned instead, " +
                         "with the saved file's path so you can inspect the omitted middle section with " +
                         "read_line/search_line if needed — this only happens for genuinely huge output " +
                         "(verbose builds, big dumps, etc.), never a tool malfunction. " +
                         "FORBIDDEN POWERSHELL OPTIONS (hard-blocked by this tool — the command is NOT run and an " +
                         "error comes back): (1) -EncodedCommand / -enc / -ec / -e, (2) -WindowStyle Hidden, " +
                         "(3) -ExecutionPolicy Bypass / Unrestricted (also Set-ExecutionPolicy Bypass). The company " +
                         "endpoint security (Symantec SONAR) treats these as malware behavior and will block this " +
                         "whole app from starting processes. They are never needed: this tool already runs with no " +
                         "visible window; for a long script, write it to a .ps1 file with write_file and run " +
                         "powershell -NoProfile -File \"path.ps1\" (or powershell -NoProfile -Command \"...\"); to " +
                         "start a long-running app in the background use run_app_background. If a .ps1 file fails " +
                         "with 'running scripts is disabled', do NOT retry with Bypass — tell the user to run once: " +
                         "Set-ExecutionPolicy -Scope CurrentUser RemoteSigned. The same options inside a .ps1/.bat/" +
                         ".cmd file that the command runs are blocked too. ALSO HARD-BLOCKED: PowerShell web " +
                         "requests — Invoke-RestMethod (irm), Invoke-WebRequest (iwr) and Net.WebClient. For HTTP " +
                         "calls to the local GreatTamanaEngine/editor server (127.0.0.1) use the http_request " +
                         "tool; for other scripting (JSON parsing, math, anything with HTTP) use Python — write a " +
                         ".py file with write_file and run python \"file.py\", or use the inline_python tool. " +
                         "(Load http_request / inline_python with load_tools first.) Loopback traffic is never " +
                         "sent through the corporate proxy (NO_PROXY is set for 127.0.0.1/localhost).")]
        [Parameter(index: 0, name: "command",
            type: "string",
            description: "The Windows cmd.exe command string to execute (e.g. \"dir C:\\temp\").",
            required: true)]
        [Parameter(index: 1, name: "timeout_seconds",
            type: "integer",
            description: "Optional. Max seconds to wait before force-killing the command (and its " +
                         "entire child-process tree). Defaults to 300 (5 minutes). Use a larger value " +
                         "for long builds/compiles.",
            required: false)]
        public static object RunShell(JsonObject input)
        {
            string cmd = input["command"]!.GetValue<string>();
            int timeoutSeconds = ParseOptionalInt(input["timeout_seconds"], 300);

            // Hard wall (see FindForbiddenPowerShellUsage): refuse commands that use the
            // PowerShell flags endpoint-security tools treat as malware behavior.
            string? blocked = FindForbiddenPowerShellUsage(cmd);
            if (blocked != null)
                return blocked;

            return RunShellCore(cmd, timeoutSeconds);
        }

        private const RegexOptions RxOpts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // "powershell ..." / "pwsh ..." and the rest of that command segment (up to & or |).
        private static readonly Regex PsHostRegex = new(
            @"(?<![\w-])(?:powershell|pwsh)(?:\.exe)?['""]?(?<rest>[^&|\r\n]*)", RxOpts);

        private static readonly Regex ArgTokenRegex = new(@"""[^""]*""|'[^']*'|\S+", RxOpts);

        // A quoted flag such as '-EncodedCommand' (used inside Start-Process -ArgumentList).
        private static readonly Regex QuotedFlagRegex = new(@"['""](?<flag>[-/][A-Za-z]+)['""]", RxOpts);

        // "-Name value" pairs, e.g. -WindowStyle Hidden, -ExecutionPolicy Bypass, '-w','hidden'.
        private static readonly Regex OptionValueRegex = new(
            @"(?<!\w)[-/](?<name>[A-Za-z]+)['""]?[\s:=,]+['""]*(?<val>[A-Za-z]+)", RxOpts);

        private static readonly Regex SetPolicyBypassRegex = new(
            @"Set-ExecutionPolicy\b[^&|\r\n]*?\b(?:bypass|unrestricted)\b", RxOpts);

        private static readonly Regex ScriptPathRegex = new(
            @"(?:[A-Za-z]:|%[A-Za-z_]+%)[\\/][^\s""'|&<>]*\.(?:ps1|bat|cmd)\b", RxOpts);

        // PowerShell web requests and WebClient. irm/iwr are the short aliases; the lookarounds
        // keep them from matching inside words, file names (irm.txt) or paths.
        private static readonly Regex NetworkBanRegex = new(
            @"(?<![\w\-./\\])(?:Invoke-RestMethod|Invoke-WebRequest|irm|iwr)(?![\w\-.])|\bNet\.WebClient\b", RxOpts);

        private static bool IsPrefixOf(string name, string full, int minLen) =>
            name.Length >= minLen && name.Length <= full.Length &&
            full.StartsWith(name, StringComparison.OrdinalIgnoreCase);

        // -EncodedCommand accepts -e, -ec and any prefix of the full name.
        private static bool IsEncodedFlag(string name) =>
            name.Equals("ec", StringComparison.OrdinalIgnoreCase) ||
            (name.Length > 0 && char.ToLowerInvariant(name[0]) == 'e' &&
             IsPrefixOf(name, "encodedcommand", 1));

        private static string? FindForbiddenPowerShellUsage(string cmd)
        {
            string? hit = CheckPowerShellText(cmd, null);
            if (hit != null) return hit;

            // Also look inside script files the command runs (e.g. powershell -File x.ps1).
            foreach (Match m in ScriptPathRegex.Matches(cmd))
            {
                try
                {
                    string path = Environment.ExpandEnvironmentVariables(m.Value);
                    var fi = new FileInfo(path);
                    if (!fi.Exists || fi.Length > 1_000_000) continue;
                    hit = CheckPowerShellText(File.ReadAllText(path), path);
                    if (hit != null) return hit;
                }
                catch { /* unreadable file: let it run, the guard is best effort */ }
            }
            return null;
        }

        internal static string? CheckPowerShellText(string text, string? scriptPath, string toolName = "run_shell")
        {
            var reasons = new List<string>();
            bool bypass = false;

            // 1) Host options of every powershell/pwsh call: -EncodedCommand.
            foreach (Match m in PsHostRegex.Matches(text))
            {
                var tokens = ArgTokenRegex.Matches(m.Groups["rest"].Value);
                for (int i = 0; i < tokens.Count; i++)
                {
                    string t = tokens[i].Value.Trim('"', '\'');
                    if (t.Length < 2 || (t[0] != '-' && t[0] != '/')) break;

                    string name = t.Substring(1);
                    int colon = name.IndexOf(':');
                    if (colon >= 0) name = name.Substring(0, colon);

                    if (IsEncodedFlag(name))
                    {
                        reasons.Add("-EncodedCommand (any form: -e, -ec, -enc ...)");
                        break;
                    }
                    // Everything after -Command / -File belongs to the script, not the host.
                    if (IsPrefixOf(name, "command", 1) || IsPrefixOf(name, "file", 1)) break;

                    bool isSwitch = name.StartsWith("no", StringComparison.OrdinalIgnoreCase) ||
                                    name.Equals("sta", StringComparison.OrdinalIgnoreCase) ||
                                    name.Equals("mta", StringComparison.OrdinalIgnoreCase);
                    if (!isSwitch && colon < 0) i++; // option with a separate value
                }
            }

            if (PsHostRegex.IsMatch(text))
            {
                foreach (Match m in QuotedFlagRegex.Matches(text))
                {
                    if (IsEncodedFlag(m.Groups["flag"].Value.Substring(1)))
                        reasons.Add("-EncodedCommand (passed as a quoted argument)");
                }
            }

            // 3) -WindowStyle Hidden and -ExecutionPolicy Bypass/Unrestricted.
            foreach (Match m in OptionValueRegex.Matches(text))
            {
                string name = m.Groups["name"].Value;
                string val  = m.Groups["val"].Value;

                if (IsPrefixOf(name, "windowstyle", 1) &&
                    val.Equals("hidden", StringComparison.OrdinalIgnoreCase))
                    reasons.Add("-WindowStyle Hidden");

                if ((IsPrefixOf(name, "executionpolicy", 2) || name.Equals("ep", StringComparison.OrdinalIgnoreCase)) &&
                    (val.Equals("bypass", StringComparison.OrdinalIgnoreCase) ||
                     val.Equals("unrestricted", StringComparison.OrdinalIgnoreCase)))
                    bypass = true;
            }
            if (SetPolicyBypassRegex.IsMatch(text)) bypass = true;

            // 4) PowerShell web requests / WebClient (see NetworkBanRegex).
            var netHits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in NetworkBanRegex.Matches(text))
                netHits.Add(m.Value);

            if (reasons.Count == 0 && !bypass && netHits.Count == 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine($"Error: {toolName} BLOCKED this command (it was NOT run). " +
                          "Endpoint security (e.g. Symantec SONAR) treats these PowerShell patterns as malware behavior, " +
                          "and this tool refuses them on purpose.");
            if (scriptPath != null)
                sb.AppendLine($"Found inside the script file: {scriptPath} - edit that file to remove it.");

            foreach (string r in new HashSet<string>(reasons))
            {
                sb.AppendLine($"- Forbidden: {r}");
                if (r.StartsWith("-EncodedCommand", StringComparison.Ordinal))
                    sb.AppendLine("  Instead: write the script to a .ps1 file (write_file) and run " +
                                  "powershell -NoProfile -File \"path.ps1\", or use powershell -NoProfile -Command \"...\".");
                else
                    sb.AppendLine("  Instead: remove it. run_shell already runs with no visible window. To start a " +
                                  "long-running app in the background, use run_app_background.");
            }

            if (netHits.Count > 0)
            {
                sb.AppendLine($"- Forbidden: {string.Join(", ", netHits)} (PowerShell web requests / WebClient).");
                sb.AppendLine("  Instead: to call the local GreatTamanaEngine / editor HTTP server (127.0.0.1), use the " +
                              "http_request tool (load it first: load_tools(tool_names: \"http_request\")). For any other " +
                              "job that needs HTTP or JSON parsing, write a Python script (write_file, then " +
                              "python \"file.py\") or use the inline_python tool (load_tools(tool_names: \"inline_python\")).");
            }

            if (bypass)
            {
                var (policy, source) = GetEffectiveScriptPolicy();
                bool allowsLocal = policy.StartsWith("RemoteSigned", StringComparison.OrdinalIgnoreCase) ||
                                   policy.StartsWith("Unrestricted", StringComparison.OrdinalIgnoreCase) ||
                                   policy.StartsWith("Bypass", StringComparison.OrdinalIgnoreCase);
                bool fromGroupPolicy = source.Contains("group policy", StringComparison.OrdinalIgnoreCase);

                sb.AppendLine("- Forbidden: -ExecutionPolicy Bypass / Unrestricted (also via Set-ExecutionPolicy).");
                sb.AppendLine($"  Windows PowerShell effective policy: {policy} (source: {source}).");
                if (allowsLocal)
                    sb.AppendLine("  Instead: just remove the flag. Local .ps1 files already run under this policy.");
                else if (fromGroupPolicy)
                    sb.AppendLine("  The policy is set by company group policy, so -ExecutionPolicy Bypass would not " +
                                  "work anyway. Do NOT retry with it. Use powershell -Command \"...\", cmd, or python " +
                                  "instead of a .ps1 file, or ask IT.");
                else
                    sb.AppendLine("  This policy blocks .ps1 files. Do NOT retry with Bypass. Ask the user to run this once " +
                                  "in a normal PowerShell window: Set-ExecutionPolicy -Scope CurrentUser RemoteSigned. " +
                                  "Until then use powershell -Command \"...\", cmd, or python instead of a .ps1 file.");
            }
            return sb.ToString().TrimEnd();
        }

        // Effective Windows PowerShell policy, in PowerShell's own precedence order:
        // MachinePolicy, UserPolicy, CurrentUser, LocalMachine. Unset = Restricted (client default).
        private static (string Policy, string Source) GetEffectiveScriptPolicy()
        {
            try
            {
                const string gpoKey = @"SOFTWARE\Policies\Microsoft\Windows\PowerShell";
                const string shellKey = @"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell";

                using (var mk = Registry.LocalMachine.OpenSubKey(gpoKey))
                {
                    if (mk?.GetValue("EnableScripts") is int en)
                    {
                        if (en == 0) return ("Restricted (scripts disabled)", "MachinePolicy (group policy)");
                        if (mk.GetValue("ExecutionPolicy") is string p && p.Length > 0)
                            return (p, "MachinePolicy (group policy)");
                    }
                }
                using (var uk = Registry.CurrentUser.OpenSubKey(gpoKey))
                {
                    if (uk?.GetValue("EnableScripts") is int en)
                    {
                        if (en == 0) return ("Restricted (scripts disabled)", "UserPolicy (group policy)");
                        if (uk.GetValue("ExecutionPolicy") is string p && p.Length > 0)
                            return (p, "UserPolicy (group policy)");
                    }
                }
                using (var cu = Registry.CurrentUser.OpenSubKey(shellKey))
                {
                    if (cu?.GetValue("ExecutionPolicy") is string p && p.Length > 0)
                        return (p, "CurrentUser");
                }
                using (var lm = Registry.LocalMachine.OpenSubKey(shellKey))
                {
                    if (lm?.GetValue("ExecutionPolicy") is string p && p.Length > 0)
                        return (p, "LocalMachine");
                }
            }
            catch { /* fall through to the default */ }

            return ("Restricted", "default, no policy set");
        }

        // Proxy for child processes: taken from the Windows system settings, logged in with the current
        // Windows account (via a local relay). Empty when Windows uses no proxy. Nothing in init.ini.
        internal static Dictionary<string, string> BuildProxyEnvironmentVariables() =>
            ProxyBridge.BuildChildProcessEnvironment();

        private static string RunShellCore(string cmd, int timeoutSeconds)
        {
            // Shared runner (also used by gcc / cmake): live output, timeout kill, encoding handling.
            CapturedCmdResult r = ExternalProcessUtils.RunCmdCaptured(
                cmd, "run_shell", null, (long)timeoutSeconds * 1000,
                BuildProxyEnvironmentVariables(), ToolRegistry.ReportProgress);

            string timeoutNote = "";
            if (!r.Exited)
            {
                timeoutNote =
                    $"\n\n⚠ TIMEOUT: command did not finish within {timeoutSeconds}s and was " +
                    "force-killed (including any child/grandchild processes). Output below is " +
                    "whatever was captured before the kill. Pass a larger 'timeout_seconds' if " +
                    "this command is just slow (e.g. a big build), not stuck.";
            }

            string result = string.IsNullOrWhiteSpace(r.Stderr)
                ? r.Stdout
                : $"STDOUT:\n{r.Stdout}\nSTDERR:\n{r.Stderr}";

            // Non-zero exit code is appended and logged: empty output alone cannot show a killed/blocked command.
            string exitNote = "";
            if (r.Exited && r.ExitCode != 0)
            {
                exitNote = ExternalProcessUtils.FormatExitNote(r.ExitCode);
                GTA.Core.SessionFileLogger.Log("TOOL_CALL",
                    $"run_shell exit code {r.ExitCode} (0x{r.ExitCode:X8}), output {result.Length} chars");
            }

            return ApplyOutputTokenBudget(result + exitNote + timeoutNote);
        }

        internal static string ApplyOutputTokenBudget(string fullResult)
        {
            int estimatedTokens = ToolsCommonPipe.EstimateTokenCount(fullResult);
            if (estimatedTokens <= RunShellMaxTokens)
                return fullResult;

            int n = fullResult.Length;

            // prefix[i] = number of ASCII chars among fullResult[0..i)
            int[] asciiPrefix = new int[n + 1];
            for (int i = 0; i < n; i++)
                asciiPrefix[i + 1] = asciiPrefix[i] + (fullResult[i] < 128 ? 1 : 0);

            double Cost(int i) => (asciiPrefix[i] / 4.0) + (i - asciiPrefix[i]);
            double totalCost = Cost(n);

            const int overheadReserveTokens = 400; // room for the notice text itself
            int budget = RunShellMaxTokens - overheadReserveTokens;
            if (budget < 2_000) budget = 2_000; // sanity floor, should never trigger

            int headBudget = budget * 2 / 5; // 40%
            int tailBudget = budget - headBudget; // 60%

            // Largest headEnd (0..n) with Cost(headEnd) <= headBudget.
            int lo = 0, hi = n;
            while (lo < hi)
            {
                int mid = lo + (hi - lo + 1) / 2;
                if (Cost(mid) <= headBudget) lo = mid; else hi = mid - 1;
            }
            int headEnd = lo;

            // Smallest tailStart (0..n) with (totalCost - Cost(tailStart)) <= tailBudget.
            lo = 0; hi = n;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (totalCost - Cost(mid) <= tailBudget) hi = mid; else lo = mid + 1;
            }
            int tailStart = lo;
            if (tailStart < headEnd) tailStart = headEnd; // never overlap

            string head         = fullResult.Substring(0, headEnd);
            string tail         = fullResult.Substring(tailStart);
            int    omittedChars = tailStart - headEnd;

            // Save the FULL untruncated text to disk — nothing is actually lost,
            // it's just not all shoved into this one tool response.
            string savedPath = Path.Combine(Path.GetTempPath(), $"run_shell_full_output_{Guid.NewGuid():N}.txt");
            string saveNote;
            try
            {
                File.WriteAllText(savedPath, fullResult, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                saveNote = $"The FULL output was saved to: {savedPath}\n" +
                           "Use read_line / search_line / get_file_line_count on that path to inspect the " +
                           "omitted middle section if you need it.";
            }
            catch (Exception ex)
            {
                saveNote = $"(Could not save the full output to disk — {ex.Message}. The omitted middle " +
                           "section below is unfortunately not recoverable.)";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"⚠ OUTPUT TOO LARGE: estimated ~{estimatedTokens:N0} tokens, over the " +
                          $"{RunShellMaxTokens:N0}-token limit for a single run_shell response. Showing " +
                          $"the START and END only ({omittedChars:N0} character(s) omitted from the middle).");
            sb.AppendLine(saveNote);
            sb.AppendLine();
            sb.Append(head);
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine($"... [{omittedChars:N0} character(s) OMITTED — see the saved file above for the " +
                          "full output] ...");
            sb.AppendLine();
            sb.Append(tail);

            return sb.ToString();
        }

        private static int ParseOptionalInt(JsonNode? node, int defaultValue)
        {
            if (node == null) return defaultValue;

            try
            {
                return node.GetValue<int>();
            }
            catch (Exception)
            {
                string? asString = node.GetValue<object>()?.ToString() ?? node.ToString();
                if (int.TryParse(asString, out int parsed))
                    return parsed;
                return defaultValue;
            }
        }
#endif // !CO_WORKER_USE (run_shell section)

        // ── load_image ────────────────────────────────────────────────────────────

        [ToolTag("external", "image", "bootstrap")]
        [ToolInfo(
            name: "load_image",
            description: "Load an image file from the local Windows filesystem as base64 " +
                         "and return it DIRECTLY as a real, viewable image content block " +
                         "so the AI can see and describe its actual pixels (never just a " +
                         "file path or placeholder — this tool's result is exempt from the " +
                         "evidence-store path-redaction applied to bulk image tools). " +
                         "ONLY supports these image formats: PNG, JPG/JPEG, GIF, WEBP. " +
                         "Do NOT call this on any other file type (e.g. .xml, .pdf, .docx, .txt, " +
                         ".json, .svg) — it cannot render them; if called anyway, it returns a " +
                         "plain error string saying the format is unsupported instead of an image.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the image file (e.g. C:\\screenshots\\ui.png). " +
                         "Must be PNG, JPG/JPEG, GIF, or WEBP — no other file types are supported.",
            required: true)]
        public static object LoadImage(JsonObject input)
        {
            string path = input["path"]!.GetValue<string>().Trim().Trim('"');

            if (!File.Exists(path))
            {
                if (Directory.Exists(path))
                    return $"Error: '{path}' is a directory, not an image file.";
                return $"Error: Image file not found at '{path}'.";
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (UnauthorizedAccessException)
            {
                return $"Error: Access denied — cannot read '{path}'.";
            }
            catch (IOException ex)
            {
                return $"Error: IO error while reading '{path}': {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"Error: Unexpected error while reading '{path}': {ex.Message}";
            }

            if (bytes.Length == 0)
                return $"Error: '{path}' is an empty file (0 bytes) — nothing to display.";

            string? sniffedMediaType = ImageUtils.DetectImageMagic(bytes);
            if (sniffedMediaType == null)
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                bool extLooksLikeImage = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";
                if (!extLooksLikeImage)
                {
                    return $"Error: '{path}' is not a supported image format for load_image " +
                           $"(extension '{ext}' — content bytes also don't match any known image " +
                           "signature). load_image only supports PNG, JPG/JPEG, GIF, and WEBP. " +
                           "Use a different tool (e.g. read_file) to read this file's contents.";
                }
            }

            string b64       = Convert.ToBase64String(bytes);
            string mediaType = sniffedMediaType ?? ImageUtils.ResolveMediaType(bytes, path);

            return new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"]       = "base64",
                        ["media_type"] = mediaType,
                        ["data"]       = b64
                    }
                }
            };
        }
    }
}
