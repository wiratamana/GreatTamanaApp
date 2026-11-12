using System.Diagnostics;

namespace GTA.Tools.Impl.Box;

/// <summary>What to do when a file with the same name is already in the Box folder.</summary>
public enum BoxIfExists
{
    /// <summary>Upload a NEW VERSION, but only when the content differs (SHA-1 check). Identical content is skipped.</summary>
    Overwrite,
    /// <summary>Never change an existing file. Identical or different, it is skipped.</summary>
    Skip,
}

public enum BoxEntryStatus
{
    FolderCreated,
    FolderExists,
    Created,
    NewVersion,
    Identical,
    SkippedDifferent,
    Refused,
    Failed,
    /// <summary>Exists in Box but NOT in the local folder (deleted locally?). Only reported: the upload never deletes anything.</summary>
    BoxOnly,
}

/// <summary>One line of a folder upload report. Path is relative to the uploaded folder (with the folder's own name first).</summary>
public sealed record BoxUploadEntry(string Path, BoxEntryStatus Status, string Detail, long Bytes);

public sealed class BoxFolderUploadReport
{
    public string LocalFolder { get; init; } = "";
    public string BoxParentFolderId { get; init; } = "";
    /// <summary>Id of the folder in Box that holds the upload. Null in a dry run when that folder does not exist yet.</summary>
    public string? RootFolderId { get; set; }
    public string RootFolderName { get; init; } = "";
    public bool DryRun { get; init; }
    public BoxIfExists IfExists { get; init; }
    public List<BoxUploadEntry> Entries { get; } = new();
    /// <summary>Set when the run stopped early (e.g. 5 failures in a row).</summary>
    public string? AbortReason { get; set; }
    public TimeSpan Elapsed { get; set; }

    public int Count(BoxEntryStatus s) => Entries.Count(e => e.Status == s);
    public long BytesSent => Entries.Where(e => e.Status is BoxEntryStatus.Created or BoxEntryStatus.NewVersion).Sum(e => e.Bytes);
}

/// <summary>
/// Public, synchronous entry point for Box uploads. The tool layer (GTA.Tools) only talks to this class.
/// Every method throws on failure (message = what went wrong); the tool layer turns that into text for the AI.
/// Uploading CHANGES data in Box and cannot be undone by this app (an overwrite keeps the old version in Box's version history).
/// </summary>
public static class BoxUploadApi
{
    /// <summary>Most files one folder upload may contain. Stops the AI from uploading a whole drive into Box by mistake.</summary>
    public const int MaxFolderFiles = 2000;

    /// <summary>Most bytes one folder upload may contain.</summary>
    public const long MaxFolderBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Deepest sub-folder level we walk.</summary>
    public const int MaxFolderDepth = 30;

    /// <summary>After this many failures IN A ROW the folder upload stops (network down, session dead, no permission...).</summary>
    public const int MaxConsecutiveFailures = 5;

    /// <summary>Biggest single file we upload (Box's simple upload API limit).</summary>
    public const long MaxFileBytes = BoxLocalFileGuard.MaxFileBytes;

    // ------------------------------------------------------------------
    // One file
    // ------------------------------------------------------------------

    /// <summary>
    /// Uploads a local file into a Box folder. Name free -> Created. Name exists -> SHA-1 compare:
    /// same content -> IdenticalSkipped; different -> NewVersion (Overwrite) or ExistsSkipped (Skip).
    /// </summary>
    public static BoxSyncResult UploadNewFile(string folderId, string localPath, string? fileName = null, BoxIfExists ifExists = BoxIfExists.Overwrite)
    {
        RequireNumeric(folderId, "folder_id");
        (_, byte[] bytes, string defaultName) = ReadAllowedFile(localPath);
        string name = ResolveName(fileName, defaultName);
        return BoxClientRunner.Run(c => c.UploadOrUpdateAsync(folderId.Trim(), name, bytes, ifExists == BoxIfExists.Overwrite));
    }

