using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using GTA.Core;

namespace GTA.App;

public static partial class ConversationPersistence
{
    /// <summary>Persist a single session to disk: metadata.json — small summary for the sidebar. session.json — full display messages + API history.</summary>
    public static void SaveSession(ChatSession session)
    {
        try
        {
            string dir = SessionDir(session.Id);
            Directory.CreateDirectory(dir);

            if (session.IsDirty)
            {
                session.LastModifiedAt = DateTimeOffset.Now;
                session.IsDirty        = false;
            }
            session.MessageCount   = session.Messages.Count;

            // Compute preview from first user message if not already set
            if (string.IsNullOrEmpty(session.Preview))
            {
                foreach (var msg in session.Messages)
                {
                    if (msg.Role == "user" && !string.IsNullOrEmpty(msg.Content))
                    {
                        session.Preview = msg.Content.Length > 80
                            ? msg.Content[..80] + "…"
                            : msg.Content;
                        break;
                    }
                }
            }

            var meta = new JsonObject
            {
                ["id"]             = session.Id,
                ["title"]          = session.Title,
                ["createdAt"]      = session.CreatedAt.ToString("O"),
                ["lastModifiedAt"] = session.LastModifiedAt.ToString("O"),
                ["preview"]        = session.Preview,
                ["messageCount"]   = session.MessageCount,
                ["isPaused"]       = session.IsPaused,
                ["turnInProgress"] = session.TurnInProgress,
            };
            if (session.ScheduledAt.HasValue)
            {
                meta["scheduledAt"]     = session.ScheduledAt.Value.ToString("O");
                meta["scheduledPrompt"] = session.ScheduledPrompt ?? string.Empty;
                meta["scheduledAttachments"] = SerializeStringList(session.ScheduledAttachments);
            }
            if (session.IsTask)
            {
                meta["isTask"]     = true;
                meta["taskHasRun"] = session.TaskHasRun;
                meta["taskUnits"]  = SerializeTaskUnits(session.TaskUnits);
                if (session.TaskScheduledAt.HasValue)
                    meta["taskScheduledAt"] = session.TaskScheduledAt.Value.ToString("O");
                if (session.TaskBackendName != null)
                    meta["taskBackendName"] = session.TaskBackendName;
            }
            if (session.ParentTaskId != null)
                meta["parentTaskId"] = session.ParentTaskId;
            if (session.ParentSessionId != null)
                meta["parentSessionId"] = session.ParentSessionId;
            bool wroteMeta = WriteJsonAtomicIfChanged(MetadataFile(session.Id), meta);

            var full = new JsonObject
            {
                ["id"]                  = session.Id,
                ["title"]               = session.Title,
                ["createdAt"]           = session.CreatedAt.ToString("O"),
                ["lastModifiedAt"]      = session.LastModifiedAt.ToString("O"),
                ["displayMessages"]     = SerializeMessages(session.Messages),
                ["conversationHistory"] = session.ConversationHistory.DeepClone(),
                // "⏸ Pause" (see ChatSession.IsPaused's own doc comment).
                ["isPaused"] = session.IsPaused,
                ["turnInProgress"] = session.TurnInProgress,
            };
            if (session.ScheduledAt.HasValue)
            {
                full["scheduledAt"]     = session.ScheduledAt.Value.ToString("O");
                full["scheduledPrompt"] = session.ScheduledPrompt ?? string.Empty;
                full["scheduledAttachments"] = SerializeStringList(session.ScheduledAttachments);
            }
            if (session.IsTask)
            {
                full["isTask"]     = true;
                full["taskHasRun"] = session.TaskHasRun;
                full["taskUnits"]  = SerializeTaskUnits(session.TaskUnits);
                if (session.TaskScheduledAt.HasValue)
                    full["taskScheduledAt"] = session.TaskScheduledAt.Value.ToString("O");
                if (session.TaskBackendName != null)
                    full["taskBackendName"] = session.TaskBackendName;
            }
            if (session.ParentTaskId != null)
                full["parentTaskId"] = session.ParentTaskId;
            if (session.ParentSessionId != null)
                full["parentSessionId"] = session.ParentSessionId;
            bool wroteFull = WriteJsonAtomicIfChanged(SessionDataFile(session.Id), full);

            // Identical content is not rewritten or logged again (the same session is often saved 2-3 times within ms).
            if (wroteMeta || wroteFull)
                SessionFileLogger.Log("PERSISTENCE",
                    $"Saved session '{session.Title}' ({session.Messages.Count} msgs, turnInProgress={session.TurnInProgress}) → {dir}");
            else
                SessionFileLogger.LogVerbose("PERSISTENCE", $"Session '{session.Title}' unchanged, not rewritten.");
        }
        catch (Exception ex)
        {
            SessionFileLogger.LogException($"SaveSession failed for '{session.Title}' ({session.Id})", ex);
        }
    }

