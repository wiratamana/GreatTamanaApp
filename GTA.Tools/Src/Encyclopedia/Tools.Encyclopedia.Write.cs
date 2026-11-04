using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Encyclopedia;

namespace GTA.Tools
{
    internal static partial class ToolsEncyclopedia
    {
        [ToolTag("essential", "encyclopedia")]
        [FileMutating]
        [ToolInfo(
            name: "encyclopedia_write",
            description:
                "Write a new entry directly to the on-disk encyclopedia knowledge base. " +
                "Creates one plain-text file named '{date_write}_{title}.md' (today's date " +
                "plus a sanitized version of the title) inside the shared encyclopedia_kb " +
                "folder — no LLM call is made, this is a pure local file write. Use this to " +
                "permanently record facts, decisions, glossary terms, investigation findings, " +
                "or any knowledge you want to be able to recall precisely later via " +
                "encyclopedia_read. Every entry ever written is later fed IN FULL to Gemini " +
                "(gemini-3.5-flash) when encyclopedia_read is called, so keep each entry " +
                "focused and factual.")]
        [Parameter(index: 0, name: "title",
            type: "string",
            description: "Short encyclopedia title for this entry (used to build the file name, " +
                         "e.g. 'Device_Provisioning_Flow'). Keep it short and descriptive.",
            required: true)]
        [Parameter(index: 1, name: "desc",
            type: "string",
            description: "The full description / content / facts to store for this entry. Be as " +
                         "precise and complete as possible — this exact text is what Gemini will " +
                         "later read back verbatim when answering questions via encyclopedia_read.",
            required: true)]
        public static object EncyclopediaWrite(JsonObject input)
        {
            string? title = input["title"]?.GetValue<string>();
            string? desc  = input["desc"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(title))
                return "Error: 'title' parameter is missing or empty.";
            if (string.IsNullOrWhiteSpace(desc))
                return "Error: 'desc' parameter is missing or empty.";

            DateTime now = DateTime.Now;
            string   path;

            try
            {
                path = EncyclopediaStore.BuildEntryPath(title, now);

                string content =
                    $"# {title.Trim()}\n" +
                    $"Date: {now:yyyy-MM-dd HH:mm:ss}\n\n" +
                    $"{desc.Trim()}\n";

                File.WriteAllText(path, content, Encoding.UTF8);
            }
            catch (UnauthorizedAccessException)
            {
                return $"Error: Access denied — cannot write encyclopedia entry for title '{title}'.";
            }
            catch (Exception ex)
            {
                return $"Error: Failed to write encyclopedia entry: {ex.Message}";
            }

            return "✔ Encyclopedia entry written.\n" +
                   $"  Title : {title.Trim()}\n" +
                   $"  Date  : {now:yyyy-MM-dd}\n" +
                   $"  File  : {path}\n\n" +
                   "Call encyclopedia_read(questions: \"...\") any time to have Gemini " +
                   "(gemini-3.5-flash) answer questions against every entry stored so far.";
        }
    }
}
