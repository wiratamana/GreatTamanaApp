using System;
using System.Diagnostics;
using System.Threading;

namespace GTA.Core
{
    /// <summary>
    /// The user's STOP button, as seen from inside a tool.
    ///
    /// Tools are plain synchronous methods (JsonObject -> object). They get no CancellationToken. So the agent
    /// loop puts the token of the running turn here (AsyncLocal = it follows the tool's own call-flow, and each
    /// chat session has its own value). A tool that starts a child process or waits for something long reads
    /// <see cref="Token"/> and stops when it is cancelled. For a child process use
    /// <see cref="WaitForExitOrKill"/>: it kills the WHOLE process tree when STOP is pressed.
    /// A tool that does not look at the token is still safe: the agent loop stops waiting for it
    /// (see AgenticLoop.ExecuteToolCancellableAsync).
    /// </summary>
    public static class ToolCancellation
    {
        private static readonly AsyncLocal<CancellationToken> _token = new();

        /// <summary>Token of the turn that is running this tool. Default (never cancelled) outside an agent loop.</summary>
        public static CancellationToken Token
        {
            get => _token.Value;
            set => _token.Value = value;
        }

        public static bool IsCancellationRequested => _token.Value.IsCancellationRequested;

        /// <summary>
        /// Waits for <paramref name="proc"/> to exit. Returns true when it exited by itself.
        /// Returns false when it was killed (whole tree) because the user pressed STOP
        /// (<paramref name="cancelled"/> = true) or because <paramref name="timeoutMs"/> ran out
        /// (<paramref name="cancelled"/> = false). A negative timeout means "no timeout".
        /// Polls every 100 ms, so STOP reacts in well under a second.
        /// </summary>
        public static bool WaitForExitOrKill(Process proc, long timeoutMs, out bool cancelled)
        {
            cancelled = false;
            CancellationToken ct = Token;
            var clock = Stopwatch.StartNew();

            while (true)
            {
                if (proc.WaitForExit(100)) return true;

                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    KillTree(proc);
                    return false;
                }

                if (timeoutMs >= 0 && clock.ElapsedMilliseconds >= timeoutMs)
                {
                    KillTree(proc);
                    return false;
                }
            }
        }

        /// <summary>Kills the process and every child / grandchild, then waits a little. Never throws.</summary>
        public static void KillTree(Process proc)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone / access denied — best effort */ }
            try { proc.WaitForExit(5000); }             catch { /* best effort */ }
        }
    }
}
