using System;
using System.IO;
using System.Text.Json;

namespace GTA.App;

public static partial class ConversationPersistence
{
    private static string SessionsDir =>
        Path.Combine(AppContext.BaseDirectory, "sessions");

    private static string SessionDir(string sessionId) =>
        Path.Combine(SessionsDir, sessionId);

    private static string MetadataFile(string sessionId) =>
        Path.Combine(SessionDir(sessionId), "metadata.json");

    private static string SessionDataFile(string sessionId) =>
        Path.Combine(SessionDir(sessionId), "session.json");

    /// <summary>Folder where image evidence extracted from tool results is saved for a given session (see GTA.Tools.ImageEvidenceStore).</summary>
    public static string EvidenceDir(string sessionId) =>
        Path.Combine(SessionDir(sessionId), "evidence");

    public static string PastedImagesDir(string sessionId) =>
        Path.Combine(SessionDir(sessionId), "pasted");

    private static string LegacyFile =>
        Path.Combine(SessionsDir, "chat_sessions.json");

    private static string LegacyMigratedFile =>
        Path.Combine(SessionsDir, "chat_sessions.json.migrated");

    private static readonly JsonSerializerOptions _writeOpts =
        new JsonSerializerOptions { WriteIndented = true };

    private const int MaxMigratedSessions = 30;

    /// <summary>Returns true if the legacy single-file format exists and has not yet been migrated to the per-session folder format.</summary>
    public static bool LegacyFileNeedsMigration =>
        File.Exists(LegacyFile) && !File.Exists(LegacyMigratedFile);
}
