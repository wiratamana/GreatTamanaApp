#if !CO_WORKER_USE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using GTA.Core;
using GTA.Tools;

namespace GTA.App;

public partial class LlmChatWindow
{
    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>Local repository polled by the watcher (ini key path_issue_watcher_repo).</summary>
    internal static string IssueWatcherRepoPath => InitSettings.Get("path_issue_watcher_repo", "");

    /// <summary>How often to poll — the very first check happens immediately on "Start", before any delay.</summary>
    private static readonly TimeSpan IssueWatcherPollInterval = TimeSpan.FromMinutes(1);

    /// <summary>LLM backend every dispatched issue-task is forced onto (see CreateAndRunTask's 'forcedBackend').</summary>
    private const string IssueWatcherForcedBackend = "Claude";

    /// <summary>Tool set pre-loaded on every dispatched issue-task's single "Prompt" step</summary>
    private static readonly string[] IssueWatcherPreloadedTools =
    {
        "git_add", "git_commit", "git_status",
        "gcc", "cmake",
        "run_shell", "run_app_background", "stop_app_background",
        "get_available_tools", "load_tools",
        "search_in_dir",
        "delegate_task",
        "gte_send_request",
        "git_post_issue_comment",
    };

    // ── State (in-memory only; always starts stopped) ─────────────────────────

    private CancellationTokenSource? _issueWatcherCts;
    private bool                     _issueWatcherRunning;

    /// <summary>Issue numbers currently being dispatched/run (between "Task Dispatched." and "Task Completed.")</summary>
    private readonly HashSet<int> _issueWatcherInFlight = new();

    private Button?    _issueWatcherButton;
    private TextBlock? _issueWatcherStatusLabel;

    private static string IssueWatcherCacheDir =>
        Path.Combine(AppContext.BaseDirectory, "IssueWatcherCache", SanitizeForFileName(
            Path.GetFileName(IssueWatcherRepoPath.TrimEnd('\\', '/'))));

