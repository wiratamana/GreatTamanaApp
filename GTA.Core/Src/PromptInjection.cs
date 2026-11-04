using System;
using System.Collections.Generic;

namespace GTA.Core
{
    public sealed class PendingInjection
    {
        /// <summary>Stable id. The queue UI uses it to edit / remove one specific message.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>When the user queued this message (local time).</summary>
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

        /// <summary>Free-text note typed by the user.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Absolute local file paths attached to this injection.</summary>
        public List<string> Files { get; set; } = new();

        public List<string> ToolNamesToLoad { get; set; } = new();

        /// <summary>Deep copy. The queue hands out copies, so the UI never touches the live queue item.</summary>
        public PendingInjection Clone() => new PendingInjection
        {
            Id              = Id,
            CreatedAt       = CreatedAt,
            Text            = Text,
            Files           = new List<string>(Files),
            ToolNamesToLoad = new List<string>(ToolNamesToLoad),
        };
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
