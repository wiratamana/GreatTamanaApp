using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Avalonia.Threading;
using GTA.Tools;

namespace GTA.App;

internal static class AskQuestionsTool
{
    /// <summary>How long the popup waits for an answer before giving up.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan BackstopWait = Timeout + TimeSpan.FromMinutes(1);

    [AvaloniaOnlyTool(
        "ask_questions",
        "Ask the human user one or more questions and SYNCHRONOUSLY wait for their answer(s) " +
        "before continuing — a window opens on their screen, you are paused until they respond, " +
        "then your turn continues normally with their answer(s) available to you. Use this when " +
        "you have a genuine ambiguity, a choice only the user can make, or need information you " +
        "cannot discover yourself. Each question needs at least 2 pre-defined answer choices — " +
        "the user can ALSO always type a completely free-form answer instead of picking one of " +
        "them. You may ask multiple questions in a single call; the user pages through them one " +
        "at a time (Back/Next) and only submits once all are answered. The window shows a live " +
        "30-minute countdown; if the user has not answered within that time the window closes " +
        "itself and this call returns a failure indicating the user appears to be absent.",
        Tags = "interactive")]
    [Parameter(index: 0, name: "questions",
        type: "string",
        description: "A JSON array of question OBJECTS — each element MUST be a JSON object " +
                     "shaped like { \"question\": \"...\", \"answers\": [\"Option A\", \"Option B\", ...] }, " +
                     "NOT a plain string. Each object needs a non-empty \"question\" string and an " +
                     "\"answers\" array with AT LEAST 2 non-empty string options (the user can still " +
                     "type something else entirely regardless of what you list here). " +
                     "Example with 2 questions: " +
                     "[{\"question\": \"Which database should we use?\", \"answers\": [\"PostgreSQL\", \"MySQL\", \"SQLite\"]}, " +
                     "{\"question\": \"Should verbose logging be on by default?\", \"answers\": [\"Yes\", \"No\"]}]. " +
                     "WRONG (do NOT do this): [\"Which database should we use?\", \"Should verbose logging be on by default?\"] " +
                     "— bare strings are rejected because each question needs its own \"answers\" list attached.",
        required: true)]
    public static object AskQuestions(JsonObject input)
    {
        if (GTA.Core.NoAskSettings.NoAskEnabled)
            return "User is lazy, decision is up to you";
        TimeZoneInfo jstZone;
        try
        {
            jstZone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            // Fallback for non-Windows ICU time zone databases.
            try { jstZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"); }
            catch (TimeZoneNotFoundException)
            {
                jstZone = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "Japan Standard Time", "JST");
            }
        }

        DateTime jstNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, jstZone).DateTime;
        TimeSpan officeStart = new TimeSpan(8, 0, 0);
        TimeSpan officeEnd = new TimeSpan(18, 30, 0);
        if (jstNow.TimeOfDay < officeStart || jstNow.TimeOfDay > officeEnd)
        {
            return "user is not at office, leave the decison making up to you";
        }
        var window = LlmChatWindow.Instance;
        if (window == null)
            return "Error: ask_questions requires the Avalonia chat UI to be running, but no window instance was found.";

        string raw = input["questions"]?.GetValue<string>() ?? string.Empty;
        List<QuestionSpec>? questions = AskQuestionsParser.Parse(raw, out string? parseError);
        if (questions == null)
            return $"Error: {parseError}";

        ChatSession? session = AskQuestionsContext.CurrentSession;

        using var doneSignal = new ManualResetEventSlim(false);
        List<QuestionAnswer>? finalAnswers = null;
        bool timedOut = false;
        AskQuestionsWindow? openedWindow = null;

        void HandleSubmit(List<QuestionAnswer> answers)
        {
            finalAnswers = answers;
            doneSignal.Set();
        }

        void HandleCancelled()
        {
            finalAnswers = null;
            doneSignal.Set();
        }

        void HandleTimeout()
        {
            finalAnswers = null;
            timedOut = true;
            doneSignal.Set();
        }

        Dispatcher.UIThread.Post(() =>
        {
            var win = new AskQuestionsWindow(questions, HandleSubmit, HandleCancelled, HandleTimeout, Timeout, session?.Title);
            openedWindow = win;

            if (session != null)
            {
                session.Runtime.OpenAskQuestionsWindow = win;
                win.Closed += (_, _) =>
                {
                    if (ReferenceEquals(session.Runtime.OpenAskQuestionsWindow, win))
                        session.Runtime.OpenAskQuestionsWindow = null;
                };
            }

            win.Show(window);
            win.Activate();
        });

        bool signaled = doneSignal.Wait(BackstopWait);
        if (!signaled)
        {
            timedOut = true;
            Dispatcher.UIThread.Post(() =>
            {
                try { openedWindow?.Close(); } catch { /* best-effort */ }
            });
        }

        if (timedOut)
        {
            return "Error: The user did not answer within the 30-minute time limit — the user " +
                   "appears to be absent right now. The question window has been closed. Proceed " +
                   "as best you can without this input, or use ask_questions again later once the " +
                   "user is back.";
        }

        if (finalAnswers == null)
        {
            return "The user closed the question window without answering any of it. " +
                   "If this information is still needed to proceed, consider asking again " +
                   "(perhaps phrased differently), or proceed as best you can without it.";
        }

        return FormatAnswers(finalAnswers);
    }

    private static string FormatAnswers(List<QuestionAnswer> answers)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"🙋 The user answered {answers.Count} question(s):");
        sb.AppendLine();

        for (int i = 0; i < answers.Count; i++)
        {
            QuestionAnswer a = answers[i];
            sb.AppendLine($"Q{i + 1}: {a.Question}");
            sb.AppendLine($"→ Answer: {a.Answer}" + (a.WasFreeText ? "  (user-typed, not one of your listed options)" : ""));
            if (i < answers.Count - 1) sb.AppendLine();
        }

        return sb.ToString();
    }
}