    private static string SanitizeForFileName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }

    // ── Start/Stop toggle (sidebar button click handler) ──────────────────────

    private void ToggleIssueWatcher()
    {
        if (_issueWatcherRunning) StopIssueWatcher();
        else                      StartIssueWatcher();
    }

    private void StartIssueWatcher()
    {
        if (_issueWatcherRunning) return;

        if (string.IsNullOrWhiteSpace(IssueWatcherRepoPath))
        {
            SetIssueWatcherStatus("Cannot start — set path_issue_watcher_repo in Config\\init.ini.");
            return;
        }

        if (GetCurrentlyRunningTaskSessions().Count > 0)
        {
            SetIssueWatcherStatus(
                "Cannot start — a Task is currently running, and it needs a dedicated network " +
                "connection (proxy/IP). Starting the watcher could switch Wi-Fi networks and fail " +
                "that task. Wait for it to finish, then try again.");
            return;
        }

        _issueWatcherRunning = true;
        _issueWatcherCts     = new CancellationTokenSource();
        RefreshIssueWatcherButtonVisual();

        _ = RunIssueWatcherLoopAsync(_issueWatcherCts.Token);
    }

    private void StopIssueWatcher()
    {
        if (!_issueWatcherRunning) return;

        _issueWatcherRunning = false;
        try { _issueWatcherCts?.Cancel(); } catch { /* already cancelled/disposed — ignore */ }
        _issueWatcherCts = null;

        RefreshIssueWatcherButtonVisual();
    }

    private void RefreshIssueWatcherButtonVisual()
    {
        if (_issueWatcherButton == null) return;

        if (_issueWatcherRunning)
        {
            _issueWatcherButton.Content = "⏹  Stop Issue Watcher";
            UiTheme.StyleButton(_issueWatcherButton, ColRunningRed, Brushes.White);
            SetIssueWatcherStatus($"Watching '{IssueWatcherRepoPath}' — checking every " +
                                   $"{IssueWatcherPollInterval.TotalMinutes:0} min…");
        }
        else
        {
            _issueWatcherButton.Content = "▶  Start Issue Watcher";
            UiTheme.StyleButton(_issueWatcherButton, ColDoneBadgeBg, Brushes.White);
            SetIssueWatcherStatus("Stopped.");
        }
    }

    private void SetIssueWatcherStatus(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetIssueWatcherStatus(text));
            return;
        }

        if (_issueWatcherStatusLabel != null)
            _issueWatcherStatusLabel.Text = text;
    }

    // ── Polling loop ────────────────────────────────────────────────────────────

    private async Task RunIssueWatcherLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckIssuesOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                SetIssueWatcherStatus($"Error during check ({ex.GetType().Name}: {ex.Message}) — will retry.");
            }

            try
            {
                await Task.Delay(IssueWatcherPollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CheckIssuesOnceAsync(CancellationToken ct)
    {
        // Guard (mirrors StartIssueWatcher's own guard above)
        if (GetCurrentlyRunningTaskSessions().Count > 0)
        {
            SetIssueWatcherStatus(
                "Paused this cycle — a Task is currently running and needs its own dedicated " +
                "network connection; will check again next interval.");
            return;
        }

        SetIssueWatcherStatus($"Checking open issues in '{IssueWatcherRepoPath}'…");

        // The underlying tool call is a genuinely blocking network/Wi-Fi-guard
        // operation — run it off the UI thread so the app stays responsive.
        string listResult = await Task.Run(() => CallTool("git_list_issues", new JsonObject
        {
            ["repository_path"] = IssueWatcherRepoPath,
            ["state"]           = "open",
        }), ct);

        if (listResult.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            SetIssueWatcherStatus($"Skipped this cycle — {TruncateOneLine(listResult, 160)}");
            return;
        }

        var issues = ParseIssueList(listResult);
        SetIssueWatcherStatus($"Checked {issues.Count} open issue(s) at {DateTimeOffset.Now:HH:mm:ss}.");

        foreach (var issue in issues)
        {
            if (ct.IsCancellationRequested) return;
            if (_issueWatcherInFlight.Contains(issue.Number)) continue; // already being handled

            DateTimeOffset? cached = ReadCachedUpdatedAt(issue.Number);
            bool isNew = cached == null || issue.UpdatedAt > cached.Value;
            if (!isNew) continue;

            // Sequential processing (per the feature's design decision)
            await ProcessTriggeredIssueAsync(issue.Number, ct);
        }
    }

    // ── Per-issue trigger handling ─────────────────────────────────────────────

    private async Task ProcessTriggeredIssueAsync(int issueNumber, CancellationToken ct)
    {
        _issueWatcherInFlight.Add(issueNumber);
        try
        {
            SetIssueWatcherStatus($"Issue #{issueNumber}: new activity detected — fetching context…");

            string issueText = await Task.Run(() => CallTool("git_read_issue", new JsonObject
            {
                ["repository_path"] = IssueWatcherRepoPath,
                ["issue_number"]    = issueNumber,
            }), ct);

            string commentsText = await Task.Run(() => CallTool("git_read_issue_comments", new JsonObject
            {
                ["repository_path"] = IssueWatcherRepoPath,
                ["issue_number"]    = issueNumber,
            }), ct);

            string issueContext =
                $"=== GitHub Issue #{issueNumber} (repository: {IssueWatcherRepoPath}) ===\n\n" +
                issueText + "\n\n" +
                "=== Comments on this issue ===\n\n" +
                commentsText;

            // "Task Dispatched." — posted by THIS WATCHER itself (system),
            // never by the LLM.
            SetIssueWatcherStatus($"Issue #{issueNumber}: posting \"Task Dispatched.\"…");
            await Task.Run(() => CallTool("git_post_issue_comment", new JsonObject
            {
                ["repository_path"] = IssueWatcherRepoPath,
                ["issue_number"]    = issueNumber,
                ["comment_body"]    = "🤖 Task Dispatched.",
            }), ct);

            // Re-cache NOW (reflecting our own just-posted comment) so it is
            // never mistaken for new user activity on a later poll cycle.
            await RefreshCachedUpdatedAtFromGithubAsync(issueNumber, ct);

            string prompt = BuildIssueTaskPrompt(issueNumber, issueContext);
            var unit = new TaskUnit
            {
                Kind               = TaskUnitKind.Prompt,
                PromptText         = prompt,
                PreloadedToolNames = new List<string>(IssueWatcherPreloadedTools),
            };

            ChatSession? taskSession = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                taskSession = CreateAndRunTask(
                    new List<TaskUnit> { unit },
                    forcedBackend: IssueWatcherForcedBackend,
                    titleOverride: $"🐙 Issue #{issueNumber}");
            });

            if (taskSession == null)
            {
                SetIssueWatcherStatus($"Issue #{issueNumber}: failed to create a Task session — skipping.");
                return;
            }

            SetIssueWatcherStatus($"Issue #{issueNumber}: task running…");

            // Poll until the whole blueprint finishes
            while (IsTaskSessionStillRunning(taskSession))
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            // "Task Completed." — again, this watcher's own system comment.
            SetIssueWatcherStatus($"Issue #{issueNumber}: posting \"Task Completed.\"…");
            await Task.Run(() => CallTool("git_post_issue_comment", new JsonObject
            {
                ["repository_path"] = IssueWatcherRepoPath,
                ["issue_number"]    = issueNumber,
                ["comment_body"]    = "✅ Task Completed.",
            }), ct);

            await RefreshCachedUpdatedAtFromGithubAsync(issueNumber, ct);

            SetIssueWatcherStatus($"Issue #{issueNumber}: done.");
        }
        catch (OperationCanceledException)
        {
            // Watcher stopped mid-flight — leave the cache exactly as it is;
            // the next "Start" will simply re-evaluate this issue fresh.
        }
        catch (Exception ex)
        {
            SetIssueWatcherStatus($"Issue #{issueNumber}: unexpected error — {ex.Message}");
        }
        finally
        {
            _issueWatcherInFlight.Remove(issueNumber);
        }
    }

    private static string BuildIssueTaskPrompt(int issueNumber, string issueContext)
    {
        return
            issueContext + "\n\n" +
            "=== Instructions ===\n" +
            $"You are working autonomously on GitHub issue #{issueNumber} in the local repository " +
            $"'{IssueWatcherRepoPath}' (already cloned there — use git_add/git_commit/git_status " +
            "directly on it, gcc/cmake to build, run_shell for anything else, " +
            "run_app_background/stop_app_background to visually debug a running app without " +
            "blocking yourself, search_in_dir for quick text search, get_available_tools/load_tools " +
            "if you need a tool you don't currently have, and delegate_task if you want to split " +
            "further work into additional sub-tasks).\n\n" +
            "Break your work down into logical sub-tasks. After you finish EACH sub-task, call " +
            "git_post_issue_comment on this same repository/issue number with a short progress " +
            "update formatted exactly like: \"Task {n}/{total} Done - <one-line summary of what " +
            "you just did>\" (pick your own best-estimate total once you know roughly how many " +
            "sub-tasks you'll need, and keep it consistent across your own updates as you go). " +
            "IMAGE EVIDENCE: if you took a screenshot for visual debugging (e.g. via " +
            "gte_send_request, or a load_image call on something run_app_background produced), " +
            "that tool's own result contains a \"[[SAVED_IMAGE path=\\\"C:\\...\\\"]]\" marker with " +
            "the real file path it was saved to on disk — pass that exact path as " +
            "git_post_issue_comment's own 'image_paths' parameter (comma-separated if more than " +
            "one) to embed it as a real picture directly in your progress comment, so there is " +
            "visual proof on the GitHub issue itself, not just text. Do NOT post \"Task " +
            "Dispatched.\" or \"Task Completed.\" yourself — those two bookend comments are posted " +
            "automatically by the system, not by you.";
    }

    // ── GitHub tool call helper ─────────────────────────────────────────────────

    private static string CallTool(string name, JsonObject input)
    {
        var (found, result) = ToolRegistry.TryExecute(name, input);
        return found ? (result?.ToString() ?? string.Empty) : $"Error: tool '{name}' not found.";
    }

    // ── "updated_at" cache (plain text files — see IssueWatcherCacheDir) ───────

    private static DateTimeOffset? ReadCachedUpdatedAt(int issueNumber)
    {
        string file = Path.Combine(IssueWatcherCacheDir, $"issue_{issueNumber}.txt");
        if (!File.Exists(file)) return null;

        string text = File.ReadAllText(file).Trim();
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : (DateTimeOffset?)null;
    }

    private static void WriteCachedUpdatedAt(int issueNumber, DateTimeOffset updatedAt)
    {
        Directory.CreateDirectory(IssueWatcherCacheDir);
        string file = Path.Combine(IssueWatcherCacheDir, $"issue_{issueNumber}.txt");
        File.WriteAllText(file, updatedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>Re-reads issue #issueNumber's CURRENT 'updated_at' straight from GitHub (via git_read_issue) and re-caches it</summary>
    private static async Task RefreshCachedUpdatedAtFromGithubAsync(int issueNumber, CancellationToken ct)
    {
        string text = await Task.Run(() => CallTool("git_read_issue", new JsonObject
        {
            ["repository_path"] = IssueWatcherRepoPath,
            ["issue_number"]    = issueNumber,
        }), ct);

        var match = Regex.Match(text, @"Updated:\s*(?<updated>\S+)");
        if (match.Success &&
            DateTimeOffset.TryParse(match.Groups["updated"].Value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var updatedAt))
        {
            WriteCachedUpdatedAt(issueNumber, updatedAt);
        }
        else
        {
            WriteCachedUpdatedAt(issueNumber, DateTimeOffset.Now);
        }
    }

    // ── git_list_issues text parsing ────────────────────────────────────────────

    /// <summary>One issue line parsed out of git_list_issues' text output, e.g.</summary>
    private readonly struct IssueListEntry
    {
        public int            Number   { get; init; }
        public string         State    { get; init; }
        public int             CommentCount { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public string         Url      { get; init; }
    }

    private static readonly Regex IssueListLineRegex = new(
        @"^#(?<num>\d+)\s*\[(?<state>\w+)\].*?—\s*(?<comments>\d+)\s*comment\(s\)\s*—\s*updated\s*(?<updated>\S+)\s*—\s*(?<url>\S+)\s*$",
        RegexOptions.Multiline);

    private static List<IssueListEntry> ParseIssueList(string toolOutput)
    {
        var list = new List<IssueListEntry>();

        foreach (Match m in IssueListLineRegex.Matches(toolOutput))
        {
            if (!int.TryParse(m.Groups["num"].Value, out int number)) continue;
            if (!DateTimeOffset.TryParse(m.Groups["updated"].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var updatedAt)) continue;

            list.Add(new IssueListEntry
            {
                Number       = number,
                State        = m.Groups["state"].Value,
                CommentCount = int.TryParse(m.Groups["comments"].Value, out int c) ? c : 0,
                UpdatedAt    = updatedAt,
                Url          = m.Groups["url"].Value,
            });
        }

        return list;
    }
}
#endif