    /// <summary>Delete a session's folder (both metadata and full data).</summary>
    public static void DeleteSession(string sessionId)
    {
        try
        {
            string dir = SessionDir(sessionId);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            SessionFileLogger.Log("PERSISTENCE", $"DeleteSession failed: {ex.Message}");
        }
    }

    // Fingerprint (length + string hash) of the last JSON written per file, so identical content is not rewritten.
    private static readonly ConcurrentDictionary<string, long> _lastWrittenFingerprint = new();

    /// <summary>Writes tmp → rename. Returns false (and writes nothing) if the content is identical to the last write.</summary>
    private static bool WriteJsonAtomicIfChanged(string filePath, JsonNode obj)
    {
        string json = obj.ToJsonString(_writeOpts);
        long fingerprint = ((long)json.Length << 32) ^ (uint)json.GetHashCode();

        if (_lastWrittenFingerprint.TryGetValue(filePath, out long previous)
            && previous == fingerprint && File.Exists(filePath))
            return false;

        string tmp = filePath + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Move(tmp, filePath, overwrite: true);
        _lastWrittenFingerprint[filePath] = fingerprint;
        return true;
    }

    private static readonly object _journalLock = new();

    /// <summary>
    /// Appends one entry to sessions/&lt;id&gt;/journal.txt and forces it to disk. Small and append-only, so it is
    /// cheap enough to call BEFORE every risky step (prompt sent, tool about to run). Never throws.
    /// </summary>
    public static void AppendJournal(string sessionId, string kind, string text)
    {
        try
        {
            string dir = SessionDir(sessionId);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "journal.txt");
            // Secrets are redacted the same way as in the other log files.
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {kind}{Environment.NewLine}{SecretRedactor.Redact(text)}{Environment.NewLine}----{Environment.NewLine}";

            lock (_journalLock)
            {
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                byte[] bytes = new UTF8Encoding(false).GetBytes(entry);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex)
        {
            SessionFileLogger.Log("PERSISTENCE", $"AppendJournal failed: {ex.Message}");
        }
    }

