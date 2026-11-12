using System;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Box;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        // DANGEROUS tool: it puts a local file into the company Box. Only the USER can enable it.
        //   [NotFindableByAi] -> not listed by get_available_tools, refused by load_tools, and ToolRegistry.TryExecute
        //                        refuses to run it unless the USER preloaded it (Preload Tools).
        //   [RecordToLog]     -> every call is written to the session log.
        [ToolTag("box_api")]
        [NotFindableByAi]
        [RecordToLog]
        [ToolInfo(
            name: "box_upload_file",
            description:
                "UPLOAD one local file to Box. This really changes data in the company Box. " +
                "Only upload what the user asked you to upload, to the place the user named. " +
                "Two modes (give exactly one of 'folder_id' or 'file_id'):\n" +
                "  1) 'folder_id': put the file into that Box folder. If the name is free, a new file is created. " +
                "If the name already exists, the SHA-1 of the local file is compared with the file in Box: " +
                "same content = nothing is sent (skipped); different content = a NEW VERSION is uploaded " +
                "(the old version stays in Box's version history), unless 'if_exists' is 'skip'.\n" +
                "  2) 'file_id': upload a NEW VERSION of that exact Box file (the file keeps its Box name). " +
                "Same content = skipped.\n" +
                "Runs over the pure-HTTP ADFS/SAML Box session (an upload-scoped token is fetched automatically). " +
                "Max file size 50 MB. Files from the app's Config / Credentials folders and key files are refused. " +
                "This tool does not retry after a network error, because the upload may already have worked: " +
                "check the folder with box_get_folder before you try again. " +
                "For a whole folder use box_upload_folder.")]
        [Parameter(index: 0, name: "local_path",
            type: "string",
            description: "Full Windows path of the local file to upload. Example: 'C:\\\\temp\\\\report.xlsx'.",
            required: true)]
        [Parameter(index: 1, name: "folder_id",
            type: "string",
            description: "Numeric Box folder ID. Puts the file into this folder (new file, or new version if the name exists). Do not combine with 'file_id'.",
            required: false)]
        [Parameter(index: 2, name: "file_id",
            type: "string",
            description: "Numeric Box file ID (without 'f_'). Uploads a NEW VERSION of this file. Do not combine with 'folder_id'.",
            required: false)]
        [Parameter(index: 3, name: "file_name",
            type: "string",
            description: "Optional name the file gets in Box (plain name, no folders). Default: the name of the local file " +
                         "(with 'file_id': the file keeps its current Box name; giving a name here RENAMES it).",
            required: false)]
        [Parameter(index: 4, name: "if_exists",
            type: "string",
            description: "Only with 'folder_id'. What to do when a file with the same name is already in the folder: " +
                         "'overwrite' (default) = upload a new version if the content differs, skip if identical; " +
                         "'skip' = never change an existing file.",
            required: false)]
        public static object BoxUploadFile(JsonObject input)
        {
            string? localPath = input["local_path"]?.GetValue<string>()?.Trim();
            string? folderId  = input["folder_id"]?.GetValue<string>()?.Trim();
            string? fileId    = input["file_id"]?.GetValue<string>()?.Trim();
            string? fileName  = input["file_name"]?.GetValue<string>()?.Trim();

            if (string.IsNullOrWhiteSpace(localPath))
                return "Error: 'local_path' is required.";

            bool hasFolder = !string.IsNullOrWhiteSpace(folderId);
            bool hasFile   = !string.IsNullOrWhiteSpace(fileId);
            if (hasFolder == hasFile)
                return "Error: give exactly one of 'folder_id' (put the file into a folder) or 'file_id' (new version of an existing file).";

            if (!TryParseIfExists(input["if_exists"]?.GetValue<string>(), out BoxIfExists ifExists, out string? ifExistsError))
                return ifExistsError!;
            if (hasFile && ifExists == BoxIfExists.Skip)
                return "Error: 'if_exists' only applies together with 'folder_id'. With 'file_id' the file is always updated (unless the content is identical).";

            string label = hasFile
                ? $"box_upload_file (new version of file {fileId})"
                : $"box_upload_file (into folder {folderId})";

            BoxSyncResult r;
            try
            {
                r = hasFile
                    ? BoxUploadApi.UploadNewVersion(fileId!, localPath, fileName)
                    : BoxUploadApi.UploadNewFile(folderId!, localPath, fileName, ifExists);
            }
            catch (Exception ex)
            {
                return $"❌ {label} — NOT uploaded\nError: {ex.Message}";
            }

            string headline = r.Outcome switch
            {
                BoxSyncOutcome.Created          => "✅ uploaded as a NEW file",
                BoxSyncOutcome.NewVersion       => "✅ uploaded as a NEW VERSION of the existing file (old version kept in Box)",
                BoxSyncOutcome.IdenticalSkipped => "⏭️ skipped: the file in Box already has the same content (same SHA-1). Nothing was changed",
                _                               => "⏭️ skipped: a file with this name exists with DIFFERENT content, and if_exists is 'skip'. Nothing was changed",
            };

            return
                $"{label}\n{headline}\n" +
                $"File ID    : {r.FileId}\n" +
                $"Name       : {r.Name}\n" +
                $"Version ID : {r.VersionId ?? "(unknown)"}\n" +
                $"Size       : {r.Size:N0} bytes\n" +
                $"SHA-1      : {r.Sha1 ?? "(unknown)"}\n" +
                $"URL        : https://{BoxHost}/file/{r.FileId}";
        }

        /// <summary>'overwrite' (default) or 'skip'. Anything else is an error text for the AI.</summary>
        internal static bool TryParseIfExists(string? raw, out BoxIfExists value, out string? error)
        {
            value = BoxIfExists.Overwrite;
            error = null;
            string? v = raw?.Trim();
            if (string.IsNullOrEmpty(v) || v.Equals("overwrite", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.Equals("skip", StringComparison.OrdinalIgnoreCase)) { value = BoxIfExists.Skip; return true; }
            error = $"Error: 'if_exists' must be 'overwrite' or 'skip' (got '{v}').";
            return false;
        }
    }
}
