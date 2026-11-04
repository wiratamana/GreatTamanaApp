using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GTA.Core;

namespace GTA.App;

public partial class LlmChatWindow
{
    // ── Popup wiring ──────────────────────────────────────────────────────

    /// <summary>Opens the "💉 Inject" popup for whichever session is currently active.</summary>
    private async void OpenInjectionWindow()
    {
        var session = _current;
        if (session == null) return;

        session.Runtime.Pause();
        RefreshInjectButtonLabel();

        try
        {
            var popup = new PromptInjectionWindow(
                session.Title,
                injection => EnqueueInjectionForSession(session, injection));

            await popup.ShowDialog(this);
        }
        finally
        {
            session.Runtime.Resume();
            RefreshInjectButtonLabel();
        }
    }

    /// <summary>Queue a submitted injection onto session's own ChatSessionRuntime</summary>
    private void EnqueueInjectionForSession(ChatSession session, PendingInjection injection)
    {
        session.Runtime.EnqueueInjection(injection);
        RefreshInjectButtonLabel();
    }

    /// <summary>Updates the "💉 Inject (N)" header button label for the active session.</summary>
    internal void RefreshInjectButtonLabel()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshInjectButtonLabel);
            return;
        }

        if (_injectButton == null) return;

        var runtime = _current?.Runtime;
        int  count  = runtime?.PendingInjectionCount ?? 0;
        bool paused = runtime?.IsPaused ?? false;

        string label = count > 0 ? $"💉 Inject ({count})" : "💉 Inject";
        if (paused) label += " ⏸";
        _injectButton.Content = label;
    }

    // ── Live rendering (called by AvaloniaAgentLogger.LogInjection) ──────────

    public void AddInjectionWidget(ChatSession session, PendingInjection injection)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddInjectionWidget(session, injection));
            return;
        }

        var runtime = session.Runtime;

        StackPanel targetPanel = RoundTarget(runtime, runtime.CurrentTurnContentPanel ?? BeginTurnBubble(session));

        var display = new DisplayPromptInjection
        {
            Text            = injection.Text,
            Files           = new List<string>(injection.Files),
            ToolNamesToLoad = new List<string>(injection.ToolNamesToLoad),
            SequenceIndex   = runtime.CurrentTurnSequence++,
        };
        runtime.CurrentTurnMessage?.PromptInjections.Add(display);

        targetPanel.Children.Add(BuildPromptInjectionWidget(display));

        if (ReferenceEquals(session, _current))
            RefreshInjectButtonLabel();

        ScrollToBottom(session);
    }

    // ── Shared widget builder (LIVE + static session-reload reconstruction) ──

    /// <summary>Build one foldable "💉 message injection from external source" widget</summary>
    internal static Control BuildPromptInjectionWidget(DisplayPromptInjection injection)
    {
        var contentTb = new SelectableTextBlock
        {
            Text         = BuildInjectionDisplayText(injection),
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
            Content             = "💉  Message injection from external source  ▶",
            FontSize            = 11,
            FontFamily          = new FontFamily("Consolas, Courier New, monospace"),
            BorderThickness     = new Thickness(0),
            Padding             = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor              = new Cursor(StandardCursorType.Hand),
        };
        UiTheme.StyleLinkButton(toggleBtn, ColThinkLabel);
        ToolTip.SetTip(toggleBtn,
            "Text (and/or attached files / pre-loaded tools) the user injected into this " +
            "running turn via the 💉 Inject popup, from outside this conversation");

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
                ? "💉  Message injection from external source  ▼"
                : "💉  Message injection from external source  ▶";
        };

        var wrapper = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin              = new Thickness(4, 2, 4, 2),
        };
        wrapper.Children.Add(toggleBtn);
        wrapper.Children.Add(contentBorder);

        return wrapper;
    }

    private static string BuildInjectionDisplayText(DisplayPromptInjection injection)
    {
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(injection.Text) ? "(no text — see attachment(s)/tool(s) below)" : injection.Text);

        if (injection.Files.Count > 0)
            sb.Append("\n\n📎 Attached file(s): ")
              .Append(string.Join(", ", injection.Files.Select(Path.GetFileName)));

        if (injection.ToolNamesToLoad.Count > 0)
            sb.Append("\n\n🧩 Requested tool(s): ")
              .Append(string.Join(", ", injection.ToolNamesToLoad));

        return sb.ToString();
    }
}
