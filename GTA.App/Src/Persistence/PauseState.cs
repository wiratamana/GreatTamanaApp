using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GTA.Core;

namespace GTA.App;

/// <summary>
/// One paused piece of work that can be continued later, also after the app was closed, recompiled and started again.
///   kind "turn": one chat turn (normal chat, or a Task step) that stopped at the end of a round.
///   kind "task": a whole Task. It stopped between two steps (Child == null), or in the middle of a step's turn (Child != null).
/// The chat history itself is NOT in here: it is saved in the session's own session.json at the moment of the pause.
/// This entry only holds what is not in the chat file (where to continue, queued injections, Sub Agent state ...).
/// </summary>
public sealed class PauseEntry
{
    public string Kind      { get; set; } = "turn";
    public string SessionId { get; set; } = string.Empty;

    // ── turn ──
    /// <summary>The round that was about to start.</summary>
    public int     NextRound   { get; set; } = 1;
    public string? AgentName   { get; set; }
    public string? BackendName { get; set; }

    /// <summary>True when the turn was paused in the middle of a Sub Agent dispatch (state is in sessions/&lt;id&gt;/pause_dispatch.json).</summary>
    public bool HasDispatch { get; set; }

    /// <summary>Injections that were queued and not yet taken by the agent.</summary>
    public List<PendingInjection> Pending { get; set; } = new();

    // ── task ──
    /// <summary>Index of the step to continue with.</summary>
    public int TaskResumeIndex { get; set; } = -1;

    /// <summary>Messages "forwarded" to the next step (in memory only until now).</summary>
    public List<string> ForwardQueue { get; set; } = new();

    /// <summary>The step chat that was in the middle of its turn when the Task paused (null = paused between steps).</summary>
    public PauseEntry? Child { get; set; }

    // ── runtime only (not saved) ──
    public int InjectionVersion { get; set; }

    // ── JSON ──

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["kind"]      = Kind,
            ["sessionId"] = SessionId,
        };

        if (Kind == "turn")
        {
            o["nextRound"]   = NextRound;
            o["agentName"]   = AgentName;
            o["backendName"] = BackendName;
            o["hasDispatch"] = HasDispatch;

            var pending = new JsonArray();
            foreach (PendingInjection p in Pending)
            {
                pending.Add(new JsonObject
                {
                    ["id"]        = p.Id,
                    ["createdAt"] = p.CreatedAt.ToString("O"),
                    ["text"]      = p.Text,
                    ["files"]     = ToArray(p.Files),
                    ["tools"]     = ToArray(p.ToolNamesToLoad),
                });
            }
            o["pending"] = pending;
        }
        else
        {
            o["taskResumeIndex"] = TaskResumeIndex;
            o["forwardQueue"]    = ToArray(ForwardQueue);
            if (Child != null) o["child"] = Child.ToJson();
        }

        return o;
    }

    public static PauseEntry? FromJson(JsonObject o)
    {
        string id = o["sessionId"]?.GetValue<string>() ?? string.Empty;
        if (id.Length == 0) return null;

        var e = new PauseEntry
        {
            Kind      = o["kind"]?.GetValue<string>() ?? "turn",
            SessionId = id,
        };

        if (e.Kind == "turn")
        {
            e.NextRound   = o["nextRound"]?.GetValue<int>() ?? 1;
            e.AgentName   = o["agentName"]?.GetValue<string>();
            e.BackendName = o["backendName"]?.GetValue<string>();
            e.HasDispatch = o["hasDispatch"]?.GetValue<bool>() ?? false;

            if (o["pending"] is JsonArray arr)
            {
                foreach (JsonNode? n in arr)
                {
                    if (n is not JsonObject po) continue;
                    var p = new PendingInjection
                    {
                        Id              = po["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                        Text            = po["text"]?.GetValue<string>() ?? string.Empty,
                        Files           = ToList(po["files"]),
                        ToolNamesToLoad = ToList(po["tools"]),
                    };
                    if (DateTimeOffset.TryParse(po["createdAt"]?.GetValue<string>(), out DateTimeOffset ca))
                        p.CreatedAt = ca;
                    e.Pending.Add(p);
                }
            }
        }
        else
        {
            e.TaskResumeIndex = o["taskResumeIndex"]?.GetValue<int>() ?? -1;
            e.ForwardQueue    = ToList(o["forwardQueue"]);
            if (o["child"] is JsonObject co) e.Child = FromJson(co);
        }

        return e;
    }

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (string s in items) a.Add(s);
        return a;
    }

    private static List<string> ToList(JsonNode? node)
    {
        var l = new List<string>();
        if (node is JsonArray a)
            foreach (JsonNode? n in a)
                if (n?.GetValue<string>() is string s) l.Add(s);
        return l;
    }
}

