using System;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Encyclopedia;

namespace GTA.Tools
{
    internal static partial class ToolsEncyclopedia
    {
        [ToolTag("essential", "encyclopedia")]
        [ToolInfo(
            name: "encyclopedia_read",
            description:
                "Query the on-disk encyclopedia knowledge base (everything ever written via " +
                "encyclopedia_write) by triggering Google Gemini (model 'gemini-3.5-flash') to " +
                "read EVERY stored entry in full and answer your 'questions' against them. " +
                "Gemini is instructed to be strictly precise and factual — it must NOT " +
                "sugar-coat, soften, or omit anything, even negative or inconvenient facts, and " +
                "must say so explicitly if the knowledge base does not contain enough " +
                "information to answer confidently rather than guessing. Returns an empty-base " +
                "warning if encyclopedia_write has never been called yet.")]
        [Parameter(index: 0, name: "questions",
            type: "string",
            description: "The question(s) to ask against the full encyclopedia knowledge base. " +
                         "Can be one question or several — Gemini will answer each in turn.",
            required: true)]
        public static object EncyclopediaRead(JsonObject input)
        {
            string? questions = input["questions"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(questions))
                return "Error: 'questions' parameter is missing or empty.";

            string corpus = EncyclopediaStore.BuildFullCorpus(out int entryCount);
            if (entryCount == 0)
                return "⚠ The encyclopedia knowledge base is currently empty (no entries have " +
                       "been written yet). Use encyclopedia_write to add entries first, then " +
                       "call encyclopedia_read again.";

            string prompt = BuildEncyclopediaReadPrompt(corpus, questions);

            try
            {
                string? answer = GeminiVideoHelper.AskTextAsync(
                    prompt,
                    GeminiVideoHelper.FlashModel,
                    GeminiVideoHelper.DefaultApiKey).GetAwaiter().GetResult();

                if (string.IsNullOrWhiteSpace(answer))
                    return "⚠ encyclopedia_read — Gemini returned an empty response " +
                           $"({entryCount} entr{(entryCount == 1 ? "y" : "ies")} searched).";

                return "╔══════════════════════════════════════════════════╗\n" +
                       "  encyclopedia_read — Gemini Flash fact lookup\n" +
                       $"  Model            : {GeminiVideoHelper.FlashModel}\n" +
                       $"  Entries searched : {entryCount}\n" +
                       "╚══════════════════════════════════════════════════╝\n\n" +
                       answer;
            }
            catch (Exception ex)
            {
                return $"❌ encyclopedia_read — Gemini query failed ({entryCount} entries): {ex.Message}";
            }
        }

        // ── Prompt builder — engineered for precise, no-sugar-coating answers ──
        private static string BuildEncyclopediaReadPrompt(string corpus, string questions) =>
            "You are a precise, factual encyclopedia lookup assistant. Below is the FULL " +
            "contents of a knowledge base made of individually written entries (each entry was " +
            "authored separately and may cover a different topic, date, or level of detail).\n\n" +
            "Answer the question(s) at the end using ONLY the information contained in these " +
            "entries. Be precise and strictly factual:\n" +
            "  - Do NOT sugar-coat, soften, hedge, or omit anything — even if the facts are " +
            "negative, embarrassing, contradictory, or not what the user might want to hear.\n" +
            "  - Quote or closely paraphrase the relevant entry text to support your answer.\n" +
            "  - If multiple entries conflict, point out the conflict explicitly rather than " +
            "silently picking one.\n" +
            "  - If the entries do NOT contain enough information to answer confidently, say so " +
            "explicitly — never guess or invent facts that are not in the knowledge base.\n\n" +
            "=== KNOWLEDGE BASE START ===\n" +
            corpus +
            "=== KNOWLEDGE BASE END ===\n\n" +
            "QUESTION(S):\n" + questions;
    }
}
