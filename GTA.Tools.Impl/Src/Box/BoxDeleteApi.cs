namespace GTA.Tools.Impl.Box;

/// <summary>What was deleted. ItemCount is the number of items DIRECTLY inside a folder (-1 for a file or when Box did not say).</summary>
public sealed record BoxDeleteResult(string Kind, string Id, string Name, string? ParentName, int ItemCount);

/// <summary>
/// Public, synchronous entry point for Box deletes. The tool layer (GTA.Tools) only talks to this class.
/// Every method throws on failure (message = what went wrong); the tool layer turns that into text for the AI.
///
/// SAFETY: the caller must give the EXPECTED NAME of the item. The real name is read from Box first and the delete is
/// refused when it differs. A wrong or stale id therefore cannot silently delete something else (a folder takes its
/// whole content with it). Deleted items go to the Box Trash and can be restored there (unless the enterprise has Trash disabled).
/// This class never empties the Trash.
/// </summary>
public static class BoxDeleteApi
{
    public static BoxDeleteResult DeleteFile(string fileId, string expectedName)
    {
        RequireNumeric(fileId, "file_id");
        RequireName(expectedName);
        fileId = fileId.Trim();

        BoxFileInfo info = BoxClientRunner.Run(c => c.GetFileInfoAsync(fileId));
        // Box does not always return the parent's name for a file: fall back to its id.
        string where = info.ParentName ?? (info.ParentId != null ? $"folder id {info.ParentId}" : "?");
        if (!string.Equals(info.Name, expectedName.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refused: file {fileId} is named \"{info.Name}\" (in {where}), not \"{expectedName.Trim()}\". Nothing was deleted.");

        bool ok = BoxClientRunner.Run(c => c.DeleteFileAsync(fileId));
        if (!ok) throw new InvalidOperationException($"Box did not confirm the delete of file {fileId} (\"{info.Name}\"). Check the folder in Box.");
        return new BoxDeleteResult("file", fileId, info.Name, where, -1);
    }

    public static BoxDeleteResult DeleteFolder(string folderId, string expectedName)
    {
        RequireNumeric(folderId, "folder_id");
        RequireName(expectedName);
        folderId = folderId.Trim();
        if (folderId.TrimStart('0').Length == 0) throw new ArgumentException("Refused: the root folder (id 0) can never be deleted.");

        BoxFolderInfo info = BoxClientRunner.Run(c => c.GetFolderInfoAsync(folderId));
        if (!string.Equals(info.Name, expectedName.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refused: folder {folderId} is named \"{info.Name}\" (in folder \"{info.ParentName ?? "?"}\"), not \"{expectedName.Trim()}\". Nothing was deleted.");

        bool ok = BoxClientRunner.Run(c => c.DeleteFolderAsync(folderId));
        if (!ok) throw new InvalidOperationException($"Box did not confirm the delete of folder {folderId} (\"{info.Name}\"). Check the folder in Box.");
        return new BoxDeleteResult("folder", folderId, info.Name, info.ParentName, info.ItemCount);
    }

    private static void RequireNumeric(string? id, string what)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException($"{what} is required.");
        if (!id.Trim().All(char.IsAsciiDigit)) throw new ArgumentException($"{what} must be a numeric Box id (got '{id}').");
    }

    private static void RequireName(string? expectedName)
    {
        if (string.IsNullOrWhiteSpace(expectedName))
            throw new ArgumentException("expected_name is required: the exact current name of the item. Look it up first (box_get_folder shows names and ids).");
    }
}