    /// <summary>
    /// Uploads a local file as a NEW VERSION of an existing Box file, unless the content is identical (then nothing is sent).
    /// The file keeps its Box name unless <paramref name="fileName"/> is given.
    /// </summary>
    public static BoxSyncResult UploadNewVersion(string fileId, string localPath, string? fileName = null)
    {
        RequireNumeric(fileId, "file_id");
        (_, byte[] bytes, _) = ReadAllowedFile(localPath);
        string? explicitName = string.IsNullOrWhiteSpace(fileName) ? null : ResolveName(fileName, "");
        return BoxClientRunner.Run(c => c.UpdateExistingAsync(fileId.Trim(), explicitName, bytes, overwrite: true));
    }

    // ------------------------------------------------------------------
    // A whole folder
    // ------------------------------------------------------------------

    /// <summary>
    /// Uploads a local folder (all sub-folders and files) as a NEW sub-folder of <paramref name="parentFolderId"/>, named like the local folder.
    /// Sub-folders that already exist in Box are reused. Files follow <paramref name="ifExists"/> (SHA-1 checked).
    /// With <paramref name="dryRun"/> nothing is created or uploaded: the report says what WOULD happen.
    /// A failing file does not stop the run (see <see cref="MaxConsecutiveFailures"/>); it is listed as Failed in the report.
    /// </summary>
    public static BoxFolderUploadReport UploadFolder(
        string parentFolderId, string localFolder, BoxIfExists ifExists = BoxIfExists.Overwrite,
        bool dryRun = false, Action<string>? progress = null)
    {
        RequireNumeric(parentFolderId, "parent_folder_id");
        if (string.IsNullOrWhiteSpace(localFolder)) throw new ArgumentException("local_folder is required.");
        parentFolderId = parentFolderId.Trim();

        string root = Path.GetFullPath(localFolder.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Folder not found: \"{root}\".");
        if (root.Length <= 3 || Path.GetPathRoot(root + Path.DirectorySeparatorChar)!.Length >= root.Length)
            throw new InvalidOperationException($"Refused: \"{root}\" is a drive root. Give a real folder.");
        string? blockedParent = BoxLocalFileGuard.BlockedFolderOf(root);
        if (blockedParent != null)
            throw new InvalidOperationException($"Refused: the app's \"{blockedParent}\" folder (or anything inside it) is never uploaded.");

        // 1. Look at the local tree FIRST (no network): limits and refused files are known before anything is created.
        var scan = new LocalScan();
        LocalDir tree = ScanDirectory(root, 0, scan);
        if (scan.FileCount > MaxFolderFiles)
            throw new InvalidOperationException($"Refused: {scan.FileCount} files is more than the limit of {MaxFolderFiles} per folder upload.");
        if (scan.Bytes > MaxFolderBytes)
            throw new InvalidOperationException($"Refused: {scan.Bytes / 1024 / 1024} MB is more than the limit of {MaxFolderBytes / 1024 / 1024} MB per folder upload.");

        // 2. The target must be readable (this also proves the id is right).
        BoxFolderInfo parent = BoxClientRunner.Run(c => c.GetFolderInfoAsync(parentFolderId));

        var report = new BoxFolderUploadReport
        {
            LocalFolder = root,
            BoxParentFolderId = parentFolderId,
            RootFolderName = tree.Name,
            DryRun = dryRun,
            IfExists = ifExists,
        };
        var run = new UploadRun(report, ifExists == BoxIfExists.Overwrite, dryRun, progress, scan.UploadableCount);
        var sw = Stopwatch.StartNew();
        progress?.Invoke($"{(dryRun ? "[dry run] " : "")}{scan.UploadableCount} file(s), {scan.Bytes / 1024.0 / 1024.0:F1} MB -> Box folder \"{parent.Name}\" ({parentFolderId})");

        try
        {
            // 3. The root folder: reuse when the name already exists in the parent, else create it.
            List<BoxItem> parentItems = BoxClientRunner.Run(c => c.ListAllItemsAsync(parentFolderId));
            string? rootId = parentItems.FirstOrDefault(i => i.Type == "folder" && string.Equals(i.Name, tree.Name, StringComparison.OrdinalIgnoreCase))?.Id;
            if (rootId != null)
                report.Entries.Add(new BoxUploadEntry(tree.Name + "/", BoxEntryStatus.FolderExists, $"folder id {rootId}", 0));
            else if (dryRun)
                report.Entries.Add(new BoxUploadEntry(tree.Name + "/", BoxEntryStatus.FolderCreated, "would create", 0));
            else
            {
                rootId = BoxClientRunner.Run(c => c.CreateFolderAsync(parentFolderId, tree.Name));
                report.Entries.Add(new BoxUploadEntry(tree.Name + "/", BoxEntryStatus.FolderCreated, $"folder id {rootId}", 0));
            }
            report.RootFolderId = rootId;

            // 4. Walk.
            UploadDirectory(tree, rootId, tree.Name, run);
        }
        finally
        {
            report.Elapsed = sw.Elapsed;
        }
        return report;
    }

