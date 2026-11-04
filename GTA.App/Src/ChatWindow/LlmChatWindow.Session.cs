using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GTA.App;

public partial class LlmChatWindow
{
    // ── Create ────────────────────────────────────────────────────────────────

    private void CreateNewChat()
    {
        var session = new ChatSession { Title = $"Chat {_sessions.Count + 1}", IsFullyLoaded = true };
        _sessions.Insert(0, session);
        var item = AddSessionListItem(session, prependToTop: true);
        SwitchSession(session, item);
        SaveSessions();
    }

    /// <summary>Build a brand-new, fully independent ChatSession whose ChatSession.Messages (UI bubbles) and ChatSession.Co...</summary>
    private static ChatSession CloneSessionForFork(ChatSession source)
    {
        var clone = new ChatSession
        {
            Title         = MakeForkTitle(source.Title),
            IsFullyLoaded = true,
        };

        foreach (var msg in source.Messages)
            clone.Messages.Add(CloneChatMessage(msg));

        foreach (var node in source.ConversationHistory)
            clone.ConversationHistory.Add(node?.DeepClone());

        return clone;
    }

    /// <summary>Deep-copy a single <see cref="ChatMessage"/> — see <see cref="CloneSessionForFork"/>.</summary>
    private static ChatMessage CloneChatMessage(ChatMessage src) => new ChatMessage
    {
        Role           = src.Role,
        Content        = src.Content,
        Timestamp      = src.Timestamp,
        AttachedImages = new List<string>(src.AttachedImages),
        ThinkingBlocks = src.ThinkingBlocks
            .Select(t => new DisplayThinkingBlock { Text = t.Text, Timestamp = t.Timestamp, SequenceIndex = t.SequenceIndex })
            .ToList(),
        ToolCalls = src.ToolCalls
            .Select(t => new DisplayToolCall
            {
                ToolName      = t.ToolName,
                InputJson     = t.InputJson,
                Result        = t.Result,
                StartedAt     = t.StartedAt,
                CompletedAt   = t.CompletedAt,
                SequenceIndex = t.SequenceIndex,
            })
            .ToList(),
        TextSegments = src.TextSegments
            .Select(t => new DisplayTextSegment { Text = t.Text, Timestamp = t.Timestamp, SequenceIndex = t.SequenceIndex })
            .ToList(),
        JiraInjections = src.JiraInjections
            .Select(j => new DisplayJiraInjection { TicketKey = j.TicketKey, Content = j.Content })
            .ToList(),
        PromptInjections = src.PromptInjections
            .Select(p => new DisplayPromptInjection
            {
                Text            = p.Text,
                Files           = new List<string>(p.Files),
                ToolNamesToLoad = new List<string>(p.ToolNamesToLoad),
                Timestamp       = p.Timestamp,
                SequenceIndex   = p.SequenceIndex,
            })
            .ToList(),
        Rounds = src.Rounds
            .Select(r => new DisplayRound
            {
                Round          = r.Round,
                SequenceIndex  = r.SequenceIndex,
                Timestamp      = r.Timestamp,
                Usage          = r.Usage,
                CostUsd        = r.CostUsd,
                ElapsedSeconds = r.ElapsedSeconds,
            })
            .ToList(),
        Usage     = src.Usage,
        ModelName = src.ModelName,
        CostUsd   = src.CostUsd,
    };

    /// <summary>Derive a forked session's sidebar title from its source, appending a " (fork)" marker exactly once (re-fork...</summary>
    private static string MakeForkTitle(string sourceTitle) =>
        sourceTitle.EndsWith(" (fork)", StringComparison.Ordinal)
            ? sourceTitle
            : sourceTitle + " (fork)";

    // ── Switch ────────────────────────────────────────────────────────────────