    private static JsonArray SerializeMessages(IReadOnlyList<ChatMessage> messages)
    {
        var arr = new JsonArray();

        foreach (var msg in messages)
        {
            // Attached images
            var imgArr = new JsonArray();
            foreach (var img in msg.AttachedImages)
                imgArr.Add(img);

            // Thinking blocks — include SequenceIndex for timeline reconstruction.
            var thinkArr = new JsonArray();
            foreach (var tb in msg.ThinkingBlocks)
            {
                thinkArr.Add(new JsonObject
                {
                    ["text"]          = tb.Text,
                    ["timestamp"]     = tb.Timestamp.ToString("O"),
                    ["sequenceIndex"] = tb.SequenceIndex,
                });
            }

            // Tool calls — include SequenceIndex for timeline reconstruction.
            var toolArr = new JsonArray();
            foreach (var tc in msg.ToolCalls)
            {
                toolArr.Add(new JsonObject
                {
                    ["toolName"]      = tc.ToolName,
                    ["inputJson"]     = tc.InputJson,
                    ["result"]        = tc.Result,
                    ["startedAt"]     = tc.StartedAt.ToString("O"),
                    ["completedAt"]   = tc.CompletedAt.ToString("O"),
                    ["sequenceIndex"] = tc.SequenceIndex,
                });
            }

            // Text segments — each segment is one prose block from the assistant.
            var segArr = new JsonArray();
            foreach (var seg in msg.TextSegments)
            {
                segArr.Add(new JsonObject
                {
                    ["text"]          = seg.Text,
                    ["timestamp"]     = seg.Timestamp.ToString("O"),
                    ["sequenceIndex"] = seg.SequenceIndex,
                });
            }

            var jiraArr = new JsonArray();
            foreach (var ji in msg.JiraInjections)
            {
                jiraArr.Add(new JsonObject
                {
                    ["ticketKey"] = ji.TicketKey,
                    ["content"]   = ji.Content,
                });
            }

            var injectArr = new JsonArray();
            foreach (var pi in msg.PromptInjections)
            {
                injectArr.Add(new JsonObject
                {
                    ["text"]            = pi.Text,
                    ["files"]           = SerializeStringList(pi.Files),
                    ["toolNamesToLoad"] = SerializeStringList(pi.ToolNamesToLoad),
                    ["timestamp"]       = pi.Timestamp.ToString("O"),
                    ["sequenceIndex"]   = pi.SequenceIndex,
                });
            }

            // "dispatch_sub_agent" cosmetic strips (see ChatMessage.SubAgentDispatches /
            // DisplaySubAgentDispatch's own doc comment).
            var subAgentArr = new JsonArray();
            foreach (var sad in msg.SubAgentDispatches)
            {
                subAgentArr.Add(new JsonObject
                {
                    ["twinSessionId"] = sad.TwinSessionId,
                    ["task"]          = sad.Task,
                    ["success"]       = sad.Success,
                    ["message"]       = sad.Message,
                    ["sequenceIndex"] = sad.SequenceIndex,
                });
            }

            // Round markers (front-end only: number + per-round tokens/cost, see DisplayRound).
            var roundArr = new JsonArray();
            foreach (var rd in msg.Rounds)
            {
                var rdObj = new JsonObject
                {
                    ["round"]         = rd.Round,
                    ["sequenceIndex"] = rd.SequenceIndex,
                    ["timestamp"]     = rd.Timestamp.ToString("O"),
                };
                if (rd.Usage.HasValue)
                {
                    var ru = rd.Usage.Value;
                    rdObj["tokenUsage"] = new JsonObject
                    {
                        ["inputTokens"]              = ru.InputTokens,
                        ["outputTokens"]             = ru.OutputTokens,
                        ["cacheCreationInputTokens"] = ru.CacheCreationInputTokens,
                        ["cacheReadInputTokens"]     = ru.CacheReadInputTokens,
                    };
                }
                if (rd.CostUsd.HasValue)        rdObj["costUsd"]        = rd.CostUsd.Value;
                if (rd.ElapsedSeconds.HasValue) rdObj["elapsedSeconds"] = rd.ElapsedSeconds.Value;
                roundArr.Add(rdObj);
            }

            var msgObj = new JsonObject
            {
                ["role"]            = msg.Role,
                ["content"]         = msg.Content,
                ["timestamp"]       = msg.Timestamp.ToString("O"),
                ["attachedImages"]  = imgArr,
                ["thinkingBlocks"]  = thinkArr,
                ["toolCalls"]       = toolArr,
                ["textSegments"]    = segArr,
                ["jiraInjections"]  = jiraArr,
                ["promptInjections"] = injectArr,
                ["rounds"]             = roundArr,
                ["subAgentDispatches"] = subAgentArr,
            };

            if (msg.Usage.HasValue)
            {
                var u = msg.Usage.Value;
                msgObj["tokenUsage"] = new JsonObject
                {
                    ["inputTokens"]              = u.InputTokens,
                    ["outputTokens"]             = u.OutputTokens,
                    ["cacheCreationInputTokens"] = u.CacheCreationInputTokens,
                    ["cacheReadInputTokens"]     = u.CacheReadInputTokens,
                };
            }
            if (msg.ModelName != null)
                msgObj["modelName"] = msg.ModelName;
            if (msg.CostUsd.HasValue)
                msgObj["costUsd"] = msg.CostUsd.Value;

            arr.Add(msgObj);
        }

        return arr;
    }

    // ── Serialize a plain string list (e.g. ChatSession.ScheduledAttachments) ─

    private static JsonArray SerializeStringList(IReadOnlyList<string> items)
    {
        var arr = new JsonArray();
        foreach (var item in items)
            arr.Add(item);
        return arr;
    }

    private static JsonArray SerializeTaskUnits(IReadOnlyList<TaskUnit> units)
    {
        var arr = new JsonArray();
        foreach (var u in units)
        {
            arr.Add(new JsonObject
            {
                ["id"]                 = u.Id,
                ["kind"]               = u.Kind.ToString(),
                ["promptText"]         = u.PromptText,
                ["waitSeconds"]        = u.WaitSeconds,
                // Absolute local-time target of a Wait step (see TaskUnit.WaitUntil) —
                // empty string = none (legacy relative wait), never a JSON null.
                ["waitUntil"]          = u.WaitUntil.HasValue
                    ? u.WaitUntil.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty,
                ["attachments"]        = SerializeStringList(u.Attachments),
                ["preloadedToolNames"] = SerializeStringList(u.PreloadedToolNames),
                ["childSessionId"]     = u.ChildSessionId ?? string.Empty,
                // "insert next" sub-task display number (see
                // TaskUnit.SubTaskNumber) — 0 for a normal top-level step.
                ["subTaskNumber"]      = u.SubTaskNumber,
                ["chainTaskSessionId"]    = u.ChainTaskSessionId ?? string.Empty,
                ["chainTaskSessionTitle"] = u.ChainTaskSessionTitle ?? string.Empty,
            });
        }
        return arr;
    }
}
