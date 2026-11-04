using System.Collections.Generic;

namespace GTA.Core
{
    /// <summary>Strongly-typed container for all settings that control one agent run.</summary>
    public sealed record RunSettings(
        string       Prompt,
        bool         Search,
        string       SystemPrompt,
        int          MaxTokens,
        string       Thinking,
        string       Effort,
        bool         Debug,
        List<string> Files,
        string?      AgentName   = null,  // optional — null = use all tools (default behaviour)
        bool         Verbose     = false, // optional — true = print full tool input/output
        string?      EvidenceDir = null   // optional — null = discard-only (old behaviour)
    );
}
