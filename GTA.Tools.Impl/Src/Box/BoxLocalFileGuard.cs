namespace GTA.Tools.Impl.Box;

/// <summary>
/// Decides which LOCAL files may leave this PC for Box. Same rules as the Teams attachments:
/// secret files, key files and the app's own Config / Credentials folders are never uploaded.
/// </summary>
internal static class BoxLocalFileGuard
{
    /// <summary>Biggest file we upload. Box's simple upload API is meant for files up to 50 MB (bigger needs chunked upload, not built).</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    private static readonly string[] BlockedFileNames = { "init.ini", "secret.ini", "credentials.ini", "my_credentials.dat" };
    private static readonly string[] BlockedExtensions = { ".key", ".pfx", ".p12", ".pem" };
    private static readonly string[] BlockedFolders = { "Config", "Credentials" };

    /// <summary>Null = this file may be uploaded. Otherwise the reason it is refused. <paramref name="fullPath"/> must be a full path of an existing file.</summary>
    public static string? Refusal(string fullPath, long size)
    {
        string name = Path.GetFileName(fullPath);
        string ext = Path.GetExtension(fullPath);

        if (BlockedFileNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            BlockedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return $"\"{name}\" is a secret/key file and is never uploaded";

        string? folder = BlockedFolderOf(fullPath);
        if (folder != null)
            return $"files inside the app's \"{folder}\" folder are never uploaded";

        if (size > MaxFileBytes)
            return $"too big ({size / 1024 / 1024} MB, limit {MaxFileBytes / 1024 / 1024} MB)";

        return null;
    }

    /// <summary>Name of the blocked app folder (Config / Credentials) that contains this path, or null.</summary>
    public static string? BlockedFolderOf(string fullPath)
    {
        foreach (string folder in BlockedFolders)
        {
            string blocked = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, folder)) + Path.DirectorySeparatorChar;
            string probe = fullPath.EndsWith(Path.DirectorySeparatorChar) ? fullPath : fullPath + Path.DirectorySeparatorChar;
            if (probe.StartsWith(blocked, StringComparison.OrdinalIgnoreCase))
                return folder;
        }
        return null;
    }
}