/// <summary>
/// Reads and writes the pause files:
///   sessions/pause_state.json                     — the list of <see cref="PauseEntry"/> (one per paused chat / Task)
///   sessions/&lt;sessionId&gt;/pause_dispatch.json — Sub Agent state of a turn paused in the middle of a dispatch
/// The pause file is rewritten every time the set of paused work changes and deleted when nothing is paused any more.
/// If it exists when the app starts, the app offers "▶ Resume". Every write goes to a temp file first, then replaces the
/// real file, so a crash can never leave half a file.
/// </summary>
public static class PauseStateStore
{
    private static readonly object _lock = new();

    public static string FilePath =>
        Path.Combine(ConversationPersistence.GetSessionsRootDirectory(), "pause_state.json");

    private static string DispatchFile(string sessionId) =>
        Path.Combine(ConversationPersistence.GetSessionDirectory(sessionId), "pause_dispatch.json");

    public static bool Exists => File.Exists(FilePath);

    public static void Save(IEnumerable<PauseEntry> entries)
    {
        try
        {
            var arr = new JsonArray();
            foreach (PauseEntry e in entries) arr.Add(e.ToJson());

            if (arr.Count == 0)
            {
                Delete();
                return;
            }

            var root = new JsonObject
            {
                ["version"] = 1,
                ["savedAt"] = DateTimeOffset.Now.ToString("O"),
                ["items"]   = arr,
            };

            WriteAtomic(FilePath, root);
            SessionFileLogger.Log("PAUSE", $"Pause file written: {arr.Count} item(s) → {FilePath}");
        }
        catch (Exception ex)
        {
            SessionFileLogger.LogException("Writing the pause file failed", ex);
        }
    }

    public static List<PauseEntry> Load()
    {
        var result = new List<PauseEntry>();
        try
        {
            if (!File.Exists(FilePath)) return result;

            JsonObject? root;
            lock (_lock)
            {
                using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                root = JsonNode.Parse(fs)?.AsObject();
            }

            if (root?["items"] is JsonArray items)
            {
                foreach (JsonNode? n in items)
                {
                    if (n is not JsonObject o) continue;
                    PauseEntry? e = PauseEntry.FromJson(o);
                    if (e != null) result.Add(e);
                }
            }
        }
        catch (Exception ex)
        {
            SessionFileLogger.LogException("Reading the pause file failed (it is ignored)", ex);
            result.Clear();
        }
        return result;
    }

    public static void Delete()
    {
        try
        {
            lock (_lock)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            SessionFileLogger.Log("PAUSE", "Pause file deleted (nothing is paused any more).");
        }
        catch (Exception ex)
        {
            SessionFileLogger.LogException("Deleting the pause file failed", ex);
        }
    }

    public static void SaveDispatch(string sessionId, DispatchResumeState state)
    {
        try
        {
            Directory.CreateDirectory(ConversationPersistence.GetSessionDirectory(sessionId));
            WriteAtomic(DispatchFile(sessionId), state.ToJson());
        }
        catch (Exception ex)
        {
            SessionFileLogger.LogException($"Writing pause_dispatch.json failed for {sessionId}", ex);
        }
    }

    public static DispatchResumeState? LoadDispatch(string sessionId)
    {
        try
        {
            string path = DispatchFile(sessionId);
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            JsonObject? o = JsonNode.Parse(fs)?.AsObject();
            return o == null ? null : DispatchResumeState.FromJson(o);
        }
        catch (Exception ex)
        {
            SessionFileLogger.LogException($"Reading pause_dispatch.json failed for {sessionId}", ex);
            return null;
        }
    }

    public static void DeleteDispatch(string sessionId)
    {
        try
        {
            string path = DispatchFile(sessionId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* leftover file is overwritten by the next pause */ }
    }

    private static void WriteAtomic(string path, JsonNode node)
    {
        lock (_lock)
        {
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024))
            using (var w  = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
            {
                node.WriteTo(w);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
    }
}
