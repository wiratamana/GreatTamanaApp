using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using GTA.Core;

namespace GTA.App;

public static partial class ConversationPersistence
{
    /// <summary>Enumerate all per-session metadata files and return lightweight ChatSession stubs (no messages or API history loaded yet).</summary>
    public static List<ChatSession> LoadSessionSummaries()
    {
        var result = new List<ChatSession>();

        if (!Directory.Exists(SessionsDir))
            return result;

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.GetDirectories(SessionsDir);
        }
        catch (Exception ex)
        {
            SessionFileLogger.Log("PERSISTENCE", $"Failed to enumerate sessions: {ex.Message}");
            return result;
        }

        foreach (string dir in dirs)
        {
            string metaPath = Path.Combine(dir, "metadata.json");
            if (!File.Exists(metaPath)) continue;

            try
            {
                string raw = File.ReadAllText(metaPath, Encoding.UTF8);
                var obj = JsonNode.Parse(raw)?.AsObject();
                if (obj == null) continue;

                var stub = DeserializeStub(obj);
                if (stub != null)
                    result.Add(stub);
            }
            catch (Exception ex)
            {
                SessionFileLogger.Log("PERSISTENCE",
                    $"Skipping corrupt metadata in '{dir}': {ex.Message}");
            }
        }

        result.Sort((a, b) => b.LastModifiedAt.CompareTo(a.LastModifiedAt));

