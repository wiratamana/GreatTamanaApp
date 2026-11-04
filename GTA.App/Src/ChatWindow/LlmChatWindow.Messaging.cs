using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using GTA.Builder;
using GTA.Core;
using GTA.Tools;

namespace GTA.App;

public partial class LlmChatWindow
{
    // ── Fork & Send ───────────────────────────────────────────────────────────

    /// <summary>Handler for the "⑂" Fork &amp; Send button.</summary>
    private void ForkAndSendCurrentMessage()
    {
        var source = _current;
        if (source == null) return;

        if (string.IsNullOrWhiteSpace(_inputBox.Text)) return;

        var forked = CloneSessionForFork(source);
        _sessions.Insert(0, forked);
        var item = AddSessionListItem(forked, prependToTop: true);
        SwitchSession(forked, item);

        ConversationPersistence.SaveSession(forked);

        SendCurrentMessage();
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    private void SendCurrentMessage()
    {
        var session = _current;
        if (session == null) return;

        if (session.IsPaused) return;

        if (session.Runtime.IsProcessing || session.Runtime.TaskCurrentUnitIndex >= 0) return;

        var text = _inputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        _inputBox.Text = string.Empty;

        var attachedImages = new List<string>(_pendingAttachments);
        _pendingAttachments.Clear();
        RefreshAttachmentStrip();

        var agentForThisMessage = _pendingAgentOverride;
        _pendingAgentOverride = null;

        _ = SendTextToSession(session, text, attachedImages, agentForThisMessage);
    }

    private async Task SendTextToSession(
        ChatSession       session,
        string            text,
        List<string>?     attachedImages    = null,
        AgentDefinition?  agentOverride     = null,
        IEnumerable<string>? extraPreloadTools = null,
        bool              autoTitle         = true,
        string?           backendOverride   = null)
    {
        EnsureSessionPanelBuilt(session);
        var panel = session.Runtime.MessagesPanel!;

        if (session.Messages.Count == 0)
            panel.Children.Clear();

        if (session.Messages.Count == 0 && autoTitle)
        {
            session.Title = text.Length > 40 ? text[..40] + "…" : text;

            if (ReferenceEquals(session, _current))
                _headerTitle.Text = SidebarTitle(session);

            if (_sidebarRows.TryGetValue(session.Id, out var rowRefs))
                rowRefs.TitleText.Text = SidebarTitle(session);
        }

        var images = attachedImages ?? new List<string>();

        var userMsg = new ChatMessage
        {
            Role           = "user",
            Content        = text,
            AttachedImages = images,
        };
        session.Messages.Add(userMsg);

        session.IsDirty = true;

        // Write-ahead: the prompt is on disk BEFORE anything else happens (JIRA fetch, backend call, tools).
        // If the app dies right after this, the chat has the prompt and the sidebar shows INTERRUPTED, not DONE.
        session.TurnInProgress = true;
        ConversationPersistence.AppendJournal(session.Id, "USER PROMPT", text);
        ConversationPersistence.SaveSession(session);

        panel.Children.Add(BuildMessageBubble(session, userMsg));

        var jiraKeys = ExtractJiraTicketKeys(text);
        List<(string Key, SelectableTextBlock TextBlock)>? jiraWidgets = null;
        if (jiraKeys.Count > 0)
        {
            jiraWidgets = new List<(string, SelectableTextBlock)>();
            foreach (var (key, _) in jiraKeys)
            {
                var (wrapper, contentTb) = BuildJiraInjectionWidget(key, "Fetching JIRA ticket…");
                panel.Children.Add(wrapper);
                jiraWidgets.Add((key, contentTb));
            }
        }

        if (ReferenceEquals(session, _current))
            _messagesScroll.ScrollToEnd();

        await RunLlmWithJiraInjectionAsync(
            session, userMsg, text, images, agentOverride, jiraKeys, jiraWidgets, extraPreloadTools, backendOverride);
    }

    // ── JIRA fetch + prompt augmentation, then LLM call ──────────────────────

    /// <summary>If jiraKeys is non-empty, fetches each ticket's full content (off the UI thread), updates the already-visib...</summary>
    private async Task RunLlmWithJiraInjectionAsync(
        ChatSession                                      session,
        ChatMessage                                      userMsg,
        string                                            rawUserText,
        List<string>?                                     attachedFiles,
        AgentDefinition?                                  agentOverride,
        List<(string Key, string BaseUrl)>                jiraKeys,
        List<(string Key, SelectableTextBlock TextBlock)>? jiraWidgets,
        IEnumerable<string>?                              extraPreloadTools = null,
        string?                                            backendOverride   = null)
    {
        string promptForLlm = rawUserText;

        if (jiraKeys.Count > 0)
        {
            var injectedBlocks = new List<string>();

            for (int i = 0; i < jiraKeys.Count; i++)
            {
                string key     = jiraKeys[i].Key;
                string baseUrl = jiraKeys[i].BaseUrl;
                string content = await FetchJiraTicketContentAsync(key, baseUrl);

                userMsg.JiraInjections.Add(new DisplayJiraInjection
                {
                    TicketKey = key,
                    Content   = content,
                });

                if (jiraWidgets != null && i < jiraWidgets.Count)
                {
                    var tb = jiraWidgets[i].TextBlock;
                    if (Dispatcher.UIThread.CheckAccess())
                        tb.Text = content;
                    else
                        Dispatcher.UIThread.Post(() => tb.Text = content);
                }

                injectedBlocks.Add(
                    $"───── JIRA Context: {key} ─────\n{content}\n───── End {key} ─────");
            }

            promptForLlm = rawUserText + "\n\n" + string.Join("\n\n", injectedBlocks);
        }

        await RunLlmAsync(session, promptForLlm, attachedFiles, agentOverride, extraPreloadTools, backendOverride);
    }

    // ── LLM call via AgenticLoop ──────────────────────────────────────────────

    private async Task RunLlmAsync(
        ChatSession           session,
        string                userText,
        List<string>?         attachedFiles     = null,
        AgentDefinition?      agentOverride     = null,
        IEnumerable<string>?  extraPreloadTools = null,
        string?               backendOverride   = null)
    {
        var runtime = session.Runtime;
        runtime.Cts = new CancellationTokenSource();
        var ct = runtime.Cts.Token;

        BeginSessionTurn(session);

        runtime.LastTurnHadError = false;

        var sessionHistory = session.ConversationHistory;

        // Resolve the agent: explicit override → UI selection → registry default
        var agent = agentOverride ?? _selectedAgent ?? AgentRegistry.DefaultAgent;

        string backendName = backendOverride ?? _selectedBackend;
        ILlmClient llmClient = backendName.StartsWith("GPT", StringComparison.OrdinalIgnoreCase)
            ? new GptTranslator()
            : backendName.StartsWith("Gemini", StringComparison.OrdinalIgnoreCase)
                ? new GeminiTranslator()
                : new ClaudeClient();

        runtime.CurrentTurnModelName = llmClient.ModelName;

        ConversationPersistence.AppendJournal(session.Id, "TURN START",
            $"backend={backendName} model={llmClient.ModelName} agent={agent.Name} prompt chars={userText.Length}");

        try
        {
#if CO_WORKER_USE
            // Co-worker build: built-in prompt + the additional prompt typed in Settings (empty = nothing added).
            string systemPrompt = AdditionalPromptStore.Apply(agent.SystemPrompt);
            if (!ReferenceEquals(systemPrompt, agent.SystemPrompt))
                AppendLog("[Agent] Additional system prompt from Settings is added.");
#else
            string systemPrompt = agent.SystemPrompt;
#endif
            var settings = new RunSettings(
                Prompt:       userText,
                Search:       AppConfig.DEFAULT_SEARCH,
                SystemPrompt: systemPrompt,
                MaxTokens:    agent.MaxTokens,
                Thinking:     agent.Thinking,
                Effort:       agent.Effort,
                Debug:        AppConfig.DEFAULT_DEBUG,
                Files:        attachedFiles ?? new List<string>(),
                AgentName:    agent.Name,
                EvidenceDir:  ConversationPersistence.EvidenceDir(session.Id)
            );

            var tagSet = new HashSet<string>(agent.ToolTags, StringComparer.OrdinalIgnoreCase);
            var tools  = ToolRegistry.GetSchemaForTags(tagSet);

            var preloadedToolNames = new List<string>(session.Runtime.PreloadedToolNames);
            if (extraPreloadTools != null)
                preloadedToolNames.AddRange(extraPreloadTools);

            if (preloadedToolNames.Count > 0)
            {
                var alreadyPresent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonNode? t in tools)
                {
                    string? n = t?["name"]?.GetValue<string>();
                    if (n != null) alreadyPresent.Add(n);
                }

                foreach (string name in preloadedToolNames)
                {
                    if (alreadyPresent.Contains(name)) continue; // duplicate guard

                    JsonObject? entry = ToolRegistry.TryGetSchemaEntry(name);
                    if (entry == null) continue; // unknown/stale tool name — ignore silently

                    tools.Add(entry);
                    alreadyPresent.Add(name);
                }
            }

            bool isClaudeBackend = llmClient is ClaudeClient;
            if (settings.Search && isClaudeBackend)
                foreach (JsonNode? t in AppConfig.WEB_TOOLS) tools.Add(t!.DeepClone());

            var logger   = new AvaloniaAgentLogger(this, session);
            var renderer = new AvaloniaStreamRenderer(this, session);

            AppendLog($"[Backend] {llmClient.ProviderName} ({llmClient.ModelName})  |  " +
                      $"[Agent] '{agent.Name}'  |  " +
                      $"tools={tools.Count}  thinking={agent.Thinking}/{agent.Effort}  |  " +
                      $"session='{session.Title}'");

            AskQuestionsContext.CurrentSession = session;

            DelegateTaskContext.CurrentSession = session;

            await Task.Run(async () =>
                await AgenticLoop.RunAsync(
                        settings, tools, logger, renderer, sessionHistory, ct, llmClient,
                        injectionQueue: runtime)
                    .ConfigureAwait(false)
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ConversationPersistence.AppendJournal(session.Id, "TURN STOPPED", "Stopped by user (cancellation).");
            AddStatusLine(session, "⏹  Stopped by user");
        }
        catch (Exception ex)
        {
            SessionFileLogger.LogException($"Unhandled error in turn of session '{session.Title}' ({session.Id})", ex);
            ConversationPersistence.AppendJournal(session.Id, "TURN ERROR", ex.ToString());
            AddStatusLine(session, $"✗  Unhandled error: {ex.Message}");
            runtime.LastTurnHadError = true;
        }
        finally
        {
            if (Dispatcher.UIThread.CheckAccess())
                FinishSessionTurn(session);
            else
                Dispatcher.UIThread.Post(() => FinishSessionTurn(session));
        }
    }
}
