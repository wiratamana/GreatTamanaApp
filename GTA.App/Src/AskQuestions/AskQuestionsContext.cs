using System.Threading;

namespace GTA.App;

internal static class AskQuestionsContext
{
    private static readonly AsyncLocal<ChatSession?> _current = new();

    public static ChatSession? CurrentSession
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