    // ------------------------------------------------------------------
    // Local scan (no network)
    // ------------------------------------------------------------------

    private sealed class LocalFile
    {
        public string Name = "";
        public string FullPath = "";
        public long Size;
        public string? Refusal;
    }

    private sealed class LocalDir
    {
        public string Name = "";
        public List<LocalFile> Files = new();
        public List<LocalDir> Dirs = new();
        public List<string> Problems = new();   // unreadable / skipped entries
        /// <summary>Name of EVERY local entry (also refused / skipped ones): "exists locally". Used to find what is only in Box.</summary>
        public HashSet<string> AllNames = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LocalScan
    {
        public int FileCount;        // every file found (also the refused ones)
        public int UploadableCount;  // files that pass the guard
        public long Bytes;           // bytes of the uploadable files
    }

    private static LocalDir ScanDirectory(string fullPath, int depth, LocalScan scan)
    {
        var dir = new LocalDir { Name = Path.GetFileName(fullPath) };
        if (depth > MaxFolderDepth)
        {
            dir.Problems.Add($"deeper than {MaxFolderDepth} levels: not walked");
            return dir;
        }

        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(fullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex) { dir.Problems.Add("cannot read folder: " + ex.Message); return dir; }

        foreach (string entry in entries)
        {
            string name = Path.GetFileName(entry);
            dir.AllNames.Add(name);
            FileAttributes attr;
            try { attr = File.GetAttributes(entry); }
            catch (Exception ex) { dir.Problems.Add($"{name}: cannot read ({ex.Message})"); continue; }

            bool isDirectory = (attr & FileAttributes.Directory) != 0;

            // Links can point anywhere (also out of the tree, e.g. into Credentials). Never follow them.
            if ((attr & FileAttributes.ReparsePoint) != 0) { dir.Problems.Add($"{name}: link / junction, not followed"); continue; }

            if (isDirectory)
            {
                dir.Dirs.Add(ScanDirectory(entry, depth + 1, scan));
                continue;
            }

            long size;
            try { size = new FileInfo(entry).Length; }
            catch (Exception ex) { dir.Problems.Add($"{name}: cannot read ({ex.Message})"); continue; }

            string? refusal = BoxLocalFileGuard.Refusal(entry, size);
            dir.Files.Add(new LocalFile { Name = name, FullPath = entry, Size = size, Refusal = refusal });
            scan.FileCount++;
            if (refusal == null) { scan.UploadableCount++; scan.Bytes += size; }
        }
        return dir;
    }

    // ------------------------------------------------------------------
    // Upload walk
    // ------------------------------------------------------------------

    private sealed class UploadRun
    {
        public BoxFolderUploadReport Report { get; }
        public bool Overwrite { get; }
        public bool DryRun { get; }
        public Action<string>? Progress { get; }
        public int TotalFiles { get; }
        public int Done;
        public int ConsecutiveFailures;
        public bool Aborted => Report.AbortReason != null;

        public UploadRun(BoxFolderUploadReport report, bool overwrite, bool dryRun, Action<string>? progress, int totalFiles)
        { Report = report; Overwrite = overwrite; DryRun = dryRun; Progress = progress; TotalFiles = totalFiles; }

