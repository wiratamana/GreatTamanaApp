using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GTA.Core;
using GTA.Tools;

namespace GTA.Builder
{
    public static class AgenticLoop
    {
        public static async Task RunAsync(
            RunSettings          settings,
            JsonArray            tools,
            IAgentLogger         logger,
            IStreamRenderer      renderer,
            JsonArray?           conversationHistory = null,
            CancellationToken    ct                  = default,
            ILlmClient?          llmClient           = null,
            IPromptInjectionQueue? injectionQueue     = null)
        {
            ILlmClient client = llmClient ?? new ClaudeClient();

            string effectiveSystemPrompt = BuildEnrichedSystemPrompt(settings.SystemPrompt);

            var messages = conversationHistory ?? new JsonArray();

            if (settings.Files.Count == 0)
                messages.Add(MessageBuilder.MakeUserMessage(settings.Prompt));
            else
                messages.Add(MessageBuilder.MakeUserMessageWithFiles(
                    settings.Prompt, settings.Files, logger));

            ToolRegistry.ActiveTools = tools;
            var sessionUsage = new TokenUsage();   // accumulated across all rounds

            bool reachedNaturalStop = false;

            int contextLengthRollbackCount = 0;
            const int MaxContextLengthRollbacks = 5;

            for (int round = 1; round <= AppConfig.MAX_AGENTIC_ROUNDS; round++)
            {
                ct.ThrowIfCancellationRequested();

                if (injectionQueue != null)
                    await WaitWhilePausedAsync(logger, ct, injectionQueue);

                if (injectionQueue != null)
                {
                    List<PendingInjection> injections = injectionQueue.DrainPending();
                    if (injections.Count > 0)
                        ApplyPendingInjections(messages, tools, injections, logger);
                }

                logger.LogRound(round);

                int effectiveMaxTokens = settings.MaxTokens;

                JsonObject? resp = await client.CallAsync(
                    messages,
                    tools,
                    effectiveSystemPrompt,
                    effectiveMaxTokens,
                    settings.Thinking,
                    settings.Effort,
                    settings.Debug,
                    logger,
                    renderer,
                    ct);

                if (resp == null)
                {
                    if (logger.LastCallExceededContextLength
                        && contextLengthRollbackCount < MaxContextLengthRollbacks
                        && RollBackBloatingRound(messages, logger,
                               contextLengthRollbackCount + 1, MaxContextLengthRollbacks))
                    {
                        contextLengthRollbackCount++;
                        logger.LastCallExceededContextLength = false; // consumed
                        continue; // retry immediately with the rolled-back history
                    }

                    if (logger.LastCallExceededContextLength)
                    {
                        logger.LogError(contextLengthRollbackCount >= MaxContextLengthRollbacks
                            ? $"Context-length rollback budget exhausted " +
                              $"({MaxContextLengthRollbacks} attempt(s)) — the conversation keeps " +
                              "re-bloating past the model's context window even after rolling back " +
                              "each time. Giving up this turn."
                            : "The very first turn's prompt/attachments alone already exceed the " +
                              "model's context window — there is no previous round to roll back " +
                              "to. Reduce the prompt size or attached file(s) and try again.");
                    }
                    return;
                }

                if (round > 1)
                    RedactProcessedImages(messages, settings.EvidenceDir);

                TokenUsage roundUsage = ReadUsage(resp);
                sessionUsage += roundUsage;
                logger.LogTokenUsage(roundUsage);

                string    stopReason = resp["stop_reason"]?.GetValue<string>() ?? "unknown";
                JsonArray blocks     = resp["content"]?.AsArray() ?? new JsonArray();

                messages.Add(new JsonObject
                {
                    ["role"]    = "assistant",
                    ["content"] = blocks.DeepClone(),
                });

                if (stopReason != "tool_use")
                {
                    logger.LogStopReason(stopReason);
                    if (round > 1)
                        logger.LogTokenUsage(sessionUsage, isFinal: true);
                    reachedNaturalStop = true;
                    break;
                }

                using IDisposable ledgerScope = FileViewLedger.BeginScope();
                var toolResults = new JsonArray();

                foreach (JsonNode? block in blocks)
                {
                    ct.ThrowIfCancellationRequested();

                    if (block?["type"]?.GetValue<string>() != "tool_use") continue;

                    string id    = block["id"]!.GetValue<string>();
                    string name  = block["name"]!.GetValue<string>();
                    var    input = block["input"]!.AsObject();

                    string inputJson = input.ToJsonString();
                    logger.LogToolCallFull(name, id, inputJson); // write-ahead: before anything runs
                    logger.LogToolCall(name, id, inputJson[..Math.Min(200, inputJson.Length)]);

                    if (string.Equals(name, "dispatch_sub_agent", StringComparison.OrdinalIgnoreCase))
                    {
                        string subTask = input["task"]?.GetValue<string>() ?? string.Empty;

                        bool allowUseShell     = GetOptionalBoolInput(input, "allow_use_shell", false);
                        bool allowFileMutation = GetOptionalBoolInput(input, "allow_file_mutation", false);

                        (IAgentLogger subLogger, IStreamRenderer subRenderer) =
                            logger.BeginSubAgentDispatch(subTask, client.ModelName, renderer);

                        logger.LogInfo(
                            "🤖 Dispatched a Sub Agent to work on a task. This chat waits for its report. " +
                            "A pause (⏸ Pause or the 💉 Inject window's hold) is respected: the Sub Agent " +
                            "holds before its next round, and this chat holds before ITS next round.");

                        (bool subSuccess, string subMessage) = await RunDispatchedSubAgentAsync(
                            messages, tools, subTask, client, settings, effectiveSystemPrompt,
                            subLogger, subRenderer, allowUseShell, allowFileMutation, ct, injectionQueue)
                            .ConfigureAwait(false);

                        logger.EndSubAgentDispatch(subSuccess, subMessage);

                        logger.LogInfo("🤖 Sub Agent reported back.");

                        string reportPreview = $"[Sub Agent {(subSuccess ? "SUCCESS" : "FAILURE")}] {subMessage}";
                        logger.LogToolResult(id, reportPreview);

                        toolResults.Add(new JsonObject
                        {
                            ["type"]        = "tool_result",
                            ["tool_use_id"] = id,
                            ["content"]     = ToolRegistry.ToContentNode(reportPreview),
                        });
                        continue;
                    }

                    ToolRegistry.ProgressSink = msg => logger.LogToolProgress(name, msg);
                    (bool found, object result) execResult;
                    try
                    {
                        execResult = ToolRegistry.TryExecute(name, input);
                    }
                    finally
                    {
                        ToolRegistry.ProgressSink = null;
                    }
                    var (found, result) = execResult;

                    if (!found)
                    {
                        logger.LogError($"Unknown tool: {name}");
                        toolResults.Add(MessageBuilder.MakeTextToolResult(
                            id, $"Error: Unknown tool '{name}'"));
                        continue;
                    }

                    string preview = result?.ToString() ?? string.Empty;
                    logger.LogToolResult(id, preview);

                    toolResults.Add(new JsonObject
                    {
                        ["type"]        = "tool_result",
                        ["tool_use_id"] = id,
                        ["content"]     = ToolRegistry.ToContentNode(result),
                    });
                }

                messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
            }

            if (!reachedNaturalStop)
            {
                logger.LogWarning(
                    $"Agentic loop reached the maximum of {AppConfig.MAX_AGENTIC_ROUNDS} " +
                    "rounds without the model reaching a natural stop. Aborting to " +
                    "prevent a runaway/looping session from consuming unbounded memory " +
                    "and tokens.");
                logger.LogTokenUsage(sessionUsage, isFinal: true);
            }
        }