        SessionFileLogger.Log("PERSISTENCE",
            $"Loaded {result.Count} session stub(s) from metadata files.");
        return result;
    }

    /// <summary>Fully load a session: parse session.json and populate Messages and ConversationHistory.</summary>
    public static void LoadSessionData(ChatSession session)
    {
        string filePath = SessionDataFile(session.Id);
        if (!File.Exists(filePath))
        {
            SessionFileLogger.Log("PERSISTENCE",
                $"No session.json found for '{session.Title}' — treating as empty.");
            session.IsFullyLoaded = true;
            return;
        }

        try
        {
            // Parse straight from the file stream: no second copy of the whole file as a (UTF-16) string.
            JsonObject? obj;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024))
                obj = JsonNode.Parse(fs)?.AsObject();
            if (obj == null)
            {
                session.IsFullyLoaded = true;
                return;
            }

            session.Messages.Clear();
            var msgsArr = obj["displayMessages"]?.AsArray();
            if (msgsArr != null)
            {
                foreach (JsonNode? node in msgsArr)
                {
                    var msg = DeserializeMessage(node);
                    if (msg != null)
                        session.Messages.Add(msg);
                }
            }

            for (int i = session.ConversationHistory.Count - 1; i >= 0; i--)
                session.ConversationHistory.RemoveAt(i);

            var histArr = obj["conversationHistory"]?.AsArray();
            if (histArr != null)
            {
                // DeepClone on purpose: a node parsed from the stream is a thin view over the WHOLE file's bytes
                // (JsonDocument). The clone copies only its own slice, so the big parsed document can be freed.
                foreach (JsonNode? node in histArr)
                {
                    if (node != null)
                        session.ConversationHistory.Add(node.DeepClone());
                }
            }

            // NOTE: "isPaused" is deliberately NOT read here. Whether a session is paused is decided at app start from the
            // pause file (see LlmChatWindow.RestorePauseState); a flag in an old session.json must not override it.
            session.TurnInProgress = obj["turnInProgress"]?.GetValue<bool>() ?? false;
            RestorePreloadedTools(session, obj);

            // Sync title from full file (may differ from stub if renamed)
            if (obj["title"]?.GetValue<string>() is string title
                && !string.IsNullOrEmpty(title))
                session.Title = title;

            session.IsFullyLoaded = true;

            SessionFileLogger.Log("PERSISTENCE",
                $"Loaded session '{session.Title}' " +
                $"({session.Messages.Count} display msgs, " +
                $"{session.ConversationHistory.Count} history entries).");
        }
        catch (Exception ex)
        {
            SessionFileLogger.Log("PERSISTENCE",
                $"Failed to load session '{session.Id}': {ex.Message}");
            session.IsFullyLoaded = true; // prevent retry loop
        }
    }

    /// <summary>The tools the user preloaded (and the ones the AI loaded itself) stay with the chat after an app restart.</summary>
    private static void RestorePreloadedTools(ChatSession session, JsonObject obj)
    {
        if (obj["preloadedToolNames"] is JsonArray arr)
            foreach (JsonNode? node in arr)
                if (node?.GetValue<string>() is string name && !string.IsNullOrWhiteSpace(name))
                    session.Runtime.PreloadedToolNames.Add(name);

        if (obj["aiLoadedToolNames"] is JsonArray aiArr)
            foreach (JsonNode? node in aiArr)
                if (node?.GetValue<string>() is string aiName && !string.IsNullOrWhiteSpace(aiName))
                    session.Runtime.AiLoadedToolNames.Add(aiName);
    }

    private static ChatSession? DeserializeStub(JsonObject obj)
    {
        try
        {
            string id    = obj["id"]?.GetValue<string>()    ?? Guid.NewGuid().ToString();
            string title = obj["title"]?.GetValue<string>() ?? "Chat";

            DateTimeOffset createdAt = DateTimeOffset.TryParse(
                obj["createdAt"]?.GetValue<string>(), out DateTimeOffset ca)
                ? ca : DateTimeOffset.Now;

            DateTimeOffset lastMod = DateTimeOffset.TryParse(
                obj["lastModifiedAt"]?.GetValue<string>(), out DateTimeOffset lm)
                ? lm : createdAt;

            DateTimeOffset? scheduledAt = DateTimeOffset.TryParse(
                obj["scheduledAt"]?.GetValue<string>(), out DateTimeOffset sch)
                ? sch : (DateTimeOffset?)null;

            var scheduledAttachments = new List<string>();
            var schedAttArr = obj["scheduledAttachments"]?.AsArray();
            if (schedAttArr != null)
                foreach (JsonNode? a in schedAttArr)
                    if (a?.GetValue<string>() is string p)
                        scheduledAttachments.Add(p);

            bool isTask     = obj["isTask"]?.GetValue<bool>()     ?? false;
            bool taskHasRun = obj["taskHasRun"]?.GetValue<bool>() ?? false;
            var  taskUnits  = DeserializeTaskUnits(obj["taskUnits"]?.AsArray());

            DateTimeOffset? taskScheduledAt = DateTimeOffset.TryParse(
                obj["taskScheduledAt"]?.GetValue<string>(), out DateTimeOffset tsch)
                ? tsch : (DateTimeOffset?)null;

            string? parentTaskId = obj["parentTaskId"]?.GetValue<string>();

            string? parentSessionId = obj["parentSessionId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(parentSessionId)) parentSessionId = null;

            string? taskBackendName = obj["taskBackendName"]?.GetValue<string>();

            bool isPaused = obj["isPaused"]?.GetValue<bool>() ?? false;

            var stubSession = new ChatSession
            {
                Id                   = id,
                Title                = title,
                CreatedAt            = createdAt,
                LastModifiedAt       = lastMod,
                Preview              = obj["preview"]?.GetValue<string>()   ?? string.Empty,
                MessageCount         = obj["messageCount"]?.GetValue<int>() ?? 0,
                IsFullyLoaded        = false,   // data NOT loaded yet
                ScheduledAt          = scheduledAt,
                ScheduledPrompt      = obj["scheduledPrompt"]?.GetValue<string>(),
                ScheduledAttachments = scheduledAttachments,
                IsTask               = isTask,
                TaskUnits            = taskUnits,
                TaskHasRun           = taskHasRun,
                TaskScheduledAt      = taskScheduledAt,
                ParentTaskId         = parentTaskId,
                ParentSessionId      = parentSessionId,
                TaskBackendName      = taskBackendName,
                IsPaused             = isPaused,
                TurnInProgress       = obj["turnInProgress"]?.GetValue<bool>() ?? false,
            };

            stubSession.Runtime.SetUserPaused(isPaused);
            RestorePreloadedTools(stubSession, obj);

            return stubSession;
        }
        catch (Exception ex)
        {
            SessionFileLogger.Log("PERSISTENCE", $"Skipping corrupt stub: {ex.Message}");
            return null;
        }
    }

    private static ChatMessage? DeserializeMessage(JsonNode? node)
    {
        if (node == null) return null;
        try
        {
            var obj = node.AsObject();

            // Parse timestamp — accept both DateTime and DateTimeOffset strings
            DateTimeOffset ts = DateTimeOffset.Now;
            if (obj["timestamp"]?.GetValue<string>() is string tsStr)
            {
                if (DateTimeOffset.TryParse(tsStr, out DateTimeOffset dto))
                    ts = dto;
                else if (DateTime.TryParse(tsStr, out DateTime dt))
                    ts = new DateTimeOffset(dt);
            }

            var msg = new ChatMessage
            {
                Role      = obj["role"]?.GetValue<string>()    ?? "user",
                Content   = obj["content"]?.GetValue<string>() ?? string.Empty,
                Timestamp = ts,
            };

            // Attached images
            var imgs = obj["attachedImages"]?.AsArray();
            if (imgs != null)
                foreach (JsonNode? img in imgs)
                    if (img?.GetValue<string>() is string path)
                        msg.AttachedImages.Add(path);

            // Thinking blocks (present in new-format sessions only)
            var thinkArr = obj["thinkingBlocks"]?.AsArray();
            if (thinkArr != null)
            {
                foreach (JsonNode? tb in thinkArr)
                {
                    if (tb == null) continue;
                    var tbObj = tb.AsObject();

                    DateTimeOffset tbTs = ts;
                    if (DateTimeOffset.TryParse(tbObj["timestamp"]?.GetValue<string>(),
                            out DateTimeOffset tdto))
                        tbTs = tdto;

                    int tbSeq = tbObj["sequenceIndex"]?.GetValue<int>() ?? 0;

                    msg.ThinkingBlocks.Add(new DisplayThinkingBlock
                    {
                        Text          = tbObj["text"]?.GetValue<string>() ?? string.Empty,
                        Timestamp     = tbTs,
                        SequenceIndex = tbSeq,
                    });
                }
            }

            // Tool calls (present in new-format sessions only)
            var toolArr = obj["toolCalls"]?.AsArray();
            if (toolArr != null)
            {
                foreach (JsonNode? tc in toolArr)
                {
                    if (tc == null) continue;
                    var tcObj = tc.AsObject();

                    DateTimeOffset startedAt   = ts;
                    DateTimeOffset completedAt = ts;
                    if (DateTimeOffset.TryParse(tcObj["startedAt"]?.GetValue<string>(),
                            out DateTimeOffset sa)) startedAt   = sa;
                    if (DateTimeOffset.TryParse(tcObj["completedAt"]?.GetValue<string>(),
                            out DateTimeOffset ea)) completedAt = ea;

                    int tcSeq = tcObj["sequenceIndex"]?.GetValue<int>() ?? 0;

                    msg.ToolCalls.Add(new DisplayToolCall
                    {
                        ToolName      = tcObj["toolName"]?.GetValue<string>()  ?? string.Empty,
                        InputJson     = tcObj["inputJson"]?.GetValue<string>()  ?? "{}",
                        Result        = tcObj["result"]?.GetValue<string>()     ?? string.Empty,
                        StartedAt     = startedAt,
                        CompletedAt   = completedAt,
                        SequenceIndex = tcSeq,
                    });
                }
            }

            // Text segments — present in sessions saved after the timeline fix.
            var segArr = obj["textSegments"]?.AsArray();
            if (segArr != null)
            {
                foreach (JsonNode? seg in segArr)
                {
                    if (seg == null) continue;
                    var segObj = seg.AsObject();

                    DateTimeOffset segTs = ts;
                    if (DateTimeOffset.TryParse(segObj["timestamp"]?.GetValue<string>(),
                            out DateTimeOffset sdto))
                        segTs = sdto;

                    int segSeq = segObj["sequenceIndex"]?.GetValue<int>() ?? 0;

                    msg.TextSegments.Add(new DisplayTextSegment
                    {
                        Text          = segObj["text"]?.GetValue<string>() ?? string.Empty,
                        Timestamp     = segTs,
                        SequenceIndex = segSeq,
                    });
                }
            }

            var injectArr = obj["promptInjections"]?.AsArray();
            if (injectArr != null)
            {
                foreach (JsonNode? pi in injectArr)
                {
                    if (pi == null) continue;
                    var piObj = pi.AsObject();

                    DateTimeOffset piTs = ts;
                    if (DateTimeOffset.TryParse(piObj["timestamp"]?.GetValue<string>(),
                            out DateTimeOffset pdto))
                        piTs = pdto;

                    var files = new List<string>();
                    var filesArr = piObj["files"]?.AsArray();
                    if (filesArr != null)
                        foreach (JsonNode? f in filesArr)
                            if (f?.GetValue<string>() is string fp)
                                files.Add(fp);

                    var toolNames = new List<string>();
                    var toolNamesArr = piObj["toolNamesToLoad"]?.AsArray();
                    if (toolNamesArr != null)
                        foreach (JsonNode? t in toolNamesArr)
                            if (t?.GetValue<string>() is string tn)
                                toolNames.Add(tn);

                    msg.PromptInjections.Add(new DisplayPromptInjection
                    {
                        Text            = piObj["text"]?.GetValue<string>() ?? string.Empty,
                        Files           = files,
                        ToolNamesToLoad = toolNames,
                        Timestamp       = piTs,
                        SequenceIndex   = piObj["sequenceIndex"]?.GetValue<int>() ?? 0,
                    });
                }
            }

            var jiraArr = obj["jiraInjections"]?.AsArray();
            if (jiraArr != null)
            {
                foreach (JsonNode? ji in jiraArr)
                {
                    if (ji == null) continue;
                    var jiObj = ji.AsObject();

                    msg.JiraInjections.Add(new DisplayJiraInjection
                    {
                        TicketKey = jiObj["ticketKey"]?.GetValue<string>() ?? string.Empty,
                        Content   = jiObj["content"]?.GetValue<string>()   ?? string.Empty,
                    });
                }
            }

            var subAgentArr = obj["subAgentDispatches"]?.AsArray();
            if (subAgentArr != null)
            {
                foreach (JsonNode? sad in subAgentArr)
                {
                    if (sad == null) continue;
                    var sadObj = sad.AsObject();

                    msg.SubAgentDispatches.Add(new DisplaySubAgentDispatch
                    {
                        TwinSessionId = sadObj["twinSessionId"]?.GetValue<string>() ?? string.Empty,
                        Task          = sadObj["task"]?.GetValue<string>()          ?? string.Empty,
                        Success       = sadObj["success"]?.GetValue<bool>()         ?? false,
                        Message       = sadObj["message"]?.GetValue<string>()       ?? string.Empty,
                        SequenceIndex = sadObj["sequenceIndex"]?.GetValue<int>()    ?? 0,
                    });
                }
            }

            // Round markers (front-end only, see DisplayRound) — absent in sessions saved before this feature.
            var roundArr = obj["rounds"]?.AsArray();
            if (roundArr != null)
            {
                foreach (JsonNode? rd in roundArr)
                {
                    if (rd == null) continue;
                    var rdObj = rd.AsObject();

                    DateTimeOffset rdTs = ts;
                    if (DateTimeOffset.TryParse(rdObj["timestamp"]?.GetValue<string>(),
                            out DateTimeOffset rdto))
                        rdTs = rdto;

                    var round = new DisplayRound
                    {
                        Round          = rdObj["round"]?.GetValue<int>()          ?? 0,
                        SequenceIndex  = rdObj["sequenceIndex"]?.GetValue<int>()  ?? 0,
                        Timestamp      = rdTs,
                        CostUsd        = rdObj["costUsd"]?.GetValue<double>(),
                        ElapsedSeconds = rdObj["elapsedSeconds"]?.GetValue<double>(),
                        KeepAlivePings      = rdObj["keepAlivePings"]?.GetValue<int>()        ?? 0,
                        KeepAliveFailures   = rdObj["keepAliveFailures"]?.GetValue<int>()     ?? 0,
                        KeepAliveReadTokens = rdObj["keepAliveReadTokens"]?.GetValue<int>()   ?? 0,
                        KeepAliveCostUsd    = rdObj["keepAliveCostUsd"]?.GetValue<double>()   ?? 0,
                        KeepAliveLastError  = rdObj["keepAliveLastError"]?.GetValue<string>(),
                    };

                    var ruObj = rdObj["tokenUsage"]?.AsObject();
                    if (ruObj != null)
                    {
                        round.Usage = new TokenUsage
                        {
                            InputTokens                = ruObj["inputTokens"]?.GetValue<int>()                ?? 0,
                            OutputTokens               = ruObj["outputTokens"]?.GetValue<int>()               ?? 0,
                            CacheCreationInputTokens   = ruObj["cacheCreationInputTokens"]?.GetValue<int>()   ?? 0,
                            CacheCreation1hInputTokens = ruObj["cacheCreation1hInputTokens"]?.GetValue<int>() ?? 0,
                            CacheReadInputTokens       = ruObj["cacheReadInputTokens"]?.GetValue<int>()       ?? 0,
                            CacheTtl1h                 = ruObj["cacheTtl1h"]?.GetValue<bool>()                ?? false,
                        };
                    }

                    msg.Rounds.Add(round);
                }
            }

            var pauseArr = obj["pauseMarkers"]?.AsArray();
            if (pauseArr != null)
            {
                foreach (JsonNode? pn in pauseArr)
                {
                    if (pn == null) continue;
                    var po = pn.AsObject();

                    var marker = new DisplayPauseMarker
                    {
                        SequenceIndex = po["sequenceIndex"]?.GetValue<int>() ?? 0,
                        Outcome       = po["outcome"]?.GetValue<string>() ?? string.Empty,
                    };
                    if (DateTimeOffset.TryParse(po["pausedAt"]?.GetValue<string>(), out DateTimeOffset pa))
                        marker.PausedAt = pa;
                    if (DateTimeOffset.TryParse(po["resumedAt"]?.GetValue<string>(), out DateTimeOffset ra))
                        marker.ResumedAt = ra;
                    msg.PauseMarkers.Add(marker);
                }
            }

            var tuObj = obj["tokenUsage"]?.AsObject();
            if (tuObj != null)
            {
                msg.Usage = new TokenUsage
                {
                    InputTokens                = tuObj["inputTokens"]?.GetValue<int>()                ?? 0,
                    OutputTokens               = tuObj["outputTokens"]?.GetValue<int>()               ?? 0,
                    CacheCreationInputTokens   = tuObj["cacheCreationInputTokens"]?.GetValue<int>()   ?? 0,
                    CacheCreation1hInputTokens = tuObj["cacheCreation1hInputTokens"]?.GetValue<int>() ?? 0,
                    CacheReadInputTokens       = tuObj["cacheReadInputTokens"]?.GetValue<int>()       ?? 0,
                    CacheTtl1h                 = tuObj["cacheTtl1h"]?.GetValue<bool>()                ?? false,
                };
            }

            if (obj["modelName"]?.GetValue<string>() is string savedModelName)
                msg.ModelName = savedModelName;

            if (obj["costUsd"] != null)
                msg.CostUsd = obj["costUsd"]!.GetValue<double>();

            return msg;
        }
        catch (Exception ex)
        {
            SessionFileLogger.Log("PERSISTENCE", $"Skipping corrupt message: {ex.Message}");
            return null;
        }
    }

    private static List<TaskUnit> DeserializeTaskUnits(JsonArray? arr)
    {
        var result = new List<TaskUnit>();
        if (arr == null) return result;

        foreach (JsonNode? node in arr)
        {
            if (node == null) continue;
            try
            {
                var obj = node.AsObject();
                string kindStr = obj["kind"]?.GetValue<string>() ?? "Prompt";
                TaskUnitKind kind = Enum.TryParse<TaskUnitKind>(kindStr, out var k) ? k : TaskUnitKind.Prompt;

                var attachments = new List<string>();
                var attArr = obj["attachments"]?.AsArray();
                if (attArr != null)
                    foreach (JsonNode? a in attArr)
                        if (a?.GetValue<string>() is string p)
                            attachments.Add(p);

                var preloadedTools = new List<string>();
                var toolsArr = obj["preloadedToolNames"]?.AsArray();
                if (toolsArr != null)
                    foreach (JsonNode? t in toolsArr)
                        if (t?.GetValue<string>() is string tn)
                            preloadedTools.Add(tn);

                string? childSessionId = obj["childSessionId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(childSessionId)) childSessionId = null;

                string? chainTaskSessionId = obj["chainTaskSessionId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(chainTaskSessionId)) chainTaskSessionId = null;

                string? chainTaskSessionTitle = obj["chainTaskSessionTitle"]?.GetValue<string>();
                if (string.IsNullOrEmpty(chainTaskSessionTitle)) chainTaskSessionTitle = null;

                DateTime? waitUntil = null;
                string? waitUntilRaw = obj["waitUntil"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(waitUntilRaw) &&
                    DateTime.TryParse(waitUntilRaw, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var parsedWaitUntil))
                    waitUntil = DateTime.SpecifyKind(parsedWaitUntil, DateTimeKind.Local);

                result.Add(new TaskUnit
                {
                    Id                    = obj["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString(),
                    Kind                  = kind,
                    PromptText            = obj["promptText"]?.GetValue<string>() ?? string.Empty,
                    WaitSeconds           = obj["waitSeconds"]?.GetValue<double>() ?? 60,
                    WaitUntil             = waitUntil,
                    Attachments           = attachments,
                    PreloadedToolNames    = preloadedTools,
                    ChildSessionId        = childSessionId,
                    SubTaskNumber         = obj["subTaskNumber"]?.GetValue<int>() ?? 0,
                    ChainTaskSessionId    = chainTaskSessionId,
                    ChainTaskSessionTitle = chainTaskSessionTitle,
                });
            }
            catch (Exception ex)
            {
                SessionFileLogger.Log("PERSISTENCE", $"Skipping corrupt task unit: {ex.Message}");
            }
        }

        return result;
    }
}
