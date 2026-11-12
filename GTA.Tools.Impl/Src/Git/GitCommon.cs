#if !CO_WORKER_USE
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using GTA.Core;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Scoop root for the current user.</summary>
        private static string GetScoopRoot()
        {
            string? scoopRoot = Environment.GetEnvironmentVariable("SCOOP");
            if (!string.IsNullOrWhiteSpace(scoopRoot))
                return scoopRoot;

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop");
        }

        /// <summary>Git for Windows installed by Scoop.</summary>
        private static string GetGitInstallRoot()
            => Path.Combine(GetScoopRoot(), "apps", "git", "current");

        /// <summary>Full path to Scoop's git.exe entry point.</summary>
        private static string GetGitExecutablePath()
            => Path.Combine(GetGitInstallRoot(), "cmd", "git.exe");

        /// <summary>Git exec-path where helpers like git-remote-https.exe live.</summary>
        private static string GetGitExecPath()
            => Path.Combine(GetGitInstallRoot(), "mingw64", "libexec", "git-core");

        /// <summary>Folders to prepend to PATH so the Scoop Git install can find its DLLs and shell tools.</summary>
        private static string GetGitPathPrefix()
            => string.Join(
                Path.PathSeparator.ToString(),
                new[]
                {
                    Path.Combine(GetGitInstallRoot(), "cmd"),
                    Path.Combine(GetGitInstallRoot(), "mingw64", "bin"),
                    Path.Combine(GetGitInstallRoot(), "usr", "bin")
                });

        private static readonly Regex UrlCredentialRegex =
            new(@"(?<=://)[^/\s:@]+:[^/\s@]*@", RegexOptions.Compiled);

        private static readonly Regex PatRegex =
            new(@"(github_pat_[A-Za-z0-9_]{10,}|gh[pousr]_[A-Za-z0-9]{20,})", RegexOptions.Compiled);

        /// <summary>Masks the auth header, proxy credentials, URL user:pass@ and PAT-like tokens before echoing args.</summary>
        private static string SanitizeArgsForDisplay(string[] args)
        {
            var display = new string[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("http.extraheader=", StringComparison.OrdinalIgnoreCase))
                    display[i] = "http.extraheader=[REDACTED]";
                else if (a.StartsWith("http.proxy=", StringComparison.OrdinalIgnoreCase))
                    display[i] = "http.proxy=[REDACTED]";
                else
                    display[i] = PatRegex.Replace(UrlCredentialRegex.Replace(a, "[REDACTED]@"), "[REDACTED]");
            }
            return string.Join(' ', display);
        }

        private static string RunGitCommand(string repositoryPath, params string[] args)
        {
            try
            {
                string gitInstallRoot = GetGitInstallRoot();
                string gitExe = GetGitExecutablePath();
                string gitExecPath = GetGitExecPath();
                string gitPathPrefix = GetGitPathPrefix();

                if (!Directory.Exists(gitInstallRoot))
                    return $"Error: Scoop Git install root was not found at '{gitInstallRoot}'. Install Git with Scoop first.";

                if (!File.Exists(gitExe))
                    return $"Error: Scoop git.exe was not found at '{gitExe}'. The Scoop Git install looks broken.";

                if (!Directory.Exists(gitExecPath))
                    return $"Error: Scoop Git exec-path folder was not found at '{gitExecPath}'. The Scoop Git install looks broken.";

                if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
                    return $"Error: repository path '{repositoryPath}' does not exist.";

                var psi = new ProcessStartInfo(gitExe)
                {
                    WorkingDirectory       = repositoryPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding  = Encoding.UTF8
                };

                // Never let a pager or an interactive credential/editor prompt block this
                // non-interactive, LLM-driven process.
                psi.ArgumentList.Add("--no-pager");
                foreach (string a in args)
                    psi.ArgumentList.Add(a);

                // Point Git at Scoop's real helper-executable folder and make sure PATH contains
                // the matching Git-for-Windows bin directories.
                psi.EnvironmentVariables["GIT_EXEC_PATH"] = gitExecPath;
                psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

                string existingPath = psi.EnvironmentVariables["PATH"] ?? string.Empty;
                psi.EnvironmentVariables["PATH"] = string.IsNullOrWhiteSpace(existingPath)
                    ? gitPathPrefix
                    : gitPathPrefix + Path.PathSeparator + existingPath;

                using Process process = Process.Start(psi)!;

                // Read both streams concurrently to avoid the classic buffer-fill deadlock.
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                // Kills the whole tree on timeout AND when the user presses STOP.
                bool exited = ToolCancellation.WaitForExitOrKill(process, DEFAULT_TIMEOUT_MS, out bool cancelled);
                if (cancelled)
                    return $"⏹ STOPPED BY USER: 'git {SanitizeArgsForDisplay(args)}' was killed because the user pressed STOP. " +
                           "It did NOT finish. Check the repository state (git status) before you repeat it.";
                if (!exited)
                {
                    return $"Error: 'git {SanitizeArgsForDisplay(args)}' timed out after {DEFAULT_TIMEOUT_MS / 1000} seconds and was killed.";
                }

                string stdout = string.Empty;
                string stderr = string.Empty;
                try { stdout = stdoutTask.Result; } catch { /* best effort */ }
                try { stderr = stderrTask.Result; } catch { /* best effort */ }

                var sb = new StringBuilder();
                sb.Append(process.ExitCode == 0 ? "Success" : $"Failed (exit code {process.ExitCode})")
                  .Append(" — command: git ").Append(SanitizeArgsForDisplay(args));

                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    sb.AppendLine();
                    sb.AppendLine("STDOUT:");
                    sb.Append(stdout.Trim());
                }

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    sb.AppendLine();
                    sb.AppendLine("STDERR:");
                    sb.Append(stderr.Trim());
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error: exception while running 'git {SanitizeArgsForDisplay(args)}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