        public void Add(BoxUploadEntry e)
        {
            Report.Entries.Add(e);
            if (e.Status == BoxEntryStatus.Failed) ConsecutiveFailures++;
            else if (e.Status is not (BoxEntryStatus.Refused or BoxEntryStatus.FolderCreated or BoxEntryStatus.FolderExists)) ConsecutiveFailures = 0;

            if (e.Status is not (BoxEntryStatus.FolderCreated or BoxEntryStatus.FolderExists or BoxEntryStatus.Refused))
            {
                Done++;
                Progress?.Invoke($"[{Done}/{TotalFiles}] {e.Path} -> {e.Status}{(e.Detail.Length > 0 ? " (" + e.Detail + ")" : "")}");
            }
            if (ConsecutiveFailures >= MaxConsecutiveFailures)
                Report.AbortReason = $"stopped after {MaxConsecutiveFailures} failures in a row (last: {e.Detail})";
        }
    }

    /// <param name="boxFolderId">Id of the matching Box folder. Null only in a dry run, when that folder does not exist yet.</param>
    private static void UploadDirectory(LocalDir dir, string? boxFolderId, string relPath, UploadRun run)
    {
        foreach (string problem in dir.Problems)
            run.Report.Entries.Add(new BoxUploadEntry(relPath + "/", BoxEntryStatus.Refused, problem, 0));

        // One COMPLETE listing per Box folder (all pages). It tells us which names exist, so identical big files are never re-sent.
        var filesByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var foldersByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var boxItems = new List<BoxItem>();
        if (boxFolderId != null)
        {
            string listId = boxFolderId;
            boxItems = BoxClientRunner.Run(c => c.ListAllItemsAsync(listId));
            foreach (BoxItem item in boxItems)
            {
                if (item.Type == "file") filesByName[item.Name] = item.Id;
                else if (item.Type == "folder") foldersByName[item.Name] = item.Id;
            }
        }

        foreach (LocalFile file in dir.Files)
        {
            if (run.Aborted) return;
            string path = relPath + "/" + file.Name;

            if (file.Refusal != null)
            {
                run.Add(new BoxUploadEntry(path, BoxEntryStatus.Refused, file.Refusal, file.Size));
                continue;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(file.FullPath);

                if (run.DryRun)
                {
                    run.Add(PlanFile(path, boxFolderId, file, bytes, filesByName, run.Overwrite));
                    continue;
                }

                string folderId = boxFolderId!;
                BoxSyncResult r = BoxClientRunner.Run(c => c.UploadOrUpdateAsync(folderId, file.Name, bytes, run.Overwrite, filesByName));
                run.Add(ToEntry(path, r, bytes.Length));
            }
            catch (Exception ex)
            {
                run.Add(new BoxUploadEntry(path, BoxEntryStatus.Failed, ex.Message, file.Size));
            }
        }

        foreach (LocalDir sub in dir.Dirs)
        {
            if (run.Aborted) return;
            string path = relPath + "/" + sub.Name;
            string? subId = null;

            if (foldersByName.TryGetValue(sub.Name, out string? existing))
            {
                subId = existing;
                run.Report.Entries.Add(new BoxUploadEntry(path + "/", BoxEntryStatus.FolderExists, $"folder id {subId}", 0));
            }
            else if (run.DryRun)
            {
                run.Report.Entries.Add(new BoxUploadEntry(path + "/", BoxEntryStatus.FolderCreated, "would create", 0));
            }
            else
            {
                try
                {
                    string parentId = boxFolderId!;
                    subId = BoxClientRunner.Run(c => c.CreateFolderAsync(parentId, sub.Name));
                    run.Report.Entries.Add(new BoxUploadEntry(path + "/", BoxEntryStatus.FolderCreated, $"folder id {subId}", 0));
                }
                catch (Exception ex)
                {
                    // The whole sub-tree is skipped: there is no folder to put it in. Other branches continue.
                    run.Add(new BoxUploadEntry(path + "/", BoxEntryStatus.Failed, "cannot create folder: " + ex.Message, 0));
                    continue;
                }
            }

            UploadDirectory(sub, subId, path, run);
        }

        // What is in this Box folder but NOT here any more (probably deleted locally). Only REPORTED, never deleted by this call:
        // the caller decides, and deletes with box_delete_file / box_delete_folder using the ids in the report.
        if (run.Aborted) return;
        foreach (BoxItem item in boxItems)
        {
            bool isFolder = item.Type == "folder";
            if (dir.AllNames.Contains(item.Name)) continue;
            run.Report.Entries.Add(new BoxUploadEntry(
                relPath + "/" + item.Name + (isFolder ? "/" : ""), BoxEntryStatus.BoxOnly,
                $"{(item.Type.Length > 0 ? item.Type : "item")} id {item.Id} exists in Box but not in the local folder", 0));
        }
    }

