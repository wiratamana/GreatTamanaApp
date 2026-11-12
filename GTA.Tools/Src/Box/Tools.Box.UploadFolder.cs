using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Box;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        // DANGEROUS tool: it creates folders and puts many local files into the company Box. Only the USER can enable it.
        //   [NotFindableByAi] -> not listed by get_available_tools, refused by load_tools, and ToolRegistry.TryExecute
        //                        refuses to run it unless the USER preloaded it (Preload Tools).
        //   [RecordToLog]     -> every call is written to the session log.
        [ToolTag("box_api")]
        [NotFindableByAi]
        [RecordToLog]
        [ToolInfo(
            name: "box_upload_folder",
            description:
                "UPLOAD a whole local folder (all sub-folders and files) to Box. This really changes data in the company Box. " +
                "Only upload what the user asked you to upload, to the place the user named. " +
                "A new Box folder named like the local folder is created INSIDE 'parent_folder_id' (an existing one with the same name is reused), " +
                "and every sub-folder and file is copied into it. " +
                "If a file already exists in Box, its SHA-1 is compared with the local file: " +
                "same content = nothing is sent (skipped); different content = a NEW VERSION is uploaded " +
                "(the old version stays in Box's version history), unless 'if_exists' is 'skip'. " +
                "So running it again on the same folder is safe: only new or changed files are sent. " +
                "TIP: call it first with dry_run=true to see what would be created / uploaded, then call it again with dry_run=false. " +
                "Limits: max 2000 files and 2 GB per call, max 50 MB per file (bigger files are reported as refused). " +
                "Files from the app's Config / Credentials folders, key files and links/junctions are never uploaded. Everything else in the folder is uploaded. " +
                "The report also lists what is ONLY IN BOX (in the Box folder but not in the local folder, with its id and type). " +
                "dry_run=true is a preview: the report shows what would be created / uploaded, and what is only in Box. " +
                "This tool NEVER deletes anything in Box: to remove an 'only in Box' item use box_delete_file / box_delete_folder with the id and exact name from the report. " +
                "A file that fails does not stop the run; the report lists every file with its result. " +
                "After 5 failures in a row the run stops. It is NOT retried after a network error " +
                "(the upload may already have worked): run it again after checking the folder with box_get_folder. " +
                "Runs over the pure-HTTP ADFS/SAML Box session. For one file use box_upload_file.")]
        [Parameter(index: 0, name: "local_folder",
            type: "string",
            description: "Full Windows path of the local folder to upload. Example: 'C:\\\\tests\\\\010-01'. The folder itself is uploaded (not only its content).",
            required: true)]
        [Parameter(index: 1, name: "parent_folder_id",
            type: "string",
            description: "Numeric Box folder ID of the folder that will CONTAIN the uploaded folder. Example: '434384228758'.",
            required: true)]
        [Parameter(index: 2, name: "if_exists",
            type: "string",
            description: "What to do when a file with the same name is already in the Box folder: " +
                         "'overwrite' (default) = upload a new version if the content differs, skip if identical; " +
                         "'skip' = never change an existing file.",
            required: false)]
        [Parameter(index: 3, name: "dry_run",
            type: "boolean",
            description: "true = change NOTHING in Box; only report what would be created / uploaded / skipped. Default: false.",
            required: false)]
        public static object BoxUploadFolder(JsonObject input)
        {
            string? localFolder = input["local_folder"]?.GetValue<string>()?.Trim();
            string? parentId    = input["parent_folder_id"]?.GetValue<string>()?.Trim();
            bool    dryRun      = input["dry_run"]?.GetValue<bool>() ?? false;

            if (string.IsNullOrWhiteSpace(localFolder)) return "Error: 'local_folder' is required.";
            if (string.IsNullOrWhiteSpace(parentId))    return "Error: 'parent_folder_id' is required.";
            if (!TryParseIfExists(input["if_exists"]?.GetValue<string>(), out BoxIfExists ifExists, out string? ifExistsError))
                return ifExistsError!;

            string label = $"box_upload_folder{(dryRun ? " (DRY RUN)" : "")} \"{localFolder}\" -> Box folder {parentId}";

            BoxFolderUploadReport report;
            try
            {
                report = BoxUploadApi.UploadFolder(parentId, localFolder, ifExists, dryRun, ToolRegistry.ReportProgress);
            }
            catch (Exception ex)
            {
                return $"❌ {label} — nothing was uploaded\nError: {ex.Message}";
            }

            return FormatFolderUploadReport(label, report);
        }

        private static string FormatFolderUploadReport(string label, BoxFolderUploadReport r)
        {
            int failed  = r.Count(BoxEntryStatus.Failed);
            int refused = r.Count(BoxEntryStatus.Refused);
            string plan = r.DryRun ? "would be " : "";

            var sb = new StringBuilder();
            string icon = r.AbortReason != null || failed > 0 ? "⚠️" : "✅";
            sb.AppendLine($"{icon} {label}{(r.DryRun ? "" : " — finished")}");
            if (r.DryRun) sb.AppendLine("DRY RUN: nothing was changed in Box.");
            if (r.AbortReason != null) sb.AppendLine($"⛔ The run {r.AbortReason}. Files after that point were NOT processed.");

            sb.AppendLine($"Mode            : if_exists = {(r.IfExists == BoxIfExists.Overwrite ? "overwrite (new version only when the content differs)" : "skip")}");
            sb.AppendLine($"Box folder      : {(r.RootFolderId != null ? $"https://{BoxHost}/folder/{r.RootFolderId}  (id {r.RootFolderId}, \"{r.RootFolderName}\")" : $"\"{r.RootFolderName}\" does not exist yet and {plan}created in folder {r.BoxParentFolderId}")}");
            sb.AppendLine($"Folders         : {r.Count(BoxEntryStatus.FolderCreated)} {plan}created, {r.Count(BoxEntryStatus.FolderExists)} already existed");
            sb.AppendLine($"New files       : {r.Count(BoxEntryStatus.Created)} {plan}created");
            sb.AppendLine($"New versions    : {r.Count(BoxEntryStatus.NewVersion)} {plan}uploaded (content differed)");
            sb.AppendLine($"Identical       : {r.Count(BoxEntryStatus.Identical)} skipped (same SHA-1)");
            sb.AppendLine($"Different, kept : {r.Count(BoxEntryStatus.SkippedDifferent)} skipped (exists with different content, if_exists = skip)");
            sb.AppendLine($"Refused         : {refused} (secret/key files, too big, links, unreadable)");
            sb.AppendLine($"Failed          : {failed}");
            sb.AppendLine($"Only in Box     : {r.Count(BoxEntryStatus.BoxOnly)} (in Box but not in the local folder = deleted locally? NOT deleted by this tool)");
            sb.AppendLine($"{(r.DryRun ? "Data to send" : "Data sent").PadRight(16)}: {r.BytesSent / 1024.0 / 1024.0:F1} MB in {r.Elapsed.TotalSeconds:F1} s");

            // Details: everything when the list is short; otherwise hide the boring lines (identical files, existing folders).
            var lines = r.Entries.AsEnumerable();
            bool hidden = false;
            if (r.Entries.Count > 60)
            {
                lines = r.Entries.Where(e => e.Status is not (BoxEntryStatus.Identical or BoxEntryStatus.FolderExists));   // BoxOnly lines are always kept
                hidden = true;
            }

            var list = lines.ToList();
            if (list.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("--- Details ---");
                const int MaxLines = 200;
                foreach (BoxUploadEntry e in list.Take(MaxLines))
                    sb.AppendLine($"{StatusMark(e.Status)} {e.Path} — {StatusWord(e.Status, r.DryRun)}{(e.Detail.Length > 0 ? ": " + e.Detail : "")}");
                if (list.Count > MaxLines)
                    sb.AppendLine($"... and {list.Count - MaxLines} more line(s) not shown.");
                if (hidden)
                    sb.AppendLine($"(identical files and existing folders are not listed: {r.Count(BoxEntryStatus.Identical)} identical, {r.Count(BoxEntryStatus.FolderExists)} folders)");
            }

            if (failed > 0)
                sb.AppendLine().AppendLine("💡 Failed files were NOT uploaded. Fix the cause and run the same call again: files that are already in Box are skipped (SHA-1).");

            return sb.ToString().TrimEnd();
        }

        private static string StatusMark(BoxEntryStatus s) => s switch
        {
            BoxEntryStatus.Failed  => "❌",
            BoxEntryStatus.Refused => "🚫",
            BoxEntryStatus.BoxOnly => "👻",
            BoxEntryStatus.Identical or BoxEntryStatus.SkippedDifferent or BoxEntryStatus.FolderExists => "⏭️",
            BoxEntryStatus.FolderCreated => "📁",
            _ => "⬆️",
        };

        private static string StatusWord(BoxEntryStatus s, bool dry) => s switch
        {
            BoxEntryStatus.FolderCreated    => dry ? "folder would be created" : "folder created",
            BoxEntryStatus.FolderExists     => "folder already exists (reused)",
            BoxEntryStatus.Created          => dry ? "would be created" : "created",
            BoxEntryStatus.NewVersion       => dry ? "would get a new version" : "new version",
            BoxEntryStatus.Identical        => "identical, skipped",
            BoxEntryStatus.SkippedDifferent => "different, kept as is",
            BoxEntryStatus.Refused          => "refused",
            BoxEntryStatus.BoxOnly          => "ONLY IN BOX (not deleted)",
            _                               => "FAILED",
        };
    }
}
