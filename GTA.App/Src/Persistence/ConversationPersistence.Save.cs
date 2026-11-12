using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

            // An UNLOADED session (see LlmChatWindow.UnloadSessionIfIdle) has empty Messages / ConversationHistory in
            // memory. Writing those would WIPE the real chat on disk. For such a session only the small metadata.json
            // is written (title, task units, flags ...); session.json stays exactly as it is.
            bool canWriteFull = session.IsFullyLoaded;
            if (canWriteFull)
                session.MessageCount = session.Messages.Count;

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
            if (session.Runtime.PreloadedToolNames.Count > 0)
                meta["preloadedToolNames"] = SerializeStringList(new List<string>(session.Runtime.PreloadedToolNames));
            if (session.Runtime.AiLoadedToolNames.Count > 0)
                meta["aiLoadedToolNames"] = SerializeStringList(new List<string>(session.Runtime.AiLoadedToolNames));
            bool wroteMeta = WriteFileStreamed(MetadataFile(session.Id), w => meta.WriteTo(w));

            // session.json is STREAMED to disk piece by piece (no DOM copy of the whole chat, no giant string, no
            // DeepClone of the API history). The old way built 3 copies of the chat in memory and one string of
            // 2x the file size on every save — every round — which fragmented the Large Object Heap for good.
            bool wroteFull = false;
            if (canWriteFull)
            {
                wroteFull = WriteFileStreamed(SessionDataFile(session.Id), w =>
                {
                    w.WriteStartObject();
                    w.WriteString("id",             session.Id);
                    w.WriteString("title",          session.Title);
                    w.WriteString("createdAt",      session.CreatedAt.ToString("O"));
                    w.WriteString("lastModifiedAt", session.LastModifiedAt.ToString("O"));

                    w.WritePropertyName("displayMessages");
                    WriteMessages(w, session.Messages);

                    w.WritePropertyName("conversationHistory");
                    WriteHistory(w, session.ConversationHistory);

                    // "⏸ Pause" (see ChatSession.IsPaused's own doc comment).
                    w.WriteBoolean("isPaused",       session.IsPaused);
                    w.WriteBoolean("turnInProgress", session.TurnInProgress);

                    if (session.ScheduledAt.HasValue)
                    {
                        w.WriteString("scheduledAt",     session.ScheduledAt.Value.ToString("O"));
                        w.WriteString("scheduledPrompt", session.ScheduledPrompt ?? string.Empty);
                        w.WritePropertyName("scheduledAttachments");
                        SerializeStringList(session.ScheduledAttachments).WriteTo(w);
                    }
                    if (session.IsTask)
                    {
                        w.WriteBoolean("isTask",     true);
                        w.WriteBoolean("taskHasRun", session.TaskHasRun);
                        w.WritePropertyName("taskUnits");
                        SerializeTaskUnits(session.TaskUnits).WriteTo(w);
                        if (session.TaskScheduledAt.HasValue)
                            w.WriteString("taskScheduledAt", session.TaskScheduledAt.Value.ToString("O"));
                        if (session.TaskBackendName != null)
                            w.WriteString("taskBackendName", session.TaskBackendName);
                    }
                    if (session.ParentTaskId != null)
                        w.WriteString("parentTaskId", session.ParentTaskId);
                    if (session.ParentSessionId != null)
                        w.WriteString("parentSessionId", session.ParentSessionId);
                    if (session.Runtime.PreloadedToolNames.Count > 0)
                    {
                        w.WritePropertyName("preloadedToolNames");
                        SerializeStringList(new List<string>(session.Runtime.PreloadedToolNames)).WriteTo(w);
                    }
                    if (session.Runtime.AiLoadedToolNames.Count > 0)
                    {
                        w.WritePropertyName("aiLoadedToolNames");
                        SerializeStringList(new List<string>(session.Runtime.AiLoadedToolNames)).WriteTo(w);
                    }
                    w.WriteEndObject();
                });
            }

            // Identical content is not logged again (the same session is often saved 2-3 times within ms).
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

    // SHA-256 fingerprint of the last JSON written per file, so identical content is not "moved in" again.
    private static readonly ConcurrentDictionary<string, string> _lastWrittenFingerprint = new();

    private static readonly JsonWriterOptions _streamWriterOpts = new() { Indented = true };

    /// <summary>
    /// Streams JSON into filePath + ".tmp" (never builds the whole text in memory), then renames it over filePath.
    /// Returns false when the content is identical to the last write of this file (the tmp file is thrown away).
    /// If <paramref name="write"/> throws, the old file stays untouched.
    /// </summary>
    private static bool WriteFileStreamed(string filePath, Action<Utf8JsonWriter> write)
    {
        string tmp = filePath + ".tmp";
        string fingerprint;

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024))
        using (var hs = new HashingWriteStream(fs))
        {
            using (var writer = new Utf8JsonWriter(hs, _streamWriterOpts))
            {
                write(writer);
                writer.Flush();
            }
            hs.Flush();
            fingerprint = hs.GetFingerprint();
        }

        if (_lastWrittenFingerprint.TryGetValue(filePath, out string? previous)
            && previous == fingerprint && File.Exists(filePath))
        {
            try { File.Delete(tmp); } catch { /* leftover tmp is overwritten by the next save */ }
            return false;
        }

        File.Move(tmp, filePath, overwrite: true);
        _lastWrittenFingerprint[filePath] = fingerprint;
        return true;
    }

    /// <summary>Write-only stream that passes everything on to the file and hashes it on the way.</summary>
    private sealed class HashingWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;

        public HashingWriteStream(Stream inner) => _inner = inner;

        public string GetFingerprint() => _length + ":" + Convert.ToBase64String(_hash.GetHashAndReset());

        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            _inner.Write(buffer, offset, count);
            _length += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            _inner.Write(buffer);
            _length += buffer.Length;
        }

        public override void Flush() => _inner.Flush();

        public override bool CanRead  => false;
        public override bool CanSeek  => false;
        public override bool CanWrite => true;
        public override long Length   => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int  Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)       => throw new NotSupportedException();
        public override void SetLength(long value)                      => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
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

    // ── Streaming serializers (same JSON shape as before; read back by ConversationPersistence.Load) ──

    /// <summary>The API history, node by node. Flushes between nodes so the writer never holds the whole history.</summary>
    private static void WriteHistory(Utf8JsonWriter w, JsonArray history)
    {
        w.WriteStartArray();
        foreach (JsonNode? node in history)
        {
            if (node == null) w.WriteNullValue();
            else              node.WriteTo(w);
            w.Flush();
        }
        w.WriteEndArray();
    }

    private static void WriteStringArray(Utf8JsonWriter w, IReadOnlyList<string> items)
    {
        w.WriteStartArray();
        foreach (var item in items) w.WriteStringValue(item);
        w.WriteEndArray();
    }

    private static void WriteTokenUsage(Utf8JsonWriter w, string name, GTA.Core.TokenUsage u)
    {
        w.WriteStartObject(name);
        w.WriteNumber("inputTokens",                u.InputTokens);
        w.WriteNumber("outputTokens",               u.OutputTokens);
        w.WriteNumber("cacheCreationInputTokens",   u.CacheCreationInputTokens);
        w.WriteNumber("cacheCreation1hInputTokens", u.CacheCreation1hInputTokens);
        w.WriteNumber("cacheReadInputTokens",       u.CacheReadInputTokens);
        w.WriteBoolean("cacheTtl1h",                u.CacheTtl1h);
        w.WriteEndObject();
    }

    private static void WriteMessages(Utf8JsonWriter w, IReadOnlyList<ChatMessage> messages)
    {
        w.WriteStartArray();

        foreach (var msg in messages)
        {
            w.WriteStartObject();
            w.WriteString("role",      msg.Role);
            w.WriteString("content",   msg.Content);
            w.WriteString("timestamp", msg.Timestamp.ToString("O"));

            w.WritePropertyName("attachedImages");
            WriteStringArray(w, msg.AttachedImages);

            // Thinking blocks — include SequenceIndex for timeline reconstruction.
            w.WriteStartArray("thinkingBlocks");
            foreach (var tb in msg.ThinkingBlocks)
            {
                w.WriteStartObject();
                w.WriteString("text",        tb.Text);
                w.WriteString("timestamp",   tb.Timestamp.ToString("O"));
                w.WriteNumber("sequenceIndex", tb.SequenceIndex);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.Flush();

            // Tool calls — include SequenceIndex for timeline reconstruction.
            w.WriteStartArray("toolCalls");
            foreach (var tc in msg.ToolCalls)
            {
                w.WriteStartObject();
                w.WriteString("toolName",    tc.ToolName);
                w.WriteString("inputJson",   tc.InputJson);
                w.WriteString("result",      tc.Result);
                w.WriteString("startedAt",   tc.StartedAt.ToString("O"));
                w.WriteString("completedAt", tc.CompletedAt.ToString("O"));
                w.WriteNumber("sequenceIndex", tc.SequenceIndex);
                w.WriteEndObject();
                w.Flush();   // a tool result can be MBs: do not let them pile up in the writer
            }
            w.WriteEndArray();

            // Text segments — each segment is one prose block from the assistant.
            w.WriteStartArray("textSegments");
            foreach (var seg in msg.TextSegments)
            {
                w.WriteStartObject();
                w.WriteString("text",        seg.Text);
                w.WriteString("timestamp",   seg.Timestamp.ToString("O"));
                w.WriteNumber("sequenceIndex", seg.SequenceIndex);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.Flush();

            w.WriteStartArray("jiraInjections");
            foreach (var ji in msg.JiraInjections)
            {
                w.WriteStartObject();
                w.WriteString("ticketKey", ji.TicketKey);
                w.WriteString("content",   ji.Content);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("promptInjections");
            foreach (var pi in msg.PromptInjections)
            {
                w.WriteStartObject();
                w.WriteString("text", pi.Text);
                w.WritePropertyName("files");
                WriteStringArray(w, pi.Files);
                w.WritePropertyName("toolNamesToLoad");
                WriteStringArray(w, pi.ToolNamesToLoad);
                w.WriteString("timestamp",   pi.Timestamp.ToString("O"));
                w.WriteNumber("sequenceIndex", pi.SequenceIndex);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            // Round markers (front-end only: number + per-round tokens/cost, see DisplayRound).
            w.WriteStartArray("rounds");
            foreach (var rd in msg.Rounds)
            {
                w.WriteStartObject();
                w.WriteNumber("round",         rd.Round);
                w.WriteNumber("sequenceIndex", rd.SequenceIndex);
                w.WriteString("timestamp",     rd.Timestamp.ToString("O"));
                if (rd.Usage.HasValue)
                    WriteTokenUsage(w, "tokenUsage", rd.Usage.Value);
                if (rd.CostUsd.HasValue)        w.WriteNumber("costUsd",        rd.CostUsd.Value);
                if (rd.ElapsedSeconds.HasValue) w.WriteNumber("elapsedSeconds", rd.ElapsedSeconds.Value);
                if (rd.KeepAlivePings > 0 || rd.KeepAliveFailures > 0)
                {
                    w.WriteNumber("keepAlivePings",      rd.KeepAlivePings);
                    w.WriteNumber("keepAliveFailures",   rd.KeepAliveFailures);
                    w.WriteNumber("keepAliveReadTokens", rd.KeepAliveReadTokens);
                    w.WriteNumber("keepAliveCostUsd",    rd.KeepAliveCostUsd);
                    if (rd.KeepAliveLastError != null) w.WriteString("keepAliveLastError", rd.KeepAliveLastError);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            // "dispatch_sub_agent" cosmetic strips (see ChatMessage.SubAgentDispatches /
            // DisplaySubAgentDispatch's own doc comment).
            w.WriteStartArray("subAgentDispatches");
            foreach (var sad in msg.SubAgentDispatches)
            {
                w.WriteStartObject();
                w.WriteString("twinSessionId", sad.TwinSessionId);
                w.WriteString("task",          sad.Task);
                w.WriteBoolean("success",      sad.Success);
                w.WriteString("message",       sad.Message);
                w.WriteNumber("sequenceIndex", sad.SequenceIndex);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            // "⏸ Currently paused" widgets (see DisplayPauseMarker).
            w.WriteStartArray("pauseMarkers");
            foreach (var pm in msg.PauseMarkers)
            {
                w.WriteStartObject();
                w.WriteNumber("sequenceIndex", pm.SequenceIndex);
                w.WriteString("pausedAt",      pm.PausedAt.ToString("O"));
                if (pm.ResumedAt.HasValue) w.WriteString("resumedAt", pm.ResumedAt.Value.ToString("O"));
                w.WriteString("outcome",       pm.Outcome);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (msg.Usage.HasValue)
                WriteTokenUsage(w, "tokenUsage", msg.Usage.Value);
            if (msg.ModelName != null)
                w.WriteString("modelName", msg.ModelName);
            if (msg.CostUsd.HasValue)
                w.WriteNumber("costUsd", msg.CostUsd.Value);

            w.WriteEndObject();
            w.Flush();
        }

        w.WriteEndArray();
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