    /// <summary>Make session the ACTIVE/VISIBLE session.</summary>
    private void SwitchSession(ChatSession session, Border listItem)
    {
        // Make sure the row can be seen: open the folds of its Task / parent Sub Agents.
        ExpandAncestorFolds(session);

        if (_activeSessionItem != null)
            SetRowActive(_activeSessionItem, false);

        if (_current != null && !ReferenceEquals(_current, session))
        {
            foreach (var host in _current.Runtime.AttachedImageHosts)
                host.Unload();

            UnloadSessionIfIdle(_current);
        }

        _activeSessionItem  = listItem;
        SetRowActive(listItem, true);
        _current            = session;
        _headerTitle.Text   = SidebarTitle(session);

        EnsureSessionPanelBuilt(session);
        _messagesScroll.Content = session.Runtime.MessagesPanel;

        foreach (var host in session.Runtime.AttachedImageHosts)
            host.Load();

        double restoreY = session.Runtime.LastScrollOffsetY;
        if (restoreY < 0)
        {
            _messagesScroll.ScrollToEnd();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_current, session)) return; // switched again already
                _messagesScroll.Offset = new Vector(_messagesScroll.Offset.X, restoreY);
            }, DispatcherPriority.Loaded);
        }

        if (_toolPreloadPopup != null && !ReferenceEquals(_toolPreloadPopupSession, session))
            _toolPreloadPopup.Close();
        RefreshPreloadToolsButtonLabel();

        RefreshInjectButtonLabel();

        session.Runtime.OpenAskQuestionsWindow?.Activate();

        // The user is now looking at this chat — the "DONE" badge (if any)
        // has served its purpose and disappears permanently.
        ClearDoneBadge(session);

        RefreshChromeForActiveSession();

        _inputBox.Focus();
    }

    // ── Unload (memory fix) ───────────────────────────────────────────────────

    /// <summary>Frees session's ENTIRE in-memory footprint — ChatSession.Messages, ChatSession.ConversationHistory, its bui...</summary>
    private void UnloadSessionIfIdle(ChatSession session)
    {
        if (!session.IsFullyLoaded)          return; // nothing loaded to free
        if (session.Runtime.IsProcessing)    return; // live turn streaming in the background
        if (IsTaskSessionStillRunning(session)) return; // live running-task banner in this panel

        // Last-chance safety net — normally a no-op (see IsDirty).
        ConversationPersistence.SaveSession(session);

        foreach (var host in session.Runtime.AttachedImageHosts)
            host.Unload();
        session.Runtime.AttachedImageHosts.Clear();

        session.Messages.Clear();
        for (int i = session.ConversationHistory.Count - 1; i >= 0; i--)
            session.ConversationHistory.RemoveAt(i);

        session.Runtime.ScheduledCountdownLabel     = null;
        session.Runtime.TaskScheduledCountdownLabel = null;
        session.Runtime.TaskStepBadges              = null;
        session.Runtime.TaskStepRows                = null;
        session.Runtime.TaskStepsTreeRowsPanel       = null;
        session.Runtime.TaskStepsTreeCountLabel      = null;

        session.Runtime.MessagesPanel = null;
        session.Runtime.PanelBuilt    = false;
        session.IsFullyLoaded         = false;
    }

    /// <summary>Build session's persistent message-bubble panel exactly once (lazy-loading the full session data from disk...</summary>
    private void EnsureSessionPanelBuilt(ChatSession session)
    {
        if (session.Runtime.PanelBuilt) return;

        // Lazy-load session data on first activation.
        if (!session.IsFullyLoaded)
            ConversationPersistence.LoadSessionData(session);

        var panel = NewMessagesPanel();

        if (session.Messages.Count == 0)
            panel.Children.Add(BuildSessionPlaceholder(session)); // see LlmChatWindow.Task.cs
        else
            foreach (var msg in session.Messages)
                panel.Children.Add(BuildMessageBubble(session, msg));

        session.Runtime.MessagesPanel = panel;
        session.Runtime.PanelBuilt    = true;
    }

    private readonly Dictionary<string, StackPanel> _taskChildContainers = new();
    private readonly Dictionary<string, Border>     _taskChildWrappers   = new();
    private readonly Dictionary<string, bool>       _taskFoldExpanded    = new();

    /// <summary>Tag of the Border that wraps one nested group of rows (Task steps / Sub Agents). It is never a real row.</summary>
    private static readonly object SubTreeTag = new();

    /// <summary>The controls of ONE sidebar row (see BuildSidebarRowControls).</summary>
    private sealed class SidebarRowControls
    {
        public Border    Item      = null!;
        public TextBlock FoldIcon  = null!;
        public TextBlock TitleText = null!;
        public TextBlock MetaText  = null!;
        public Border    Badge     = null!;
        public TextBlock BadgeText = null!;
    }

    /// <summary>
    /// Builds the visual of one sidebar row — shared by real sessions and by the "waiting" placeholder rows of a Task.
    /// Layout:  [▾]  Title (one line)  [BADGE]   with a small grey "kind · time" line under the title.
    /// The caller still has to set Item.Tag (the session, or null for a placeholder) and put the row in a panel.
    /// </summary>
    private SidebarRowControls BuildSidebarRowControls(string title, string meta, bool isChild)
    {
        var foldIcon = new TextBlock
        {
            Text                = "▾",
            FontSize            = 11,
            Foreground          = ColSideMeta,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsVisible           = false,
            Cursor              = new Cursor(StandardCursorType.Hand),
        };

        var titleText = new TextBlock
        {
            Text             = title,
            FontSize         = isChild ? 12.5 : 13,
            FontWeight       = isChild ? FontWeight.Normal : FontWeight.Medium,
            Foreground       = isChild ? ColSideChild : ColSideTitle,
            TextTrimming     = TextTrimming.CharacterEllipsis,
            TextWrapping     = TextWrapping.NoWrap,
        };

        var metaText = new TextBlock
        {
            Text         = meta,
            FontSize     = 10,
            Foreground   = ColSideMeta,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin       = new Thickness(0, 1, 0, 0),
        };

        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children          = { titleText, metaText },
        };

        // ── Badge (see LlmChatWindow.SidebarStatus.cs) ──
        var badgeText = new TextBlock
        {
            FontWeight          = FontWeight.Bold,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var badge = new Border
        {
            IsVisible           = false,
            CornerRadius        = new CornerRadius(9),
            BorderThickness     = new Thickness(1),
            BorderBrush         = Brushes.Transparent,
            Padding             = new Thickness(7, 1, 7, 1),
            Margin              = new Thickness(6, 0, 0, 0),
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child               = badgeText,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(16, GridUnitType.Pixel)); // fold glyph: fixed width keeps titles aligned
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(foldIcon,  0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(badge,     2);
        grid.Children.Add(foldIcon);
        grid.Children.Add(textStack);
        grid.Children.Add(badge);

        var item = new Border
        {
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(3, 6, 8, 6),
            Margin          = new Thickness(0, 1, 0, 1),
            BorderThickness = new Thickness(3, 0, 0, 0), // left edge: accent bar of the ACTIVE row (see SetRowActive)
            BorderBrush     = Brushes.Transparent,
            Cursor          = new Cursor(StandardCursorType.Hand),
            Child           = grid,
            Tag             = null,
        };

        item.PointerEntered += (_, _) =>
        {
            if (item != _activeSessionItem) item.Background = BgHoverItem;
        };
        item.PointerExited += (_, _) =>
        {
            if (item != _activeSessionItem) item.Background = null;
        };
        item.PointerPressed += (_, _) =>
        {
            if (item.Tag is ChatSession s) SwitchSession(s, item);
        };
        foldIcon.PointerPressed += (_, e) =>
        {
            e.Handled = true; // do not also open the session
            if (item.Tag is ChatSession s) ToggleTaskFold(s);
        };

        return new SidebarRowControls
        {
            Item      = item,
            FoldIcon  = foldIcon,
            TitleText = titleText,
            MetaText  = metaText,
            Badge     = badge,
            BadgeText = badgeText,
        };
    }

    /// <summary>
    /// Builds one sidebar row for session. With parent set, the row is nested under that session's row (Task step
    /// under its Task, Sub Agent under the session that dispatched it); otherwise it is a top-level row.
    /// </summary>
    private Border AddSessionListItem(
        ChatSession session, bool prependToTop = false, ChatSession? parent = null,
        int? childInsertIndex = null)
    {
        bool isChild = parent != null;

        var row  = BuildSidebarRowControls(SidebarTitle(session), SidebarMeta(session), isChild);
        var item = row.Item;
        item.Tag = session;
        ToolTip.SetTip(item, SidebarTitle(session));

        if (isChild)
        {
            var childContainer = GetOrCreateChildContainer(parent!);
            if (childInsertIndex.HasValue &&
                childInsertIndex.Value >= 0 &&
                childInsertIndex.Value <= childContainer.Children.Count)
                childContainer.Children.Insert(childInsertIndex.Value, item);
            else
                childContainer.Children.Add(item); // default: appended in call order (Task 1, Task 2, … as they fire)
        }
        else if (prependToTop)
        {
            _sessionListPanel.Children.Insert(0, item);
        }
        else
        {
            _sessionListPanel.Children.Add(item);
        }

        _sidebarRows[session.Id] = new SidebarRowRefs
        {
            Item      = item,
            TitleText = row.TitleText,
            TimeText  = row.MetaText,
            Badge     = row.Badge,
            BadgeText = row.BadgeText,
            FoldIcon  = row.FoldIcon,
        };
        RefreshSidebarBadge(session);

        return item;
    }

    /// <summary>
    /// Returns (creating on first use) the nested StackPanel that holds parent's CHILD rows. The group sits right
    /// below parent's own row, in whatever panel that row lives in — so it works at any depth
    /// (Task → Task step → Sub Agent → Sub Agent …). A thin vertical guide line on its left shows the nesting.
    /// </summary>
    private StackPanel GetOrCreateChildContainer(ChatSession parent)
    {
        if (_taskChildContainers.TryGetValue(parent.Id, out var existing))
            return existing;

        var childStack = new StackPanel { Spacing = 1 };
        var wrapper    = new Border
        {
            Tag             = SubTreeTag,
            IsVisible       = false,
            BorderBrush     = ColSideGuide,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Margin          = new Thickness(14, 0, 0, 3),
            Padding         = new Thickness(3, 0, 0, 0),
            Child           = childStack,
        };

        _taskChildContainers[parent.Id] = childStack;
        _taskChildWrappers[parent.Id]   = wrapper;
        _taskFoldExpanded[parent.Id]    = true; // expanded by default

        if (_sidebarRows.TryGetValue(parent.Id, out var parentRefs) &&
            parentRefs.Item.Parent is Panel host)
        {
            int idx = host.Children.IndexOf(parentRefs.Item);
            host.Children.Insert(idx >= 0 ? idx + 1 : host.Children.Count, wrapper);
        }
        else
        {
            // Safety net — should not normally happen (the parent's own row
            // is always added before any of its children can exist).
            _sessionListPanel.Children.Add(wrapper);
        }

        return childStack;
    }

    /// <summary>Registers child as one more nested row under parent's foldable tree entry — called once per "Prompt" step of a Task.</summary>
    private void AddChildSessionRow(ChatSession parent, ChatSession child)
    {
        AddSessionListItem(child, prependToTop: false, parent: parent);
        RefreshTaskFoldVisibility(parent);
    }

    /// <summary>Flips parentTask's fold state (expanded ⇄ collapsed) — the sidebar row's "▾/▸" glyph click handler.</summary>
    private void ToggleTaskFold(ChatSession parentTask)
    {
        if (!_taskChildContainers.ContainsKey(parentTask.Id)) return;

        bool expanded = _taskFoldExpanded.TryGetValue(parentTask.Id, out var e) && e;
        _taskFoldExpanded[parentTask.Id] = !expanded;
        RefreshTaskFoldVisibility(parentTask);
    }

    /// <summary>Repaints the fold glyph ("▾" expanded / "▸" collapsed, hidden entirely while there are still zero children)...</summary>
    private void RefreshTaskFoldVisibility(ChatSession parentTask)
    {
        if (!_taskChildWrappers.TryGetValue(parentTask.Id, out var wrapper)) return;
        if (!_taskChildContainers.TryGetValue(parentTask.Id, out var stack)) return;

        bool hasChildren = stack.Children.Count > 0;
        bool expanded    = _taskFoldExpanded.TryGetValue(parentTask.Id, out var e) && e;

        wrapper.IsVisible = hasChildren && expanded;

        if (_sidebarRows.TryGetValue(parentTask.Id, out var refs) && refs.FoldIcon != null)
        {
            refs.FoldIcon.IsVisible = hasChildren;
            refs.FoldIcon.Text      = expanded ? "▾" : "▸";
        }
    }

    /// <summary>Sidebar-row registry key for a step that has no real child session yet.</summary>
    private static string TaskUnitPlaceholderKey(string parentId, int index) => $"{parentId}::taskunit{index}";

    /// <summary>Adds one "⏳ WAITING" placeholder row for unit (a Prompt-kind TaskUnit that has never started running) into...</summary>
    private (Border Item, TextBlock TitleText, TextBlock BadgeText, Border Badge) AddTaskUnitPlaceholderRow(
        ChatSession parentTask, TaskUnit unit, int index, int? insertAtVisualIndex = null)
    {
        var row = BuildSidebarRowControls(PlaceholderRowTitle(unit, index), "Not started yet", isChild: true);
        var item = row.Item;

        // Waiting steps stay quieter than real chats, but still readable.
        row.TitleText.Foreground = ColSideMeta;

        item.Tag = null; // no session yet — the shared click handler (see BuildSidebarRowControls) ignores it until the step really starts

        var childContainer = GetOrCreateChildContainer(parentTask);
        if (insertAtVisualIndex.HasValue)
            childContainer.Children.Insert(ToRealChildIndex(childContainer, insertAtVisualIndex.Value), item);
        else
            childContainer.Children.Add(item);

        SetSidebarTaskBadgeState(row.Badge, row.BadgeText, TaskRowBadgeState.Waiting);

        var refs = new SidebarRowRefs
        {
            Item      = item,
            TitleText = row.TitleText,
            TimeText  = row.MetaText,
            Badge     = row.Badge,
            BadgeText = row.BadgeText,
            FoldIcon  = row.FoldIcon,
        };
        _sidebarRows[TaskUnitPlaceholderKey(parentTask.Id, index)] = refs;

        var editMenu = BuildTaskUnitPlaceholderMenu(parentTask, unit, index, row.TitleText);
        item.ContextMenu = editMenu;
        ToolTip.SetTip(item, "Click to edit this step's prompt or pre-loaded tools before it runs");
        item.PointerPressed += (_, _) =>
        {
            if (item.Tag == null) editMenu.Open(item);
        };

        RefreshTaskFoldVisibility(parentTask);

        return (item, row.TitleText, row.BadgeText, row.Badge);
    }

    /// <summary>One-line sidebar title of a not-yet-started step (task prompts can hold line breaks and long runs of spaces).</summary>
    private static string PlaceholderRowTitle(TaskUnit unit, int index) =>
        WhitespaceRun.Replace(BuildTaskUnitPlaceholderTitle(unit, index), " ").Trim();

    /// <summary>
    /// Converts "the n-th real row" of a nested container into a Children index. A container can also hold the
    /// nested sub-tree of one of its rows (a Sub Agent group); those are skipped when counting.
    /// </summary>
    private static int ToRealChildIndex(StackPanel container, int rowNumber)
    {
        int seen = 0;
        for (int i = 0; i < container.Children.Count; i++)
        {
            if (container.Children[i] is Border b && ReferenceEquals(b.Tag, SubTreeTag)) continue;
            if (seen == rowNumber) return i;
            seen++;
        }
        return container.Children.Count;
    }

    /// <summary>"Task N: …" for a normal top-level step, or "Sub-Task N: …" for a step spliced in mid-run via "insert next"...</summary>
    private static string BuildTaskUnitPlaceholderTitle(TaskUnit unit, int index) =>
        unit.SubTaskNumber > 0
            ? $"Sub-Task {unit.SubTaskNumber}: {TruncateOneLine(unit.PromptText, 40)}"
            : unit.RestartLabelIndex >= 0
                ? $"Task {unit.RestartLabelIndex + 1}: {TruncateOneLine(unit.PromptText, 40)}"
                : $"Task {index + 1}: {TruncateOneLine(unit.PromptText, 40)}";

    /// <summary>Builds the 2-option popup menu ("✎ Edit Prompt" / "🧩 Modify Preloaded Tools") for one still-WAITING per-ste...</summary>
    private ContextMenu BuildTaskUnitPlaceholderMenu(
        ChatSession parentTask, TaskUnit unit, int index, TextBlock titleText)
    {
        var menu = new ContextMenu();

        var editPromptItem = new MenuItem { Header = "✎  Edit Prompt" };
        editPromptItem.Click += async (_, _) =>
            await EditWaitingTaskUnitPromptAsync(parentTask, unit, index, titleText);

        var editToolsItem = new MenuItem { Header = "🧩  Modify Preloaded Tools" };
        editToolsItem.Click += async (_, _) =>
            await EditWaitingTaskUnitToolsAsync(parentTask, unit, index);

        menu.Items.Add(editPromptItem);
        menu.Items.Add(editToolsItem);

        return menu;
    }

    /// <summary>"✎ Edit Prompt" — reopens this ONE step's own TaskUnitEditWindow (the same widget TaskBuilderWindow uses),...</summary>
    private async Task EditWaitingTaskUnitPromptAsync(
        ChatSession parentTask, TaskUnit unit, int index, TextBlock titleText)
    {
        var win = new TaskUnitEditWindow(unit.Kind, unit);
        await win.ShowDialog(this);

        if (win.Result == null) return; // cancelled — nothing changed

        titleText.Text = PlaceholderRowTitle(unit, index);
        ConversationPersistence.SaveSession(parentTask);
    }

    /// <summary>"🧩 Modify Preloaded Tools" — the same ToolPreloadPopupWindow popup used everywhere else in the app, backed...</summary>
    private async Task EditWaitingTaskUnitToolsAsync(ChatSession parentTask, TaskUnit unit, int index)
    {
        var toolSet = new HashSet<string>(unit.PreloadedToolNames, StringComparer.OrdinalIgnoreCase);
        var popup   = new ToolPreloadPopupWindow(
            toolSet, null,
            unit.SubTaskNumber > 0 ? $"Sub-Task {unit.SubTaskNumber}" : $"Task {index + 1}");
        await popup.ShowDialog(this);

        unit.PreloadedToolNames = toolSet.ToList();
        ConversationPersistence.SaveSession(parentTask);
    }

    /// <summary>Called the instant a step's real child ChatSession is created (see LlmChatWindow.Task.cs — CreateChildTaskS...</summary>
    private void UpgradeTaskPlaceholderToChildSession(
        ChatSession parentTask, ChatSession child, int index, string? previousChildSessionId = null)
    {
        string placeholderKey = TaskUnitPlaceholderKey(parentTask.Id, index);

        SidebarRowRefs? refs;
        string keyToRemove;

        if (_sidebarRows.TryGetValue(placeholderKey, out refs))
        {
            keyToRemove = placeholderKey;
        }
        else if (previousChildSessionId != null && _sidebarRows.TryGetValue(previousChildSessionId, out refs))
        {
            keyToRemove = previousChildSessionId;
        }
        else
        {
            AddChildSessionRow(parentTask, child);
            return;
        }

        _sidebarRows.Remove(keyToRemove);

        refs!.Item.Tag    = child;
        refs.Item.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(refs.Item, "Click to open this step's own chat");
        refs.Item.ContextMenu = null;
        refs.TitleText.Text = SidebarTitle(child);
        refs.TimeText.Text  = SidebarMeta(child);
        refs.TitleText.Foreground = ColSideChild;

        _sidebarRows[child.Id] = refs;

        SetSidebarTaskBadgeState(refs.Badge, refs.BadgeText, TaskRowBadgeState.Running);

        var badges = parentTask.Runtime.SidebarStepBadges;
        var rows   = parentTask.Runtime.SidebarStepRows;
        if (badges != null && index < badges.Count) badges[index] = refs.BadgeText;
        if (rows   != null && index < rows.Count)   rows[index]   = refs.Item;
    }

    /// <summary>Ensures every Prompt-kind unit in parentTask's blueprint has its own sidebar row — a real (clickable) row f...</summary>
    private void EnsureTaskChildPlaceholderRows(ChatSession parentTask)
    {
        var badges = parentTask.Runtime.SidebarStepBadges ?? new List<TextBlock?>();
        var rows   = parentTask.Runtime.SidebarStepRows   ?? new List<Border?>();

        for (int i = badges.Count; i < parentTask.TaskUnits.Count; i++)
        {
            var unit = parentTask.TaskUnits[i];

            if (unit.Kind != TaskUnitKind.Prompt)
            {
                badges.Add(null);
                rows.Add(null);
                continue;
            }

            if (!string.IsNullOrEmpty(unit.ChildSessionId))
            {
                var existingChild = _sessions.FirstOrDefault(s => s.Id == unit.ChildSessionId);
                if (existingChild != null)
                {
                    if (!_sidebarRows.ContainsKey(existingChild.Id))
                        AddChildSessionRow(parentTask, existingChild);

                    if (_sidebarRows.TryGetValue(existingChild.Id, out var er))
                    {
                        badges.Add(er.BadgeText);
                        rows.Add(er.Item);
                        RefreshSidebarBadge(existingChild); // stamps RUNNING/DONE correctly
                    }
                    else
                    {
                        badges.Add(null);
                        rows.Add(null);
                    }
                    continue;
                }
            }

            var (item, _, badgeText, _) = AddTaskUnitPlaceholderRow(parentTask, unit, i);
            badges.Add(badgeText);
            rows.Add(item);
        }

        parentTask.Runtime.SidebarStepBadges = badges;
        parentTask.Runtime.SidebarStepRows   = rows;
    }

    /// <summary>Splices ONE brand-new sidebar placeholder row into parentTask's nested tree at its correct position — used...</summary>
    private (Border Item, TextBlock TitleText, TextBlock BadgeText, Border Badge) InsertTaskUnitPlaceholderRowAt(
        ChatSession parentTask, TaskUnit unit, int insertIndex)
    {
        int newCount = parentTask.TaskUnits.Count; // already includes `unit`, spliced in by the caller

        for (int i = newCount - 2; i >= insertIndex; i--)
        {
            string oldKey = TaskUnitPlaceholderKey(parentTask.Id, i);
            if (_sidebarRows.TryGetValue(oldKey, out var refs))
            {
                _sidebarRows.Remove(oldKey);
                _sidebarRows[TaskUnitPlaceholderKey(parentTask.Id, i + 1)] = refs;
            }
        }

        int visualPos = 0;
        for (int k = 0; k < insertIndex; k++)
            if (parentTask.TaskUnits[k].Kind == TaskUnitKind.Prompt) visualPos++;

        var built = AddTaskUnitPlaceholderRow(parentTask, unit, insertIndex, insertAtVisualIndex: visualPos);

        var badges = parentTask.Runtime.SidebarStepBadges;
        var rows   = parentTask.Runtime.SidebarStepRows;
        if (badges != null)
        {
            if (insertIndex >= badges.Count) badges.Add(built.BadgeText);
            else badges.Insert(insertIndex, built.BadgeText);
        }
        if (rows != null)
        {
            if (insertIndex >= rows.Count) rows.Add(built.Item);
            else rows.Insert(insertIndex, built.Item);
        }

        return built;
    }

    /// <summary>Drops every still-WAITING placeholder row (a unit with no real TaskUnit.ChildSessionId yet) from parentTask...</summary>
    private void RebuildTaskChildPlaceholderRows(ChatSession parentTask)
    {
        if (_taskChildContainers.TryGetValue(parentTask.Id, out var container))
        {
            for (int i = container.Children.Count - 1; i >= 0; i--)
            {
                if (container.Children[i] is Border row && row.Tag == null)
                    container.Children.RemoveAt(i);
            }
        }

        foreach (var key in _sidebarRows.Keys
                     .Where(k => k.StartsWith(parentTask.Id + "::taskunit", StringComparison.Ordinal))
                     .ToList())
        {
            _sidebarRows.Remove(key);
        }

        parentTask.Runtime.SidebarStepBadges = null;
        parentTask.Runtime.SidebarStepRows   = null;

        EnsureTaskChildPlaceholderRows(parentTask);
    }

    /// <summary>Called right after a session's ChatSession.LastModifiedAt has just been refreshed on disk (see FinishSessio...</summary>
    private void BumpSessionToTopAndRefreshTime(ChatSession session)
    {
        bool hasRow = _sidebarRows.TryGetValue(session.Id, out var refs);

        if (hasRow)
            refs!.TimeText.Text = SidebarMeta(session);

        // Reorder the in-memory list — most-recently-updated first.
        int idx = _sessions.IndexOf(session);
        if (idx > 0)
        {
            _sessions.RemoveAt(idx);
            _sessions.Insert(0, session);
        }

        // Mirror the same reordering in the visible sidebar list (top-level rows only — nested rows keep their place).
        if (hasRow)
        {
            int childIdx = _sessionListPanel.Children.IndexOf(refs!.Item);
            if (childIdx > 0)
            {
                _sessionListPanel.Children.RemoveAt(childIdx);
                _sessionListPanel.Children.Insert(0, refs.Item);

                // The nested rows (Task steps / Sub Agents) travel together with their parent row.
                if (_taskChildWrappers.TryGetValue(session.Id, out var subTree) &&
                    _sessionListPanel.Children.Remove(subTree))
                {
                    _sessionListPanel.Children.Insert(1, subTree);
                }
            }
        }
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    /// <summary>Attempt to restore sessions from the previous run via ConversationPersistence.LoadSessionSummaries.</summary>
    private void LoadSavedSessions()
    {
        // Migrate legacy single-file format in the background (one-time).
        if (ConversationPersistence.LegacyFileNeedsMigration)
        {
            _ = Task.Run(() =>
            ConversationPersistence.MigrateLegacyFileIfNeeded(msg => AppendLog(msg)));
        }

        var stubs = ConversationPersistence.LoadSessionSummaries();

        if (stubs.Count == 0)
        {
            CreateNewChat();
            return;
        }

        foreach (var session in stubs)
            _sessions.Add(session);

        // Sub Agent sessions saved by an older build have no parent link on disk — find their dispatcher once.
        ConversationPersistence.LinkLegacySubAgentSessions(_sessions);

        var childIdsConsumed = new HashSet<string>();

        // Pass 1: top-level sessions (+ every Task with its own step rows).
        foreach (var session in _sessions)
        {
            if (session.ParentTaskId != null) continue;    // handled below (nested or orphan)
            if (session.ParentSessionId != null) continue; // Sub Agent — nested in pass 3

            AddSessionListItem(session, prependToTop: false);

            if (!session.IsTask) continue;

            EnsureTaskChildPlaceholderRows(session);

            foreach (var unit in session.TaskUnits)
                if (!string.IsNullOrEmpty(unit.ChildSessionId))
                    childIdsConsumed.Add(unit.ChildSessionId);
        }

        // Pass 2: Task steps whose Task is gone (or no longer lists them) — shown as loose rows.
        foreach (var session in _sessions)
        {
            if (session.ParentTaskId == null) continue;
            if (childIdsConsumed.Contains(session.Id)) continue;

            AddSessionListItem(session, prependToTop: false);
        }

        // Pass 3: Sub Agents, nested under the session that dispatched them (also under another Sub Agent).
        // A Sub Agent can only be placed once its parent row exists, so repeat until nothing more can be placed.
        var pendingSubAgents = _sessions
            .Where(s => s.ParentSessionId != null)
            .OrderBy(s => s.CreatedAt)   // oldest first → "Sub Agent #1, #2, …" read top to bottom
            .ToList();

        bool placedAny = true;
        while (pendingSubAgents.Count > 0 && placedAny)
        {
            placedAny = false;
            foreach (var sub in pendingSubAgents.ToList())
            {
                var parent = _sessions.Find(p => p.Id == sub.ParentSessionId);
                if (parent == null || !_sidebarRows.ContainsKey(parent.Id)) continue;

                AddSessionListItem(sub, parent: parent);
                RefreshTaskFoldVisibility(parent);
                pendingSubAgents.Remove(sub);
                placedAny = true;
            }
        }

        // Whatever is left lost its parent (deleted session) — keep it reachable as a loose row.
        foreach (var orphan in pendingSubAgents)
            AddSessionListItem(orphan, prependToTop: false);

        // Activate the most-recent session (lazy-loads full data in SwitchSession).
        if (_sessions.Count > 0 &&
            _sidebarRows.TryGetValue(_sessions[0].Id, out var firstRefs))
        {
            SwitchSession(_sessions[0], firstRefs.Item);
        }
    }

    /// <summary>
    /// Opens every fold above session (its Sub Agent parents and its Task) so its sidebar row is actually visible.
    /// Called before a session is switched to — e.g. by "Open session" on a Sub Agent report.
    /// </summary>
    private void ExpandAncestorFolds(ChatSession session)
    {
        var seen = new HashSet<string>();
        var cur  = session;

        while (cur != null)
        {
            string? parentId = cur.ParentSessionId ?? cur.ParentTaskId;
            if (string.IsNullOrEmpty(parentId) || !seen.Add(parentId)) break;

            var parent = _sessions.Find(s => s.Id == parentId);
            if (parent == null) break;

            if (_taskChildContainers.ContainsKey(parent.Id))
            {
                _taskFoldExpanded[parent.Id] = true;
                RefreshTaskFoldVisibility(parent);
            }

            cur = parent;
        }
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    /// <summary>Persist the currently active session to its own folder on disk.</summary>
    public void SaveSessions()
    {
        if (_current != null)
            ConversationPersistence.SaveSession(_current);
    }

    /// <summary>Called on application shutdown (see App.cs ShutdownRequested): cancels every session's in-flight turn (if a...</summary>
    public void ShutdownAllSessions()
    {
        foreach (var session in _sessions)
        {
            try { session.Runtime.Cts?.Cancel(); }
            catch { /* already cancelled/disposed — ignore */ }

            // Never loaded this run → nothing changed → don't touch its file.
            if (!session.IsFullyLoaded) continue;

            ConversationPersistence.SaveSession(session);
        }
    }

    // ── Time formatting ───────────────────────────────────────────────────────

    /// <summary>Format a timestamp for the sidebar chip: • today → "HH:mm" • this year → "MMM d" • older → "MM/dd/yy" Alway...</summary>
    private static string FormatSessionTime(DateTimeOffset ts)
    {
        var now = DateTimeOffset.Now;
        if (ts.Date == now.Date) return ts.ToString("HH:mm");
        if (ts.Year == now.Year) return ts.ToString("MMM d");
        return ts.ToString("MM/dd/yy");
    }

    // ── Welcome placeholder ───────────────────────────────────────────────────

    /// <summary>Build the empty-session welcome screen shown in the main area when a new chat has no messages yet.</summary>
    private Control BuildWelcomePlaceholder()
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Margin              = new Thickness(0, 60, 0, 0),
            Spacing             = 10,
        };

        panel.Children.Add(new TextBlock
        {
            Text                = "⬡",
            FontSize            = 52,
            Foreground          = ColAccent,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        panel.Children.Add(new TextBlock
        {
            Text                = "How can I help you today?",
            FontSize            = 24,
            FontWeight          = FontWeight.SemiBold,
            Foreground          = ColText,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        panel.Children.Add(new TextBlock
        {
            Text                = "Type a message below to start the conversation.",
            FontSize            = 14,
            Foreground          = ColMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var chipPanel = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 12, 0, 0),
        };

        string[] suggestions =
        {
            "📝  Summarize text",
            "💡  Brainstorm ideas",
            "🔍  Explain a concept",
            "💻  Write some code",
        };

        foreach (var s in suggestions)
        {
            var chip = new Button
            {
                Content    = s,
                FontSize   = 12,
                Margin     = new Thickness(4),
                Padding    = new Thickness(12, 6, 12, 6),
            };
            UiTheme.StyleButton(chip, BgAiBubble, ColText, hoverBackground: BgHoverItem, pressedBackground: BgActiveItem);
            chip.Click += (_, _) =>
            {
                _inputBox.Text = s[s.IndexOf(' ')..].TrimStart();
                _inputBox.Focus();
            };
            chipPanel.Children.Add(chip);
        }

        panel.Children.Add(chipPanel);
        return panel;
    }
}
