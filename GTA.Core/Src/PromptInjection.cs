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

        /// <summary>
        /// True while an OWNER (for example the 💉 Inject window) holds the NEXT round. Respected by the main loop AND by
        /// every dispatched Sub Agent loop.
        /// </summary>
        bool IsPaused => false;

        /// <summary>
        /// True while the USER asked for a pause (⏸ button, global or per session). Respected by the main loop at the very
        /// end of a round (= before the next round starts) AND by every dispatched Sub Agent of that session (each one holds
        /// before its next round). A Sub Agent can not be paused or resumed on its own: only through its parent.
        /// </summary>
        bool IsPauseRequested => false;

        void OnPauseWaitStart() { }

        void OnPauseWaitEnd() { }

        /// <summary>
        /// Called by the MAIN loop at the top of every round, before the pause check. <paramref name="nextRound"/> is the
        /// number of the round that is about to start (a resume continues from it).
        /// </summary>
        void OnRoundFence(int nextRound) { }

        /// <summary>
        /// Called by the MAIN loop (never by a Sub Agent loop) with true when it starts to hold at the round fence and
        /// with false when it leaves it. While it is true the history is complete and nothing changes it, so it can be saved.
        /// </summary>
        void OnMainFenceHold(bool holding) { }

        /// <summary>The Sub Agent batch of this session's main loop (see <see cref="DispatchTracker"/>), or null if the host has none.</summary>
        DispatchTracker? DispatchTracker => null;
    }
}
