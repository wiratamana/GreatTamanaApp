using System.Collections.Generic;

namespace GTA.Core
{
    public sealed class PendingInjection
    {
        /// <summary>Free-text note typed by the user.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Absolute local file paths attached to this injection.</summary>
        public List<string> Files { get; set; } = new();

        public List<string> ToolNamesToLoad { get; set; } = new();
    }

    public interface IPromptInjectionQueue
    {
        /// <summary>Atomically remove and return every injection queued so far (FIFO order), or an empty list if none are pending.</summary>
        List<PendingInjection> DrainPending();

        /// <summary>True while the host UI wants <see cref="AgenticLoop"/> to hold BEFORE starting its NEXT round</summary>
        bool IsPaused => false;

        void OnPauseWaitStart() { }

        void OnPauseWaitEnd() { }
    }
}
