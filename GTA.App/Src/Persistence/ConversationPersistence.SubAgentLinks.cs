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
    /// <summary>Every Sub Agent session title starts with this (see LlmChatWindow.BuildSubAgentTwinTitle).</summary>
    public const string SubAgentTitlePrefix = "🤖 Sub Agent";

    /// <summary>
    /// One-time repair for Sub Agent sessions that were saved BEFORE ChatSession.ParentSessionId existed. Such a
    /// session has no link on disk, so it would come back as a loose top-level chat after a restart. The only
    /// record of who dispatched it is the "subAgentDispatches" strip (twinSessionId) inside the DISPATCHER's
    /// session.json. This finds that dispatcher, sets ParentSessionId and patches metadata.json, so the repair
    /// runs only once per session — the next start reads the link directly from metadata.json.
    /// Returns the number of sessions linked. Never throws.
    /// </summary>
    public static int LinkLegacySubAgentSessions(IReadOnlyList<ChatSession> sessions)
    {
        try
        {
            var orphans = sessions
                .Where(s => s.ParentSessionId == null
                            && s.ParentTaskId == null
                            && s.Title.StartsWith(SubAgentTitlePrefix, StringComparison.Ordinal))
                .ToList();
            if (orphans.Count == 0) return 0;

            int linked = 0;

            foreach (var candidate in sessions)
            {
                if (orphans.Count == 0) break;

                string file = SessionDataFile(candidate.Id);
                if (!File.Exists(file)) continue;

                string raw;
                try { raw = File.ReadAllText(file, Encoding.UTF8); }
                catch { continue; }

                // Only a session that dispatched at least one Sub Agent has this key.
                if (raw.IndexOf("twinSessionId", StringComparison.Ordinal) < 0) continue;

                foreach (var twin in orphans.ToList())
                {
                    if (ReferenceEquals(twin, candidate)) continue;
                    if (raw.IndexOf(twin.Id, StringComparison.Ordinal) < 0) continue;

                    twin.ParentSessionId = candidate.Id;
                    PatchMetadataParentSessionId(twin);
                    orphans.Remove(twin);
                    linked++;
                }
            }

            if (linked > 0)
                SessionFileLogger.Log("PERSISTENCE",
                    $"Linked {linked} legacy Sub Agent session(s) to the session that dispatched them.");
            return linked;
        }
        catch (Exception ex)
        {
            SessionFileLogger.Log("PERSISTENCE", $"LinkLegacySubAgentSessions failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Writes "parentSessionId" into an existing metadata.json (tmp file → rename, like every other write).</summary>
    private static void PatchMetadataParentSessionId(ChatSession session)
    {
        try
        {
            string path = MetadataFile(session.Id);
            if (!File.Exists(path)) return;

            var obj = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))?.AsObject();
            if (obj == null) return;

            obj["parentSessionId"] = session.ParentSessionId;

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, obj.ToJsonString(_writeOpts), Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            SessionFileLogger.Log("PERSISTENCE",
                $"Could not patch parentSessionId into metadata of '{session.Id}': {ex.Message}");
        }
    }
}