    private static BoxUploadEntry ToEntry(string path, BoxSyncResult r, long bytes) => r.Outcome switch
    {
        BoxSyncOutcome.Created => new BoxUploadEntry(path, BoxEntryStatus.Created, $"file id {r.FileId}", bytes),
        BoxSyncOutcome.NewVersion => new BoxUploadEntry(path, BoxEntryStatus.NewVersion, $"file id {r.FileId}, version id {r.VersionId}", bytes),
        BoxSyncOutcome.IdenticalSkipped => new BoxUploadEntry(path, BoxEntryStatus.Identical, $"same SHA-1, nothing sent, file id {r.FileId}", bytes),
        _ => new BoxUploadEntry(path, BoxEntryStatus.SkippedDifferent, $"exists with different content, not overwritten, file id {r.FileId}", bytes),
    };

    /// <summary>Dry run for ONE file: what would UploadOrUpdateAsync do? (read-only: only a file-info call.)</summary>
    private static BoxUploadEntry PlanFile(string path, string? boxFolderId, LocalFile file, byte[] bytes,
        Dictionary<string, string> filesByName, bool overwrite)
    {
        if (boxFolderId == null || !filesByName.TryGetValue(file.Name, out string? existingId))
            return new BoxUploadEntry(path, BoxEntryStatus.Created, "would create", bytes.Length);

        BoxFileInfo info = BoxClientRunner.Run(c => c.GetFileInfoAsync(existingId));
        if (string.Equals(info.Sha1, BoxAdfsLoginClient.Sha1Hex(bytes), StringComparison.OrdinalIgnoreCase))
            return new BoxUploadEntry(path, BoxEntryStatus.Identical, "would skip: same SHA-1", bytes.Length);
        return overwrite
            ? new BoxUploadEntry(path, BoxEntryStatus.NewVersion, "would upload a new version", bytes.Length)
            : new BoxUploadEntry(path, BoxEntryStatus.SkippedDifferent, "would skip: exists with different content", bytes.Length);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void RequireNumeric(string? id, string what)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException($"{what} is required.");
        if (!id.Trim().All(char.IsAsciiDigit)) throw new ArgumentException($"{what} must be a numeric Box id (got '{id}').");
    }

    private static string ResolveName(string? fileName, string defaultName)
    {
        string name = string.IsNullOrWhiteSpace(fileName) ? defaultName : fileName.Trim();
        if (name.Length == 0) throw new ArgumentException("file_name is empty.");
        if (name.IndexOfAny(new[] { '/', '\\' }) >= 0)
            throw new ArgumentException($"file_name must be a plain name without folders (got '{name}').");
        return name;
    }

    /// <summary>Full path + bytes + file name of a local file that is allowed to leave this PC, or throws.</summary>
    private static (string Full, byte[] Bytes, string Name) ReadAllowedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("local_path is required.");

        string full = Path.GetFullPath(path.Trim().Trim('"'));
        if (!File.Exists(full)) throw new FileNotFoundException($"File not found: \"{full}\".", full);

        string? refusal = BoxLocalFileGuard.Refusal(full, new FileInfo(full).Length);
        if (refusal != null) throw new InvalidOperationException($"Refused: {refusal}.");

        return (full, File.ReadAllBytes(full), Path.GetFileName(full));
    }
}