        private static async Task<(bool success, string message)> RunDispatchedSubAgentAsync(
            JsonArray         realMessages,
            JsonArray         realTools,
            string            task,
            ILlmClient        client,
            RunSettings       settings,
            string            parentSystemPrompt,
            IAgentLogger      subLogger,
            IStreamRenderer   subRenderer,
            bool              allowUseShell,
            bool              allowFileMutation,
            CancellationToken ct,
            IPromptInjectionQueue? parentGate = null)
        {
            var subMessages = new JsonArray();
            for (int i = 0; i < realMessages.Count - 1; i++)
                subMessages.Add(realMessages[i]?.DeepClone());

            subMessages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] =
                    "───── 🤖 SUB AGENT DISPATCH ─────\n" +
                    "You have been dispatched, as a Sub Agent, to accomplish EXACTLY one task, " +
                    "described below. You are STRICTLY PROHIBITED from doing anything outside this " +
                    "task — no tangents, no unrelated exploration, and no asking anything back (there " +
                    "is nobody available to answer you — make reasonable assumptions and proceed). " +
                    "When you are done — whether you fully succeeded, partially succeeded, or could " +
                    "not complete it at all — you MUST end your turn with a normal, PLAIN TEXT reply " +
                    "(do NOT call any tool as your final action) formatted EXACTLY like this, as the " +
                    "very last thing you write:\n\n" +
                    "STATUS: success (or STATUS: failure)\n" +
                    "MESSAGE: <a complete summary of what you did, found, or concluded>\n\n" +
                    "That is the ONLY way you can end this task — nothing you say outside of that " +
                    "final STATUS/MESSAGE reply will ever be seen by anyone.\n\n" +
                    "YOUR TASK:\n" + task + "\n───── END DISPATCH ─────",
            });

            var subTools = new JsonArray();
            foreach (JsonNode? t in realTools)
                subTools.Add(t?.DeepClone());

            string subSystemPrompt = parentSystemPrompt;

            JsonArray? previousActiveTools = ToolRegistry.ActiveTools;
            ToolRegistry.ActiveTools = subTools;

            bool       gotNaturalStop   = false;
            bool       reportSuccess    = false;
            string?    reportMessage    = null;
            TokenUsage subSessionUsage  = default;
            int        subRoundsRun     = 0;

            try
            {
                for (int round = 1; round <= AppConfig.MAX_AGENTIC_ROUNDS; round++)
                {
                    ct.ThrowIfCancellationRequested();

                    // BUG FIX: the sub agent never looked at any pause, so "Pause" did nothing while it ran.
                    await WaitWhilePausedAsync(subLogger, ct, parentGate, subLogger.PauseGate)
                        .ConfigureAwait(false);

                    subLogger.LogRound(round);

                    JsonObject? resp = await client.CallAsync(
                        subMessages,
                        subTools,
                        subSystemPrompt,
                        settings.MaxTokens,
                        settings.Thinking,
                        settings.Effort,
                        settings.Debug,
                        subLogger,
                        subRenderer,
                        ct).ConfigureAwait(false);

                    if (resp == null)
                    {
                        reportMessage = "Sub Agent's LLM call failed outright (see the main log for " +
                                        "the underlying error) before it ever reported back.";
                        break;
                    }

                    TokenUsage subRoundUsage = ReadUsage(resp);
                    subSessionUsage += subRoundUsage;
                    subRoundsRun++;
                    subLogger.LogTokenUsage(subRoundUsage);

                    JsonArray blocks = resp["content"]?.AsArray() ?? new JsonArray();
                    subMessages.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks.DeepClone() });

                    string stopReason = resp["stop_reason"]?.GetValue<string>() ?? "unknown";

                    if (stopReason != "tool_use")
                    {
                        gotNaturalStop = true;
                        string finalText = ExtractPlainText(blocks);
                        (reportSuccess, reportMessage) = ParseSubAgentReport(finalText);
                        break;
                    }

                    using IDisposable subLedgerScope = FileViewLedger.BeginScope();
                    var subToolResults = new JsonArray();

                    foreach (JsonNode? block in blocks)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (block?["type"]?.GetValue<string>() != "tool_use") continue;

                        string tid   = block["id"]!.GetValue<string>();
                        string tname = block["name"]!.GetValue<string>();
                        var    tin   = block["input"]!.AsObject();

                        string tInputJson = tin.ToJsonString();
                        subLogger.LogToolCallFull(tname, tid, tInputJson); // write-ahead: before anything runs
                        subLogger.LogToolCall(tname, tid, tInputJson[..Math.Min(200, tInputJson.Length)]);

                        string? denyReason = TryDenySubAgentToolCall(tname, allowUseShell, allowFileMutation);
                        if (denyReason != null)
                        {
                            subLogger.LogToolResult(tid, denyReason);
                            subToolResults.Add(MessageBuilder.MakeTextToolResult(tid, denyReason));
                            continue;
                        }

                        ToolRegistry.ProgressSink = msg => subLogger.LogToolProgress(tname, msg);
                        (bool found, object result) execResult;
                        try { execResult = ToolRegistry.TryExecute(tname, tin); }
                        finally { ToolRegistry.ProgressSink = null; }
                        var (found, result) = execResult;

                        if (!found)
                        {
                            subLogger.LogError($"Unknown tool: {tname}");
                            subToolResults.Add(MessageBuilder.MakeTextToolResult(
                                tid, $"Error: Unknown tool '{tname}'"));
                            continue;
                        }

                        string tPreview = result?.ToString() ?? string.Empty;
                        subLogger.LogToolResult(tid, tPreview);

                        subToolResults.Add(new JsonObject
                        {
                            ["type"]        = "tool_result",
                            ["tool_use_id"] = tid,
                            ["content"]     = ToolRegistry.ToContentNode(result),
                        });
                    }

                    subMessages.Add(new JsonObject { ["role"] = "user", ["content"] = subToolResults });
                }

                if (subRoundsRun > 1)
                    subLogger.LogTokenUsage(subSessionUsage, isFinal: true);
            }
            catch (OperationCanceledException)
            {
                throw; // propagate as a real whole-turn cancellation — never swallowed into a "failed" report
            }
            catch (Exception ex)
            {
                SessionFileLogger.LogException("Dispatched Sub Agent failed with an unexpected error", ex);
                gotNaturalStop = true;
                reportSuccess  = false;
                reportMessage  = $"Sub Agent failed with an unexpected error: {ex.Message}";
            }
            finally
            {
                ToolRegistry.ActiveTools = previousActiveTools;
            }

            if (!gotNaturalStop)
            {
                reportSuccess = false;
                reportMessage ??= "Sub Agent ended without ever reaching a natural (tool-free) stop " +
                                   "(reached its round limit or stopped unexpectedly) — no result was provided.";
            }

            return (reportSuccess, reportMessage!);
        }

        private static readonly HashSet<string> AlwaysDeniedForSubAgent =
            new(StringComparer.OrdinalIgnoreCase) { "dispatch_sub_agent", "delegate_task", "ask_questions" };

        private static string? TryDenySubAgentToolCall(string toolName, bool allowUseShell, bool allowFileMutation)
        {
            if (AlwaysDeniedForSubAgent.Contains(toolName))
            {
                return $"Error: you (a dispatched Sub Agent) are not allowed to invoke '{toolName}' — " +
                       "it is reserved for the real/top-level conversation only and is never available " +
                       "to a dispatched Sub Agent, regardless of any dispatch settings.";
            }

            if (!allowUseShell)
            {
                string[] tags = ToolRegistry.GetTagsForTool(toolName);
                foreach (string t in tags)
                {
                    if (string.Equals(t, "shell", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"Error: you (a dispatched Sub Agent) are not allowed to invoke '{toolName}' " +
                               "— this dispatch was not given 'allow_use_shell: true', so shell/arbitrary-" +
                               "command-execution tools are disabled for you by default.";
                    }
                }
            }

            if (!allowFileMutation && ToolRegistry.IsFileMutating(toolName))
            {
                return $"Error: you (a dispatched Sub Agent) are not allowed to invoke '{toolName}' — this " +
                       "dispatch was not given 'allow_file_mutation: true', so tools that create/modify/" +
                       "delete/move files or folders on the local file system are disabled for you by " +
                       "default.";
            }

            return null;
        }

        private static bool GetOptionalBoolInput(JsonObject input, string key, bool defaultValue)
        {
            JsonNode? node = input[key];
            if (node is null) return defaultValue;

            if (node is JsonValue value)
            {
                if (value.TryGetValue(out bool b)) return b;

                if (value.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
                {
                    s = s.Trim();
                    if (bool.TryParse(s, out bool parsed)) return parsed;
                    if (s == "1") return true;
                    if (s == "0") return false;
                    return defaultValue;
                }

                if (value.TryGetValue(out double d)) return d != 0;
            }

            return defaultValue;
        }

        private static string ExtractPlainText(JsonArray blocks)
        {
            var sb = new StringBuilder();
            foreach (JsonNode? block in blocks)
            {
                if (block?["type"]?.GetValue<string>() != "text") continue;
                string? t = block["text"]?.GetValue<string>();
                if (string.IsNullOrEmpty(t)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t);
            }
            return sb.ToString();
        }

        private static (bool success, string message) ParseSubAgentReport(string finalText)
        {
            if (string.IsNullOrWhiteSpace(finalText))
                return (false, "Sub Agent ended its turn with no text output at all — no result was provided.");

            Match statusMatch  = Regex.Match(finalText, @"STATUS\s*:\s*(success|failure)", RegexOptions.IgnoreCase);
            Match messageMatch = Regex.Match(finalText, @"MESSAGE\s*:\s*(.*)", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (statusMatch.Success && messageMatch.Success)
            {
                bool   success = string.Equals(statusMatch.Groups[1].Value, "success", StringComparison.OrdinalIgnoreCase);
                string message = messageMatch.Groups[1].Value.Trim();
                return (success, string.IsNullOrWhiteSpace(message) ? finalText.Trim() : message);
            }

            return (true, finalText.Trim());
        }

        private static bool AnyPaused(IPromptInjectionQueue?[] gates)
        {
            foreach (IPromptInjectionQueue? g in gates)
                if (g != null && g.IsPaused) return true;
            return false;
        }

        /// <summary>
        /// Holds while ANY of the gates says "paused". The main loop passes its own session's gate.
        /// A dispatched sub agent passes BOTH its parent's gate and its own session's gate, so pausing
        /// either one really stops the sub agent before its next round.
        /// </summary>
        private static async Task WaitWhilePausedAsync(
            IAgentLogger                 logger,
            CancellationToken            ct,
            params IPromptInjectionQueue?[] gates)
        {
            if (!AnyPaused(gates)) return;

            foreach (IPromptInjectionQueue? g in gates) g?.OnPauseWaitStart();
            try
            {
                logger.LogInfo(
                    "⏸ Paused — holding before the next round starts (thinking/tool execution " +
                    "already in flight is never affected) until resumed — either the host UI's own " +
                    "explicit \"⏸ Pause\"/\"▶ Resume\" button, or the hold of the 💉 Inject window, " +
                    "whichever is currently holding it.");

                while (AnyPaused(gates))
                    await Task.Delay(200, ct).ConfigureAwait(false);

                logger.LogInfo("▶ Resumed — continuing to the next round.");
            }
            finally
            {
                foreach (IPromptInjectionQueue? g in gates) g?.OnPauseWaitEnd();
            }
        }

        private static void ApplyPendingInjections(
            JsonArray            messages,
            JsonArray            tools,
            List<PendingInjection> injections,
            IAgentLogger         logger)
        {
            if (messages.Count == 0) return;
            if (messages[^1] is not JsonObject lastMsg) return;
            if (lastMsg["role"]?.GetValue<string>() != "user") return;

            JsonArray contentArray;
            if (lastMsg["content"] is JsonArray existingArray)
            {
                contentArray = existingArray;
            }
            else
            {
                contentArray = new JsonArray();
                if (lastMsg["content"] is JsonValue textVal && textVal.TryGetValue(out string? text))
                    contentArray.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                lastMsg["content"] = contentArray;
            }

            foreach (PendingInjection injection in injections)
            {
                contentArray.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] =
                        "───── 💉 External Injection (queued by the user mid-turn, via a " +
                        "separate control outside this conversation) ─────\n" +
                        (string.IsNullOrWhiteSpace(injection.Text) ? "(no text — see attachment(s) below)" : injection.Text) +
                        "\n───── End Injection ─────",
                });

                foreach (string filePath in injection.Files)
                {
                    try
                    {
                        string ext = Path.GetExtension(filePath);
                        if (AppConfig.IMAGE_EXTS.Contains(ext))
                        {
                            byte[] bytes = File.ReadAllBytes(filePath);
                            contentArray.Add(new JsonObject
                            {
                                ["type"] = "image",
                                ["source"] = new JsonObject
                                {
                                    ["type"]       = "base64",
                                    ["media_type"] = ToolsIOHelpers.GetMediaType(filePath),
                                    ["data"]       = Convert.ToBase64String(bytes),
                                },
                            });
                        }
                        else
                        {
                            string text = File.ReadAllText(filePath, Encoding.UTF8);
                            contentArray.Add(new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = $"[Injected attached file: {Path.GetFileName(filePath)}]\n{text}",
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        contentArray.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"[Injected file could not be read: {filePath} — {ex.Message}]",
                        });
                    }
                }

                if (injection.ToolNamesToLoad.Count > 0)
                {
                    var alreadyPresent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonNode? t in tools)
                    {
                        string? n = t?["name"]?.GetValue<string>();
                        if (n != null) alreadyPresent.Add(n);
                    }

                    var newlyLoaded = new List<string>();
                    foreach (string name in injection.ToolNamesToLoad)
                    {
                        if (alreadyPresent.Contains(name)) continue; // already available — skip silently

                        JsonObject? entry = ToolRegistry.TryGetSchemaEntry(name);
                        if (entry == null) continue; // unknown/stale tool name — ignore silently

                        tools.Add(entry);
                        alreadyPresent.Add(name);
                        newlyLoaded.Add(name);
                    }

                    if (newlyLoaded.Count > 0)
                        contentArray.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"[The user pre-loaded additional tool(s) for you: {string.Join(", ", newlyLoaded)}]",
                        });
                }

                logger.LogInjection(injection);
            }
        }

        private static bool RollBackBloatingRound(
            JsonArray    messages,
            IAgentLogger logger,
            int          attemptNumber,
            int          maxAttempts)
        {
            if (messages.Count < 3) return false;

            JsonObject? removedAssistant = messages[^2] as JsonObject;

            messages.RemoveAt(messages.Count - 1);
            messages.RemoveAt(messages.Count - 1);

            if (messages[^1] is not JsonObject lastMsg ||
                lastMsg["role"]?.GetValue<string>() != "user")
            {
                return false;
            }

            var toolNames = new List<string>();
            if (removedAssistant?["content"] is JsonArray removedBlocks)
            {
                foreach (JsonNode? b in removedBlocks)
                {
                    if (b?["type"]?.GetValue<string>() != "tool_use") continue;
                    string? n = b["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(n)) toolNames.Add(n!);
                }
            }
            string toolsMentioned = toolNames.Count > 0
                ? $" (the call(s) to: {string.Join(", ", toolNames)})"
                : "";

            string note =
                $"⚠ SYSTEM NOTICE — automatic context-length recovery (attempt " +
                $"{attemptNumber}/{maxAttempts}): your last action{toolsMentioned} produced (or " +
                "accumulated) a result so large that the very next API call was REJECTED outright " +
                "for exceeding the model's maximum context window. That entire round has been " +
                "AUTOMATICALLY ROLLED BACK — it is fully undone and no longer part of this " +
                "conversation, exactly as if it never happened.\n\n" +
                "You are doing something that bloats token usage far too much. Do NOT repeat the " +
                "same approach — it will just fail again the same way. Try a different, smarter, " +
                "more targeted method instead. For example: read/return only a SMALL portion of a " +
                "file or result at a time (use pagination/offset/line-range/chunk parameters where " +
                "available), filter or search for just the specific information you actually need " +
                "instead of dumping an entire file/output, avoid loading huge files or tool results " +
                "in full, and prefer narrower, more targeted tool calls in general. Continue the " +
                "task now using a leaner approach.";

            JsonArray contentArray;
            if (lastMsg["content"] is JsonArray existingArray)
            {
                contentArray = existingArray;
            }
            else
            {
                contentArray = new JsonArray();
                if (lastMsg["content"] is JsonValue textVal && textVal.TryGetValue(out string? text))
                    contentArray.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                lastMsg["content"] = contentArray;
            }

            contentArray.Add(new JsonObject { ["type"] = "text", ["text"] = note });

            logger.LogWarning(
                $"[Context-length rollback {attemptNumber}/{maxAttempts}] The prompt exceeded the " +
                $"model's max context window. Rolled back the last round{toolsMentioned} and " +
                "injected a corrective note telling the model to use a smarter/leaner approach, " +
                "then retrying.");

            return true;
        }

        private static string BuildEnrichedSystemPrompt(string basePrompt)
        {
            DateTime now = DateTime.Now;

            string runtimeBlock =
                "\n\n" +
                "════════════════════════════════════════════════════════════════\n" +
                " RUNTIME CONTEXT (injected automatically for this turn)\n" +
                "════════════════════════════════════════════════════════════════\n" +
                $"Current date & time: {now:yyyy-MM-dd HH:mm:ss} ({now:dddd})\n" +
                "Treat this as ground truth for 'today', 'now', or any relative date " +
                "('yesterday', 'last week', 'this quarter') — never guess the current " +
                "date from training data.\n\n" +
                "BUG REPORT TOOL (bug_report) — NARROW scope: use it ONLY when a tool you " +
                "actually called during this task misbehaved (errored, threw an exception, " +
                "timed out, or returned an obviously wrong/unexpected/garbage result).\n" +
                "TOOL-ERROR RECOVERY PROTOCOL (the #1 correct trigger): if any tool call " +
                "errors out or returns a wrong/unexpected/empty result, do NOT give up after " +
                "one attempt — retry the SAME tool with different/corrected parameters to " +
                "confirm it is genuinely broken. Once confirmed (whether or not a working " +
                "approach was eventually found), call " +
                "bug_report(title, tool_name, issue, evidence, task): 'tool_name' = the exact " +
                "tool that misbehaved; 'issue' = what you tried to do, what you expected, and " +
                "what happened instead; 'evidence' = the exact parameters used and the exact " +
                "error/wrong output text, with any secrets/tokens/credentials SANITIZED to " +
                "'[REDACTED]'; 'task' = a concrete to-do for the developer to fix it.\n" +
                "Do NOT call bug_report for anything else — not for general ideas, process " +
                "gaps, or observations unrelated to a tool actually malfunctioning. If nothing " +
                "anomalous happened with any tool this session, do nothing on this front. If " +
                "you notice something worth mentioning that is NOT a tool malfunction, just " +
                "add a brief note in your final summary to the user instead of logging it.\n";

            return string.IsNullOrEmpty(basePrompt)
                ? runtimeBlock.TrimStart()
                : basePrompt + runtimeBlock;
        }

        private static TokenUsage ReadUsage(JsonObject resp)
        {
            JsonNode? u = resp["usage"];
            if (u == null) return default;
            return new TokenUsage
            {
                InputTokens              = u["input_tokens"]?.GetValue<int>()                ?? 0,
                OutputTokens             = u["output_tokens"]?.GetValue<int>()               ?? 0,
                CacheCreationInputTokens = u["cache_creation_input_tokens"]?.GetValue<int>() ?? 0,
                CacheReadInputTokens     = u["cache_read_input_tokens"]?.GetValue<int>()     ?? 0,
            };
        }

        private static void RedactProcessedImages(JsonArray messages, string? evidenceDir)
        {
            var toolNameById = new System.Collections.Generic.Dictionary<string, string>();
            foreach (JsonNode? message in messages)
            {
                if (message?["role"]?.GetValue<string>() != "assistant") continue;
                if (message["content"] is not JsonArray assistantBlocks) continue;

                foreach (JsonNode? block in assistantBlocks)
                {
                    if (block?["type"]?.GetValue<string>() != "tool_use") continue;
                    string? id   = block["id"]?.GetValue<string>();
                    string? name = block["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                        toolNameById[id!] = name!;
                }
            }

            foreach (JsonNode? message in messages)
            {
                if (message?["role"]?.GetValue<string>() != "user") continue;

                if (message["content"] is not JsonArray toolResultsArray) continue;

                foreach (JsonNode? toolResult in toolResultsArray)
                {
                    if (toolResult?["type"]?.GetValue<string>() != "tool_result") continue;

                    string? toolUseId = toolResult["tool_use_id"]?.GetValue<string>();
                    if (toolUseId != null &&
                        toolNameById.TryGetValue(toolUseId, out string? toolName) &&
                        GTA.Tools.ImageUtils.ShouldAlwaysKeepImage(toolName))
                    {
                        continue;
                    }

                    if (toolResult["content"] is not JsonArray contentBlocks) continue;

                    for (int i = 0; i < contentBlocks.Count; i++)
                    {
                        JsonNode? block = contentBlocks[i];
                        if (block?["type"]?.GetValue<string>() != "image") continue;

                        string mediaType = block["source"]?["media_type"]?.GetValue<string>()
                                           ?? "image";
                        string? data = block["source"]?["data"]?.GetValue<string>();

                        string replacementText;

                        string? savedPath = null;
                        long    savedSize  = 0;
                        if (!string.IsNullOrEmpty(evidenceDir) && !string.IsNullOrEmpty(data))
                        {
                            savedPath = GTA.Tools.ImageEvidenceStore.TrySaveBase64ToFile(
                                data!, mediaType, evidenceDir!, out savedSize);
                        }

                        replacementText = savedPath != null
                            ? GTA.Tools.ImageEvidenceStore.BuildMarkerText(savedPath, mediaType, savedSize)
                            : $"[{mediaType} — image data redacted from history " +
                              "after LLM processing to prevent re-transmission]";

                        contentBlocks[i] = new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = replacementText,
                        };
                    }
                }
            }
        }

    }
}
