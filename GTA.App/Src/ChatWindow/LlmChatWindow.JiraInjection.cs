using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GTA.Tools;

namespace GTA.App;

public partial class LlmChatWindow
{
    // ── URL detection ─────────────────────────────────────────────────────

    /// <summary>Matches absolute JIRA "browse ticket" URLs</summary>
    private static readonly Regex JiraUrlRegex = new(
        @"https?://([a-zA-Z0-9.-]+\.atlassian\.net)/browse/([A-Z][A-Z0-9]*-\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Scan raw user prompt text for JIRA browse URLs and return the unique set of (ticket key, base URL) pairs found</summary>
    internal static List<(string Key, string BaseUrl)> ExtractJiraTicketKeys(string text)
    {
        var result = new List<(string Key, string BaseUrl)>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (Match m in JiraUrlRegex.Matches(text))
        {
            string host    = m.Groups[1].Value;
            string key     = m.Groups[2].Value.ToUpperInvariant();
            string baseUrl = $"https://{host}";

            if (!result.Exists(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)))
                result.Add((key, baseUrl));
        }
        return result;
    }

    // ── Fetch ──────────────────────────────────────────────────────────────

    private static Task<string> FetchJiraTicketContentAsync(string ticketKey, string baseUrl) =>
        Task.Run(() =>
        {
            try
            {
                var input = new JsonObject
                {
                    ["issue_key"]     = ticketKey,
                    ["jira_base_url"] = baseUrl,
                };
                var (found, result) = ToolRegistry.TryExecute("jira_read_ticket", input);
                if (!found)
                    return "⚠ Tool 'jira_read_ticket' is not registered — is ToolRegistry.Initialize() running?";
                return result?.ToString() ?? "(empty response)";
            }
            catch (Exception ex)
            {
                return $"⚠ Failed to fetch {ticketKey}: {ex.Message}";
            }
        });

    // ── Foldable "Injection : KEY" widget ────────────────────────────────

    /// <summary>Builds the foldable "Injection : KEY" widget.</summary>
    private static (Control Wrapper, SelectableTextBlock ContentText) BuildJiraInjectionWidget(
        string ticketKey, string initialText)
    {
        var contentTb = new SelectableTextBlock
        {
            Text         = initialText,
            TextWrapping = TextWrapping.Wrap,
            Foreground   = ColMuted,
            FontSize     = 12,
            FontFamily   = new FontFamily("Consolas, Courier New, monospace"),
            LineHeight   = 18,
            Opacity      = 0.92,
        };

        bool expanded = false;
        var toggleBtn = new Button
        {
            Content             = $"📋  Injection : {ticketKey}  ▶",
            FontSize            = 11,
            FontFamily          = new FontFamily("Consolas, Courier New, monospace"),
            BorderThickness     = new Thickness(0),
            Padding             = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor              = new Cursor(StandardCursorType.Hand),
        };
        UiTheme.StyleLinkButton(toggleBtn, ColThinkLabel);
        ToolTip.SetTip(toggleBtn, $"Click to expand / collapse the fetched JIRA ticket {ticketKey}");

        var contentBorder = new Border
        {
            IsVisible           = false,
            Background          = BgThinking,
            BorderBrush         = ColDivider,
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(6),
            Padding             = new Thickness(12, 8, 12, 8),
            Margin              = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child               = contentTb,
        };

        toggleBtn.Click += (_, _) =>
        {
            expanded                = !expanded;
            contentBorder.IsVisible = expanded;
            toggleBtn.Content       = expanded
                ? $"📋  Injection : {ticketKey}  ▼"
                : $"📋  Injection : {ticketKey}  ▶";
        };

        var wrapper = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin              = new Thickness(4, 2, 4, 2),
        };
        wrapper.Children.Add(toggleBtn);
        wrapper.Children.Add(contentBorder);

        return (wrapper, contentTb);
    }
}
