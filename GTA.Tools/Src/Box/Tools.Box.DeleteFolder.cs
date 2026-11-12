using System;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Box;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        // DANGEROUS tool: it deletes a folder AND EVERYTHING INSIDE IT in the company Box. Only the USER can enable it.
        //   [NotFindableByAi] -> not listed by get_available_tools, refused by load_tools, and ToolRegistry.TryExecute
        //                        refuses to run it unless the USER preloaded it (Preload Tools).
        //   [RecordToLog]     -> every call is written to the session log.
        [ToolTag("box_api")]
        [NotFindableByAi]
        [RecordToLog]
        [ToolInfo(
            name: "box_delete_folder",
            description:
                "DELETE a folder in Box AND EVERYTHING INSIDE IT (all files and sub-folders). " +
                "This really changes data in the company Box. Only delete what the user asked you to delete. " +
                "The folder is moved to the Box Trash (the user can restore it there; this tool never empties the Trash). " +
                "SAFETY: you must give 'expected_name', the exact current name of the folder. " +
                "The real name is read from Box first, and the delete is REFUSED if it is different, " +
                "so a wrong folder_id cannot delete the wrong folder. The root folder (id 0) is always refused. " +
                "Look at the folder with box_get_folder first, so you know what is inside. " +
                "Runs over the pure-HTTP ADFS/SAML Box session. " +
                "Not retried after a network error (the delete may already have worked): check before you try again.")]
        [Parameter(index: 0, name: "folder_id",
            type: "string",
            description: "Numeric Box folder ID. Example: '434385877616'. Id '0' (root) is refused.",
            required: true)]
        [Parameter(index: 1, name: "expected_name",
            type: "string",
            description: "The exact current name of the folder in Box. Example: '010-01'. " +
                         "Case matters. The delete is refused when the real name is different.",
            required: true)]
        public static object BoxDeleteFolder(JsonObject input)
        {
            string? folderId     = input["folder_id"]?.GetValue<string>()?.Trim();
            string? expectedName = input["expected_name"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(folderId))     return "Error: 'folder_id' is required.";
            if (string.IsNullOrWhiteSpace(expectedName)) return "Error: 'expected_name' is required (the exact current name of the folder).";

            string label = $"box_delete_folder {folderId} (\"{expectedName.Trim()}\")";
            try
            {
                BoxDeleteResult r = BoxDeleteApi.DeleteFolder(folderId, expectedName);
                return
                    $"✅ {label} — deleted with everything inside (moved to the Box Trash)\n" +
                    $"Folder ID : {r.Id}\n" +
                    $"Name      : {r.Name}\n" +
                    $"Was in    : {r.ParentName ?? "?"}\n" +
                    $"Items     : {(r.ItemCount >= 0 ? r.ItemCount.ToString() : "?")} directly inside (plus whatever was in the sub-folders)\n" +
                    "It can be restored from the Trash in the Box web UI.";
            }
            catch (Exception ex)
            {
                return $"❌ {label} — NOT deleted\nError: {ex.Message}";
            }
        }
    }
}
