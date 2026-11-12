using System;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Box;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        // DANGEROUS tool: it deletes a file in the company Box. Only the USER can enable it.
        //   [NotFindableByAi] -> not listed by get_available_tools, refused by load_tools, and ToolRegistry.TryExecute
        //                        refuses to run it unless the USER preloaded it (Preload Tools).
        //   [RecordToLog]     -> every call is written to the session log.
        [ToolTag("box_api")]
        [NotFindableByAi]
        [RecordToLog]
        [ToolInfo(
            name: "box_delete_file",
            description:
                "DELETE one file in Box. This really changes data in the company Box. " +
                "Only delete what the user asked you to delete. " +
                "The file is moved to the Box Trash (the user can restore it there; this tool never empties the Trash). " +
                "SAFETY: you must give 'expected_name', the exact current name of the file. " +
                "The real name is read from Box first, and the delete is REFUSED if it is different, " +
                "so a wrong file_id cannot delete the wrong file. " +
                "Get the id and name from box_get_folder. Runs over the pure-HTTP ADFS/SAML Box session. " +
                "Not retried after a network error (the delete may already have worked): check the folder before you try again.")]
        [Parameter(index: 0, name: "file_id",
            type: "string",
            description: "Numeric Box file ID (without 'f_'). Example: '2571392447748'.",
            required: true)]
        [Parameter(index: 1, name: "expected_name",
            type: "string",
            description: "The exact current name of the file in Box, including the extension. Example: 'report.xlsx'. " +
                         "Case matters. The delete is refused when the real name is different.",
            required: true)]
        public static object BoxDeleteFile(JsonObject input)
        {
            string? fileId       = input["file_id"]?.GetValue<string>()?.Trim();
            string? expectedName = input["expected_name"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(fileId))       return "Error: 'file_id' is required.";
            if (string.IsNullOrWhiteSpace(expectedName)) return "Error: 'expected_name' is required (the exact current name of the file).";

            string label = $"box_delete_file {fileId} (\"{expectedName.Trim()}\")";
            try
            {
                BoxDeleteResult r = BoxDeleteApi.DeleteFile(fileId, expectedName);
                return
                    $"✅ {label} — deleted (moved to the Box Trash)\n" +
                    $"File ID : {r.Id}\n" +
                    $"Name    : {r.Name}\n" +
                    $"Was in  : {r.ParentName ?? "?"}\n" +
                    "It can be restored from the Trash in the Box web UI.";
            }
            catch (Exception ex)
            {
                return $"❌ {label} — NOT deleted\nError: {ex.Message}";
            }
        }
    }
}
