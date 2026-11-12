using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        /// <summary>
        /// Runs one user turn (many rounds). STOP-safe: whatever moment <paramref name="ct"/> is cancelled
        /// (thinking, streaming, a tool running, waiting in a pause), the history is left VALID and the next
        /// turn of the same chat can continue. See <see cref="ConversationHistoryRepair"/>.
        ///
        /// RESUME (after a pause that was saved to disk): <paramref name="resumeTurn"/> = true means the history already
        /// holds the user prompt (and everything the turn did so far), so NO new user message is added.
        /// <paramref name="startRound"/> is the round that was about to start. When the pause happened in the middle of a
        /// Sub Agent dispatch, <paramref name="resumeDispatch"/> carries the tool results already produced in that round
        /// and the state of every Sub Agent; the loop then continues that tool phase instead of calling the model.
        /// </summary>
        public static async Task RunAsync(
            RunSettings          settings,
            JsonArray            tools,
            IAgentLogger         logger,
            IStreamRenderer      renderer,
            JsonArray?           conversationHistory = null,
            CancellationToken    ct                  = default,
            ILlmClient?          llmClient           = null,
            IPromptInjectionQueue? injectionQueue     = null,
            bool                 resumeTurn          = false,
            int                  startRound          = 1,
            DispatchResumeState? resumeDispatch      = null)
        {
            var messages = conversationHistory ?? new JsonArray();
            try
            {
                await RunLoopAsync(settings, tools, logger, renderer, messages, ct, llmClient, injectionQueue,
                        resumeTurn, startRound, resumeDispatch)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The loop was cut at an arbitrary point. Close the history cleanly, THEN let the stop go up.
                ConversationHistoryRepair.CloseInterruptedTurn(messages, logger);
                throw;
            }
        }

        private static async Task RunLoopAsync(
            RunSettings          settings,
            JsonArray            tools,
            IAgentLogger         logger,
            IStreamRenderer      renderer,
            JsonArray            messages,
            CancellationToken    ct,
            ILlmClient?          llmClient,
            IPromptInjectionQueue? injectionQueue,
            bool                 resumeTurn,
            int                  startRound,
            DispatchResumeState? resumeDispatch)
        {
            ILlmClient client = llmClient ?? new ClaudeClient();

            string effectiveSystemPrompt = BuildEnrichedSystemPrompt(settings.SystemPrompt);
            if (!resumeTurn)
            {
                if (settings.Files.Count == 0)
                    messages.Add(MessageBuilder.MakeUserMessage(settings.Prompt));
                else
                    messages.Add(MessageBuilder.MakeUserMessageWithFiles(
                        settings.Prompt, settings.Files, logger));
            }
            // The history of an earlier turn may be broken: the user pressed STOP in the middle of a tool call,
            // the app was killed mid-turn, or the chat was saved by an older version. Heal it BEFORE the first
            // API call. Without this, every call of the chat fails with HTTP 400.
            // NOT when resuming inside a tool phase: then the last assistant message has tool calls that are
            // answered a moment later, and the repair would wrongly fill them with "stopped" results.
            if (resumeDispatch == null)
                ConversationHistoryRepair.Repair(messages, logger);

            ToolRegistry.ActiveTools = tools;
            var sessionUsage = new TokenUsage();   // accumulated across all rounds

            bool reachedNaturalStop = false;

            int contextLengthRollbackCount = 0;
            const int MaxContextLengthRollbacks = 5;
            int ghostRetries = 0;   // consecutive responses that held ONLY non-existent tool calls

            bool resumingToolPhase = resumeDispatch != null;
            DispatchResumeState? roundResumeState = null;

            for (int round = Math.Max(1, startRound); round <= AppConfig.MAX_AGENTIC_ROUNDS; round++)
            {
                ct.ThrowIfCancellationRequested();

                JsonArray blocks;
                string    stopReason;
                long      callStartTs = Stopwatch.GetTimestamp();

                if (resumingToolPhase)
                {
                    // RESUME in the middle of a round's tool phase (the pause came while Sub Agents were running).
                    // The assistant message with the tool calls is already the last message of the history. Do not
                    // call the model again: go straight to the tool phase and finish it.
                    resumingToolPhase = false;
                    roundResumeState  = resumeDispatch;
                    stopReason = "tool_use";
                    blocks     = (messages.Count > 0 ? (messages[^1] as JsonObject)?["content"] as JsonArray : null)
                                 ?? new JsonArray();
                    injectionQueue?.OnRoundFence(round);
                    // No LogRound / info line here: the host keeps writing into the SAME round row and bubble, and shows
                    // its own "▶ Resumed" widget. A second "round N" row would be a duplicate.
                }
                else
                {
                injectionQueue?.OnRoundFence(round);   // the host remembers it: a resume continues from this round

                if (injectionQueue != null)
                    await WaitWhilePausedAsync(logger, ct, injectionQueue, injectionQueue.OnMainFenceHold, injectionQueue);

                if (injectionQueue != null)
                {
                    List<PendingInjection> injections = injectionQueue.DrainPending();
                    if (injections.Count > 0)
                        ApplyPendingInjections(messages, tools, injections, logger);
                }

                logger.LogRound(round);

                int effectiveMaxTokens = settings.MaxTokens;

                // Shrink every image that is too big for the API (it refuses the WHOLE request for one bad image).
                GTA.Tools.Impl.Common.ImageHistoryGuard.ShrinkOversizedImages(messages, logger);

                callStartTs = Stopwatch.GetTimestamp();   // this call (re)starts the 5-minute cache timer
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

                // STOP during the model call (thinking / streaming): do not treat it as "call failed" (null) and do not
                // use a half or finished answer. Nothing of this round is in the history yet, so it stays valid.
                ct.ThrowIfCancellationRequested();

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

                stopReason = resp["stop_reason"]?.GetValue<string>() ?? "unknown";
                blocks     = resp["content"]?.AsArray() ?? new JsonArray();

                // GHOST TOOL CALLS: the model sometimes calls a tool that does not exist anywhere (not loaded AND
                // not in the registry). Such a call is removed from the response BEFORE it enters the history, so
                // the model never sees it (see StripGhostToolCalls for why this is safe for the API).
                if (stopReason == "tool_use")
                {
                    if (ghostRetries < MaxGhostRetries)
                    {
                        List<string> dropped = StripGhostToolCalls(blocks, logger);
                        if (dropped.Count > 0 && !ContainsToolUse(blocks))
                        {
                            // Nothing real is left to run. Throw the whole response away and ask again,
                            // exactly as if the model had never produced it.
                            ghostRetries++;
                            logger.LogWarning(
                                $"Model called non-existent tool(s): {string.Join(", ", dropped)}. " +
                                $"Dropped from history, asking the model again ({ghostRetries}/{MaxGhostRetries}).");
                            continue;
                        }
                    }
                    ghostRetries = 0;   // real progress, or the retry budget is used up (then the explicit error below is sent)
                }

                messages.Add(new JsonObject
                {
                    ["role"]    = "assistant",
                    ["content"] = blocks.DeepClone(),
                });
                }   // end of "normal round start" (model call). A resumed tool phase skips all of it.

                if (stopReason != "tool_use")
                {
                    logger.LogStopReason(stopReason);
                    if (round > 1)
                        logger.LogTokenUsage(sessionUsage, isFinal: true);
                    reachedNaturalStop = true;
                    break;
                }

                using IDisposable ledgerScope = FileViewLedger.BeginScope();

                // On a resume inside the tool phase, the results already produced before the pause are used again.
                DispatchResumeState? roundResume = roundResumeState;
                roundResumeState = null;
                var toolResults = roundResume != null ? roundResume.ToolResults : new JsonArray();

                var answeredIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonNode? r in toolResults)
                    if (r?["tool_use_id"]?.GetValue<string>() is string aid) answeredIds.Add(aid);

                // A long tool (cmake, tests, long CLI) must not let the 5-minute prompt cache expire.
                // Normal short tools never cause a ping. Stopped + billed after the tool loop below.
                using CacheKeepAlive keepAlive = CacheKeepAlive.Start(
                    client, messages, tools, effectiveSystemPrompt,
                    settings.Thinking, settings.Effort, logger, callStartTs);

                // Parallel Sub Agents: reports of this response's dispatch calls (filled by the first dispatch call),
                // and the dispatch calls whose write-ahead log was already written early.
                var dispatchReports       = new Dictionary<string, (bool success, string message)>(StringComparer.Ordinal);
                var preLoggedDispatchIds  = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                foreach (JsonNode? block in blocks)
                {
                    ct.ThrowIfCancellationRequested();

                    if (block?["type"]?.GetValue<string>() != "tool_use") continue;

                    string id    = block["id"]!.GetValue<string>();
                    string name  = block["name"]!.GetValue<string>();
                    var    input = block["input"]!.AsObject();
                    // Resume: this call already has its result (it ran before the pause). Do not run it again.
                    if (answeredIds.Contains(id)) continue;

                    // A dispatch call that was already logged (write-ahead) by an earlier dispatch call of the same batch.
                    if (!preLoggedDispatchIds.Contains(id))
                    {
                        string inputJson = input.ToJsonString();
                        logger.LogToolCallFull(name, id, inputJson); // write-ahead: before anything runs
                        logger.LogToolCall(name, id, inputJson[..Math.Min(200, inputJson.Length)]);
                    }

                    if (string.Equals(name, "dispatch_sub_agent", StringComparison.OrdinalIgnoreCase))
                    {
                        // PARALLEL SUB AGENTS: the FIRST dispatch call of this response starts ALL dispatch calls of
                        // the response together and waits until every one of them has reported (or failed).
                        // The later dispatch calls then just pick up their report from dispatchReports.
                        if (!dispatchReports.TryGetValue(id, out (bool success, string message) report))
                        {
                            var batch = new List<(string id, JsonObject input)>();
                            foreach (JsonNode? b in blocks)
                            {
                                if (!IsDispatchBlock(b)) continue;

                                string   bid    = b!["id"]!.GetValue<string>();
                                string   bname  = b["name"]!.GetValue<string>();
                                JsonObject bin  = b["input"]!.AsObject();
                                batch.Add((bid, bin));

                                if (bid != id)
                                {
                                    // Write-ahead for the others now, because they start now (not when the loop reaches them).
                                    preLoggedDispatchIds.Add(bid);
                                    string bJson = bin.ToJsonString();
                                    logger.LogToolCallFull(bname, bid, bJson);
                                    logger.LogToolCall(bname, bid, bJson[..Math.Min(200, bJson.Length)]);
                                }
                            }

                            logger.LogInfo(batch.Count == 1
                                ? "🤖 Dispatched a Sub Agent to work on a task. This chat waits for its report. " +
                                  "A pause (⏸ Pause) is respected: the Sub Agent holds before its next round, " +
                                  "and this chat holds before ITS next round."
                                : $"🤖 Dispatched {batch.Count} Sub Agents IN PARALLEL (max {AppConfig.MAX_PARALLEL_SUB_AGENTS} at the same time, " +
                                  "the rest wait for a free slot). This chat waits until ALL of them have reported. " +
                                  "A pause (⏸ Pause) is respected by every Sub Agent.");

                            // The Sub Agents keep the shared cache warm themselves (pinned breakpoint), so no pings meanwhile.
                            Dictionary<string, (bool success, string message)> batchReports;
                            using (keepAlive.Suspend())
                            {
                                batchReports = await RunDispatchBatchAsync(
                                    batch, messages, tools, client, settings, effectiveSystemPrompt,
                                    logger, renderer, ct, injectionQueue, toolResults, roundResume)
                                    .ConfigureAwait(false);
                            }

                            foreach (var kv in batchReports) dispatchReports[kv.Key] = kv.Value;
                            report = dispatchReports[id];

                            logger.LogInfo(batch.Count == 1
                                ? "🤖 Sub Agent reported back."
                                : $"🤖 All {batch.Count} Sub Agents reported back.");
                        }

                        string reportPreview = $"[Sub Agent {(report.success ? "SUCCESS" : "FAILURE")}] {report.message}";
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
                    (bool found, object result, bool stopped) execResult;
                    try
                    {
                        // Runs the tool on its own thread and waits for it OR for STOP, whichever comes first.
                        execResult = await ExecuteToolCancellableAsync(name, input, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        ToolRegistry.ProgressSink = null;
                    }
                    var (found, result, stopped) = execResult;

                    if (!found)
                    {
                        logger.LogError($"Unknown tool: {name}");
                        toolResults.Add(MessageBuilder.MakeTextToolResult(
                            id, UnknownToolMessage(name)));
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

                    // STOP arrived while this tool ran. Its (partial / "stopped") result is saved above.
                    // Now leave the loop: the catch below answers every tool call that did not run.
                    if (stopped) ct.ThrowIfCancellationRequested();
                }
                }
                catch (OperationCanceledException)
                {
                    // STOP in the middle of a tool phase. The assistant message with ALL its tool_use blocks is already
                    // in the history, so EVERY tool_use needs a tool_result now, or the next call of this chat fails.
                    // Real results of tools that already finished are kept. The others get a "stopped" result.
                    keepAlive.Stop();
                    CompleteInterruptedToolResults(blocks, toolResults, logger);
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
                    throw;
                }
                sessionUsage += keepAlive.Stop();   // cost of the cache keep-alive pings (0 when no tool ran long)
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

        // ── Parallel Sub Agents ─────────────────────────────────────────────────────────

        private static bool IsDispatchBlock(JsonNode? block) =>
            block?["type"]?.GetValue<string>() == "tool_use" &&
            string.Equals(block["name"]?.GetValue<string>(), "dispatch_sub_agent", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Runs EVERY dispatch_sub_agent call of ONE model response at the same time and waits until all of them
        /// are done (success, failure or error). Returns one report per tool_use id.
        ///
        /// Rules:
        ///   - At most <see cref="AppConfig.MAX_PARALLEL_SUB_AGENTS"/> run at once. The others wait for a free slot.
        ///   - The parent history is NOT changed while the batch runs. Every Sub Agent gets its own copy.
        ///   - One Sub Agent that fails never kills the others. It only gives a "failure" report.
        ///   - STOP cancels all of them. Every twin session is closed before the stop goes up.
        ///   - PAUSE: every Sub Agent holds before its next round (see <see cref="LiveSubAgent"/>). The batch is registered
        ///     in the parent's <see cref="DispatchTracker"/>, so the host can save it and a later run can continue it:
        ///     <paramref name="resume"/> carries the state of every Sub Agent (queued / running with its history / done).
        /// </summary>
        private static async Task<Dictionary<string, (bool success, string message)>> RunDispatchBatchAsync(
            List<(string id, JsonObject input)> dispatches,
            JsonArray              messages,
            JsonArray              tools,
            ILlmClient             client,
            RunSettings            settings,
            string                 systemPrompt,
            IAgentLogger           logger,
            IStreamRenderer        renderer,
            CancellationToken      ct,
            IPromptInjectionQueue? parentGate,
            JsonArray              toolResults,
            DispatchResumeState?   resume)
        {
            // One snapshot, taken on this thread. Each Sub Agent clones from it, so no two threads
            // ever read the live parent history at the same time.
            var messagesSnapshot = (JsonArray)messages.DeepClone();
            var toolsSnapshot    = (JsonArray)tools.DeepClone();

            int maxParallel = Math.Max(1, AppConfig.MAX_PARALLEL_SUB_AGENTS);
            using var slots = new SemaphoreSlim(maxParallel);

            var resumedById = new Dictionary<string, LiveSubAgent>(StringComparer.Ordinal);
            if (resume != null)
                foreach (LiveSubAgent a in resume.Agents) resumedById[a.DispatchId] = a;

            var agents = new List<LiveSubAgent>();
            foreach ((string id, JsonObject input) in dispatches)
            {
                if (resumedById.TryGetValue(id, out LiveSubAgent? previous))
                {
                    agents.Add(previous);   // continues where it was paused (or is already done)
                    continue;
                }

                agents.Add(new LiveSubAgent
                {
                    DispatchId        = id,
                    Task              = input["task"]?.GetValue<string>() ?? string.Empty,
                    AllowUseShell     = GetOptionalBoolInput(input, "allow_use_shell", false),
                    AllowFileMutation = GetOptionalBoolInput(input, "allow_file_mutation", false),
                });
            }

            DispatchTracker? tracker = parentGate?.DispatchTracker;
            tracker?.Begin(toolResults, agents, maxParallel);
            try
            {
                var running = new List<(string id, Task<(bool success, string message)> task)>();
                foreach (LiveSubAgent live in agents)
                {
                    Task<(bool success, string message)> t;
                    if (live.State == LiveSubAgent.StateDone)
                    {
                        // Finished before the pause: its report is all that is needed.
                        t = Task.FromResult((live.Success, live.Message ?? string.Empty));
                    }
                    else
                    {
                        LiveSubAgent captured = live;
                        t = Task.Run(() => RunOneParallelSubAgentAsync(
                            captured, messagesSnapshot, toolsSnapshot, client, settings, systemPrompt,
                            logger, renderer, slots, ct, parentGate));
                    }
                    running.Add((live.DispatchId, t));
                }

                // Every task handles its own errors (see RunOneParallelSubAgentAsync). Only STOP can make WhenAll throw,
                // and it throws AFTER all tasks ended, so every twin session is already closed at that point.
                await Task.WhenAll(running.ConvertAll(r => r.task)).ConfigureAwait(false);

                var reports = new Dictionary<string, (bool success, string message)>(StringComparer.Ordinal);
                foreach ((string id, Task<(bool success, string message)> task) in running)
                    reports[id] = task.Result;
                return reports;
            }
            finally
            {
                tracker?.End();
            }
        }

        private static async Task<(bool success, string message)> RunOneParallelSubAgentAsync(
            LiveSubAgent           live,
            JsonArray              messagesSnapshot,
            JsonArray              toolsSnapshot,
            ILlmClient             client,
            RunSettings            settings,
            string                 systemPrompt,
            IAgentLogger           logger,
            IStreamRenderer        renderer,
            SemaphoreSlim          slots,
            CancellationToken      ct,
            IPromptInjectionQueue? parentGate)
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);   // STOP while queued -> OperationCanceledException, no slot taken
            try
            {
                // Has a slot now. (A queued Sub Agent that is still waiting is never counted as "running".)
                bool resuming = live.Messages != null;   // it was paused in the middle of its work: continue it
                live.State = LiveSubAgent.StateRunning;

                IAgentLogger? subLogger = null;
                try
                {
                    IStreamRenderer subRenderer;
                    (subLogger, subRenderer) = resuming && !string.IsNullOrEmpty(live.TwinSessionId)
                        ? logger.ResumeSubAgentDispatch(live.TwinSessionId!, live.Task, client.ModelName, renderer)
                        : logger.BeginSubAgentDispatch(live.Task, client.ModelName, renderer);
                    live.TwinSessionId = subLogger.SessionId ?? live.TwinSessionId;

                    bool   success;
                    string message;
                    try
                    {
                        (success, message) = await RunDispatchedSubAgentAsync(
                            messagesSnapshot, toolsSnapshot, live.Task, client, settings, systemPrompt,
                            subLogger, subRenderer, live.AllowUseShell, live.AllowFileMutation, ct, parentGate, live)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // STOP while this Sub Agent works: close its twin chat (it must not stay "running" forever).
                        try { logger.EndSubAgentDispatch(subLogger, false, "⏹ Stopped by the user."); } catch { /* never block the stop */ }
                        throw;
                    }

                    logger.EndSubAgentDispatch(subLogger, success, message);
                    live.Success = success;
                    live.Message = message;
                    live.State   = LiveSubAgent.StateDone;
                    return (success, message);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Any other problem (UI, logger, ...) only fails THIS Sub Agent. The others keep running.
                    SessionFileLogger.LogException("Parallel Sub Agent failed outside its own loop", ex);
                    string msg = $"Sub Agent failed with an unexpected error: {ex.Message}";
                    if (subLogger != null)
                    {
                        try { logger.EndSubAgentDispatch(subLogger, false, msg); } catch { /* ignore */ }
                    }
                    live.Success = false;
                    live.Message = msg;
                    live.State   = LiveSubAgent.StateDone;
                    return (false, msg);
                }
            }
            finally
            {
                live.AtFence = false;
                slots.Release();
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
            IPromptInjectionQueue? parentGate = null,
            LiveSubAgent?     live = null)
        {
            live ??= new LiveSubAgent { Task = task, AllowUseShell = allowUseShell, AllowFileMutation = allowFileMutation };

            // RESUME: the Sub Agent was paused earlier (maybe before an app restart). Its history, tools and round
            // were saved in 'live'. Everything is continued from there; nothing is rebuilt.
            bool resuming = live.Messages != null;

            JsonArray subMessages;
            JsonArray subTools;

            if (resuming)
            {
                subMessages = live.Messages!;
                subTools    = live.Tools ?? (JsonArray)realTools.DeepClone();
            }
            else
            {
                subMessages = new JsonArray();
                for (int i = 0; i < realMessages.Count - 1; i++)
                    subMessages.Add(realMessages[i]?.DeepClone());

                // CACHE KEEP-WARM: pin a prompt-cache breakpoint on the LAST message that is shared with the
                // parent (index Count-2 of the parent history; the parent's last message is the dispatch
                // tool_use, which the Sub Agent does not copy). Every Sub Agent request then re-touches this
                // cache entry, so the parent's history does not hit the 5-minute TTL while the Sub Agent works.
                // The parent's next request (after the report) finds this entry by its 20-block look-back.
                int sharedLast = realMessages.Count - 2;
                if (sharedLast >= 0 &&
                    subMessages[sharedLast] is JsonObject pinMsg &&
                    pinMsg["role"]?.GetValue<string>() == "user")
                {
                    pinMsg[AzureApiClient.CachePinKey] = true;
                }
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

                subTools = new JsonArray();
                foreach (JsonNode? t in realTools)
                    subTools.Add(t?.DeepClone());
            }

            // Visible to the host's pause tracker (see DispatchTracker): while this Sub Agent holds at its fence,
            // these two are a complete, untouched picture of it.
            live.Messages = subMessages;
            live.Tools    = subTools;

            string subSystemPrompt = parentSystemPrompt;

            JsonArray? previousActiveTools = ToolRegistry.ActiveTools;
            ToolRegistry.ActiveTools = subTools;

            bool       gotNaturalStop   = false;
            bool       reportSuccess    = false;
            string?    reportMessage    = null;
            TokenUsage subSessionUsage  = default;
            int        subRoundsRun     = 0;
            int        subGhostRetries  = resuming ? live.GhostRetries : 0;

            try
            {
                for (int round = resuming ? Math.Max(1, live.NextRound) : 1; round <= AppConfig.MAX_AGENTIC_ROUNDS; round++)
                {
                    ct.ThrowIfCancellationRequested();

                    live.NextRound    = round;
                    live.GhostRetries = subGhostRetries;

                    // A pause of the PARENT holds this Sub Agent too (it can not be paused on its own). The holds of the
                    // parent / of this Sub Agent's own session (💉 Inject window) are respected as before.
                    await WaitWhilePausedAsync(subLogger, ct, parentGate, h => live.AtFence = h,
                            parentGate, subLogger.PauseGate)
                        .ConfigureAwait(false);

                    subLogger.LogRound(round);
                    // Shrink every image that is too big for the API (it refuses the WHOLE request for one bad image).
                    GTA.Tools.Impl.Common.ImageHistoryGuard.ShrinkOversizedImages(subMessages, subLogger);
                    long subCallStartTs = Stopwatch.GetTimestamp();   // the cache is refreshed by this call
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

                    // STOP during the model call: never treat it as "call failed" (null) and never use a half answer.
                    ct.ThrowIfCancellationRequested();
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

                    JsonArray blocks     = resp["content"]?.AsArray() ?? new JsonArray();
                    string    stopReason = resp["stop_reason"]?.GetValue<string>() ?? "unknown";

                    // Same ghost-tool-call handling as the main loop (see there).
                    if (stopReason == "tool_use")
                    {
                        if (subGhostRetries < MaxGhostRetries)
                        {
                            List<string> dropped = StripGhostToolCalls(blocks, subLogger);
                            if (dropped.Count > 0 && !ContainsToolUse(blocks))
                            {
                                subGhostRetries++;
                                subLogger.LogWarning(
                                    $"Sub Agent called non-existent tool(s): {string.Join(", ", dropped)}. " +
                                    $"Dropped from history, asking again ({subGhostRetries}/{MaxGhostRetries}).");
                                continue;
                            }
                        }
                        subGhostRetries = 0;
                    }

                    subMessages.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks.DeepClone() });

                    if (stopReason != "tool_use")
                    {
                        gotNaturalStop = true;
                        string finalText = ExtractPlainText(blocks);
                        (reportSuccess, reportMessage) = ParseSubAgentReport(finalText);
                        break;
                    }

                    using IDisposable subLedgerScope = FileViewLedger.BeginScope();
                    var subToolResults = new JsonArray();

                    // A long tool must not let the 5-minute prompt cache of this Sub Agent expire.
                    using CacheKeepAlive subKeepAlive = CacheKeepAlive.Start(
                        client, subMessages, subTools, subSystemPrompt,
                        settings.Thinking, settings.Effort, subLogger, subCallStartTs);

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
                        (bool found, object result, bool stopped) execResult;
                        try { execResult = await ExecuteToolCancellableAsync(tname, tin, ct).ConfigureAwait(false); }
                        finally { ToolRegistry.ProgressSink = null; }
                        var (found, result, stopped) = execResult;
                        // STOP while this tool ran: the Sub Agent's own history is thrown away with the whole turn.
                        if (stopped) ct.ThrowIfCancellationRequested();

                        if (!found)
                        {
                            subLogger.LogError($"Unknown tool: {tname}");
                            subToolResults.Add(MessageBuilder.MakeTextToolResult(
                                tid, UnknownToolMessage(tname)));
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
                    subSessionUsage += subKeepAlive.Stop();   // cost of the cache keep-alive pings (0 when no tool ran long)
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

        // ── STOP handling for tools ─────────────────────────────────────────────────────

        /// <summary>How long a tool gets to finish by itself after STOP (tools that watch the token kill their process in this time).</summary>
        private static readonly TimeSpan ToolStopGrace = TimeSpan.FromSeconds(4);

        /// <summary>Set when the loop stops waiting for a tool. A tool thread that is still running then stays silent in the UI.</summary>
        private sealed class AbandonFlag { public volatile bool Set; }

        /// <summary>
        /// Runs ONE tool on its own thread and waits for it OR for STOP, whichever comes first.
        /// Tools are synchronous methods, so before this the STOP button did nothing while a tool ran
        /// (run_shell, python, git, http ... blocked the loop until they ended or timed out).
        ///
        /// On STOP:
        ///   - the token is put in <see cref="ToolCancellation.Token"/> (a tool that starts a child process kills the whole
        ///     process tree and returns a partial result: run_shell, gcc, cmake, inline_python, dotnet, git);
        ///   - the tool gets <see cref="ToolStopGrace"/> to return; if it does, its result is used (stopped = true);
        ///   - if not, the loop does NOT wait any more. The tool thread is abandoned (its progress lines are muted) and a
        ///     "stopped" result is returned. This is the honest limit: a tool that ignores the token (for example a long
        ///     HTTP request) keeps running in the background until it ends by itself. It can no longer block STOP.
        /// </summary>
        private static async Task<(bool found, object result, bool stopped)> ExecuteToolCancellableAsync(
            string name, JsonObject input, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return (true, ConversationHistoryRepair.StoppedToolResultText, true);

            Action<string>? realSink = ToolRegistry.ProgressSink;
            var abandoned = new AbandonFlag();
            Action<string>? sink = realSink == null ? null : m => { if (!abandoned.Set) realSink(m); };

            Task<(bool found, object result)> work = Task.Run(() =>
            {
                ToolCancellation.Token    = ct;     // AsyncLocal: only this tool's own call-flow sees it
                ToolRegistry.ProgressSink = sink;
                return ToolRegistry.TryExecute(name, input);
            });

            try
            {
                var done = await work.WaitAsync(ct).ConfigureAwait(false);
                return (done.found, done.result, false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try
                {
                    var done = await work.WaitAsync(ToolStopGrace).ConfigureAwait(false);
                    return (done.found, done.result, true);
                }
                catch (Exception)
                {
                    // TimeoutException (tool did not return in time) or a tool failure: fall through to "abandoned".
                }

                abandoned.Set = true;
                _ = work.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);   // never an unobserved exception
                SessionFileLogger.Log("TOOL_CALL",
                    $"STOP: tool '{name}' did not end within {ToolStopGrace.TotalSeconds:0}s. It was abandoned and may still run in the background.");
                return (true, ConversationHistoryRepair.StoppedToolResultText, true);
            }
        }

        /// <summary>
        /// STOP in the middle of a tool phase: adds a "stopped" tool_result for every tool_use of this response that
        /// has no result yet (in the original order), and closes its UI widget. Keeps the real results already collected.
        /// </summary>
        private static void CompleteInterruptedToolResults(JsonArray blocks, JsonArray toolResults, IAgentLogger logger)
        {
            var answered = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonNode? r in toolResults)
            {
                if (r?["tool_use_id"]?.GetValue<string>() is string rid) answered.Add(rid);
            }

            // Rebuild in the order of the tool_use blocks (the API does not require the order, but it keeps the log readable).
            var ordered = new List<JsonNode?>();
            foreach (JsonNode? block in blocks)
            {
                if (block?["type"]?.GetValue<string>() != "tool_use") continue;
                string? id = block["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id) || answered.Contains(id!)) continue;

                try { logger.LogToolResult(id!, ConversationHistoryRepair.StoppedToolResultText); }   // finishes the widget ("Running…")
                catch { /* UI problem must not break the stop */ }

                ordered.Add(ConversationHistoryRepair.MakeStoppedToolResult(id!));
            }
            foreach (JsonNode? stub in ordered) toolResults.Add(stub);
        }

        // ── Ghost tool calls (tool names that exist nowhere) ─────────────────────────────

        /// <summary>
        /// How many times in a row a response made ONLY of non-existent tool calls is silently thrown away and
        /// requested again. After that, the old behaviour is used: a real tool_result error goes back to the model,
        /// so a model that keeps repeating the same ghost call still gets told about it and cannot loop forever.
        /// </summary>
        private const int MaxGhostRetries = 3;

        /// <summary>
        /// Removes every tool_use block whose name is not registered AT ALL (not only "not loaded": it does not exist
        /// in <see cref="ToolRegistry"/>, so load_tools could never add it either). Returns the removed names.
        /// Removing the block keeps the history valid for the API: every tool_use must have a matching tool_result in
        /// the next user message, and a block that is gone needs no result. The UI widget that was already created
        /// while the response streamed is closed with <see cref="IAgentLogger.LogToolAbandoned"/>.
        /// </summary>
        private static List<string> StripGhostToolCalls(JsonArray blocks, IAgentLogger logger)
        {
            var removed = new List<string>();
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                JsonNode? block = blocks[i];
                if (block?["type"]?.GetValue<string>() != "tool_use") continue;

                string name = block["name"]?.GetValue<string>() ?? string.Empty;
                if (ToolRegistry.IsKnownTool(name)) continue;

                string id = block["id"]?.GetValue<string>() ?? string.Empty;
                logger.LogToolAbandoned(id, $"'{name}' is not a real tool — call ignored and not sent back to the model.");
                removed.Insert(0, name);
                blocks.RemoveAt(i);
            }
            return removed;
        }

        private static bool ContainsToolUse(JsonArray blocks)
        {
            foreach (JsonNode? block in blocks)
                if (block?["type"]?.GetValue<string>() == "tool_use") return true;
            return false;
        }

        /// <summary>The error sent back when the silent-retry budget is used up.</summary>
        private static string UnknownToolMessage(string name) =>
            $"Error: Unknown tool '{name}'. This tool does not exist at all — it is not loaded and it is not " +
            "in the tool registry either, so load_tools cannot add it. Do not invent tool names. " +
            "Call get_available_tools to see the real tools, then use only those names.";


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

        private static bool ShouldHold(IPromptInjectionQueue? userPauseGate, IPromptInjectionQueue?[] gates)
        {
            if (userPauseGate != null && userPauseGate.IsPauseRequested) return true;   // the user's ⏸ Pause
            foreach (IPromptInjectionQueue? g in gates)
                if (g != null && g.IsPaused) return true;                               // a hold, e.g. the 💉 Inject window
            return false;
        }

        /// <summary>
        /// Holds while the user asked for a pause (<paramref name="userPauseGate"/>.IsPauseRequested) OR any of the
        /// <paramref name="gates"/> holds (💉 Inject window). The main loop passes its own session's runtime as
        /// <paramref name="userPauseGate"/>; a dispatched Sub Agent passes its PARENT's runtime, so pausing the parent
        /// stops every Sub Agent before its next round, and the Sub Agent can not be paused on its own.
        /// <paramref name="fenceHold"/> is told true when the hold starts and false when it ends: the host knows that
        /// the history of this loop is complete and not changing while it is true.
        /// </summary>
        private static async Task WaitWhilePausedAsync(
            IAgentLogger                 logger,
            CancellationToken            ct,
            IPromptInjectionQueue?       userPauseGate,
            Action<bool>?                fenceHold,
            params IPromptInjectionQueue?[] gates)
        {
            if (!ShouldHold(userPauseGate, gates)) return;

            foreach (IPromptInjectionQueue? g in gates) g?.OnPauseWaitStart();
            fenceHold?.Invoke(true);
            try
            {
                logger.LogInfo(
                    "⏸ Paused — holding before the next round starts (thinking/tool execution " +
                    "already in flight is never affected) until resumed — either the host UI's own " +
                    "explicit \"⏸ Pause\"/\"▶ Resume\" button, or the hold of the 💉 Inject window, " +
                    "whichever is currently holding it.");

                while (ShouldHold(userPauseGate, gates))
                    await Task.Delay(200, ct).ConfigureAwait(false);

                logger.LogInfo("▶ Resumed — continuing to the next round.");
            }
            finally
            {
                fenceHold?.Invoke(false);
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
                InputTokens                = u["input_tokens"]?.GetValue<int>()                   ?? 0,
                OutputTokens               = u["output_tokens"]?.GetValue<int>()                  ?? 0,
                CacheCreationInputTokens   = u["cache_creation_input_tokens"]?.GetValue<int>()    ?? 0,
                CacheCreation1hInputTokens = u["cache_creation_1h_input_tokens"]?.GetValue<int>() ?? 0,
                CacheReadInputTokens       = u["cache_read_input_tokens"]?.GetValue<int>()        ?? 0,
                CacheTtl1h                 = u["cache_ttl_1h"]?.GetValue<bool>()                  ?? false,
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
