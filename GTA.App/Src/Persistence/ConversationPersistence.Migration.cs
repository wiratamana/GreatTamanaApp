using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.App;

public static partial class ConversationPersistence
{
    /// <summary>One-time migration from the legacy single-file format to per-session folders.</summary>
    public static void MigrateLegacyFileIfNeeded(Action<string>? log = null)
    {
        if (!File.Exists(LegacyFile))        return;
        if (File.Exists(LegacyMigratedFile)) return;

        long size = new FileInfo(LegacyFile).Length;
        log?.Invoke($"PERSISTENCE: Migrating legacy file ({size / 1024 / 1024} MB)…");

        try
        {
            string raw     = File.ReadAllText(LegacyFile, Encoding.UTF8);
            var    root    = JsonNode.Parse(raw)?.AsObject();
            var    sessArr = root?["sessions"]?.AsArray();

            if (sessArr == null)
            {
                MoveLegacyToMigrated();
                return;
            }

            int total    = sessArr.Count;
            int limit    = Math.Min(total, MaxMigratedSessions);
            int migrated = 0;

            for (int i = 0; i < limit; i++)
            {
                JsonNode? node = sessArr[i];
                if (node == null) continue;

                try
                {
                    var    obj   = node.AsObject();
                    string id    = obj["id"]?.GetValue<string>()    ?? Guid.NewGuid().ToString();
                    string title = obj["title"]?.GetValue<string>() ?? "Chat";

                    // Skip sessions that already have a per-session folder
                    if (Directory.Exists(SessionDir(id))) { migrated++; continue; }

                    DateTimeOffset createdAt = DateTimeOffset.TryParse(
                        obj["createdAt"]?.GetValue<string>(), out DateTimeOffset ca)
                        ? ca
                        : DateTimeOffset.Now.AddSeconds(-i); // stagger if unknown

                    var session = new ChatSession
                    {
                        Id            = id,
                        Title         = title,
                        CreatedAt     = createdAt,
                        IsFullyLoaded = true,
                    };

                    // Restore display messages (old format: no thinkingBlocks/toolCalls)
                    var msgsArr = obj["messages"]?.AsArray();
                    if (msgsArr != null)
                    {
                        foreach (JsonNode? m in msgsArr)
                        {
                            var msg = DeserializeMessage(m);
                            if (msg != null) session.Messages.Add(msg);
                        }
                    }

                    // Restore conversation history
                    var histArr = obj["conversationHistory"]?.AsArray();
                    if (histArr != null)
                    {
                        foreach (JsonNode? h in histArr)
                        {
                            if (h != null)
                                session.ConversationHistory.Add(h.DeepClone());
                        }
                    }

                    SaveSession(session);
                    migrated++;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"PERSISTENCE: Skipping session during migration: {ex.Message}");
                }
            }

            MoveLegacyToMigrated();
            log?.Invoke($"PERSISTENCE: Migration complete — " +
                        $"{migrated} of {total} sessions migrated " +
                        $"(max = {MaxMigratedSessions}).");
        }
        catch (Exception ex)
        {
            log?.Invoke($"PERSISTENCE: Migration failed: {ex.Message}");
        }

        void MoveLegacyToMigrated()
        {
            try { File.Move(LegacyFile, LegacyMigratedFile, overwrite: true); }
            catch { /* ignore — do not crash if rename fails */ }
        }
    }
}
