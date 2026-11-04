using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace GTA.App;

/// <summary>One question the AI wants to ask the human user.</summary>
public sealed class QuestionSpec
{
    /// <summary>The question text shown to the user.</summary>
    public string Question { get; init; } = string.Empty;

    /// <summary>Two or more pre-defined answer choices (rendered as a single-select checkbox group in AskQuestionsWindow).</summary>
    public List<string> Answers { get; init; } = new();
}

/// <summary>One question + the user's actual answer, produced by AskQuestionsWindow on Submit.</summary>
public sealed class QuestionAnswer
{
    public string Question    { get; init; } = string.Empty;
    public string Answer      { get; init; } = string.Empty;

    /// <summary>True if the user typed a custom answer instead of picking one of the provided Answers options.</summary>
    public bool WasFreeText { get; init; } = false;
}

internal static class AskQuestionsParser
{
    public static List<QuestionSpec>? Parse(string raw, out string? error)
    {
        error = null;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (Exception ex)
        {
            error = $"'questions' is not valid JSON: {ex.Message}";
            return null;
        }

        if (node is not JsonArray array || array.Count == 0)
        {
            error = "'questions' must be a non-empty JSON array of " +
                    "{ \"question\": \"...\", \"answers\": [\"A\", \"B\", ...] } objects.";
            return null;
        }

        var result = new List<QuestionSpec>();

        for (int i = 0; i < array.Count; i++)
        {
            JsonNode? item = array[i];
            if (item is not JsonObject obj)
            {
                error = $"questions[{i}] must be a JSON object, not {item?.GetType().Name ?? "null"}.";
                return null;
            }

            string? question = obj["question"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(question))
            {
                error = $"questions[{i}].question must be a non-empty string.";
                return null;
            }

            var answers = new List<string>();
            if (obj["answers"] is JsonArray answersArray)
            {
                foreach (JsonNode? a in answersArray)
                {
                    string? text = a?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text))
                        answers.Add(text.Trim());
                }
            }

            if (answers.Count < 2)
            {
                error = $"questions[{i}].answers must contain at least 2 non-empty answer option(s) " +
                        "(the user can always additionally type a free-form answer instead).";
                return null;
            }

            result.Add(new QuestionSpec { Question = question.Trim(), Answers = answers });
        }

        return result;
    }
}
