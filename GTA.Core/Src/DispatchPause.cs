using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace GTA.Core
{
    /// <summary>
    /// State of ONE dispatched Sub Agent. The Sub Agent loop keeps it up to date while it runs. When the user pauses the
    /// parent, the Sub Agent holds at the top of its next round (<see cref="AtFence"/> = true) and this object is a
    /// complete picture of it: it can be saved to disk and a later run (also after an app restart) can continue from it.
    /// </summary>
    public sealed class LiveSubAgent
    {
        public const string StateQueued  = "queued";   // waiting for a free parallel slot, nothing started yet
        public const string StateRunning = "running";  // has a slot, its loop is running (or holding at its fence)
        public const string StateDone    = "done";     // finished, Success / Message hold its report

        /// <summary>The id of the dispatch_sub_agent tool_use block of the parent that started this Sub Agent.</summary>
        public string DispatchId { get; set; } = string.Empty;

        public string Task { get; set; } = string.Empty;
        public bool   AllowUseShell     { get; set; }
        public bool   AllowFileMutation { get; set; }

        private volatile string _state = StateQueued;
        public string State { get => _state; set => _state = value; }

        private volatile bool _atFence;
        /// <summary>True while the loop holds at the top of a round because of a pause. Then Messages is complete and untouched.</summary>
        public bool AtFence { get => _atFence; set => _atFence = value; }

        /// <summary>Id of the twin chat session that shows this Sub Agent (null until it was created).</summary>
        public string? TwinSessionId { get; set; }

        /// <summary>The Sub Agent's own history (a copy of the parent's history + the dispatch note + its own rounds).</summary>
        public JsonArray? Messages { get; set; }

        /// <summary>The tools of this Sub Agent.</summary>
        public JsonArray? Tools { get; set; }

        /// <summary>The round that starts next (or that is holding right now).</summary>
        public int NextRound { get; set; } = 1;

        public int GhostRetries { get; set; }

        // Report (State == done)
        public bool    Success { get; set; }
        public string? Message { get; set; }

        public JsonObject ToJson()
        {
            var o = new JsonObject
            {
                ["dispatchId"]        = DispatchId,
                ["task"]              = Task,
                ["allowUseShell"]     = AllowUseShell,
                ["allowFileMutation"] = AllowFileMutation,
                ["state"]             = State,
                ["twinSessionId"]     = TwinSessionId,
                ["nextRound"]         = NextRound,
                ["ghostRetries"]      = GhostRetries,
                ["success"]           = Success,
                ["message"]           = Message,
            };
            if (Messages != null) o["messages"] = Messages.DeepClone();
            if (Tools    != null) o["tools"]    = Tools.DeepClone();
            return o;
        }

        public static LiveSubAgent FromJson(JsonObject o) => new LiveSubAgent
        {
            DispatchId        = o["dispatchId"]?.GetValue<string>() ?? string.Empty,
            Task              = o["task"]?.GetValue<string>() ?? string.Empty,
            AllowUseShell     = o["allowUseShell"]?.GetValue<bool>() ?? false,
            AllowFileMutation = o["allowFileMutation"]?.GetValue<bool>() ?? false,
            State             = o["state"]?.GetValue<string>() ?? StateQueued,
            TwinSessionId     = o["twinSessionId"]?.GetValue<string>(),
            NextRound         = o["nextRound"]?.GetValue<int>() ?? 1,
            GhostRetries      = o["ghostRetries"]?.GetValue<int>() ?? 0,
            Success           = o["success"]?.GetValue<bool>() ?? false,
            Message           = o["message"]?.GetValue<string>(),
            Messages          = o["messages"] is JsonArray m ? (JsonArray)m.DeepClone() : null,
            Tools             = o["tools"]    is JsonArray t ? (JsonArray)t.DeepClone() : null,
        };
    }

    /// <summary>
    /// What a resumed main loop needs to continue the tool phase of a round that was paused in the middle of a
    /// Sub Agent dispatch: the tool results that were already produced in that round and the state of every Sub Agent.
    /// </summary>
    public sealed class DispatchResumeState
    {
        public JsonArray           ToolResults { get; set; } = new JsonArray();
        public List<LiveSubAgent>  Agents      { get; set; } = new List<LiveSubAgent>();

        public JsonObject ToJson()
        {
            var agents = new JsonArray();
            foreach (LiveSubAgent a in Agents) agents.Add(a.ToJson());
            return new JsonObject
            {
                ["toolResults"] = ToolResults.DeepClone(),
                ["agents"]      = agents,
            };
        }

        public static DispatchResumeState FromJson(JsonObject o)
        {
            var s = new DispatchResumeState();
            if (o["toolResults"] is JsonArray tr) s.ToolResults = (JsonArray)tr.DeepClone();
            if (o["agents"] is JsonArray arr)
                foreach (JsonNode? n in arr)
                    if (n is JsonObject ao) s.Agents.Add(LiveSubAgent.FromJson(ao));
            return s;
        }
    }

    /// <summary>
    /// Lets the host see the parallel Sub Agents of the dispatch that is running right now (one per main loop).
    /// The main loop registers the batch here; the host asks <see cref="IsFrozen"/> to know when EVERYTHING of the
    /// dispatch holds, and then reads <see cref="Snapshot"/>. Thread-safe.
    /// </summary>
    public sealed class DispatchTracker
    {
        private readonly object _lock = new object();
        private List<LiveSubAgent> _agents = new List<LiveSubAgent>();
        private JsonArray? _toolResults;
        private int _maxParallel = 1;
        private volatile bool _active;

        /// <summary>True from the start of a Sub Agent batch until it is over.</summary>
        public bool Active => _active;

        public void Begin(JsonArray toolResults, IEnumerable<LiveSubAgent> agents, int maxParallel)
        {
            lock (_lock)
            {
                _toolResults = toolResults;
                _agents      = new List<LiveSubAgent>(agents);
                _maxParallel = Math.Max(1, maxParallel);
                _active      = true;
            }
        }

        public void End()
        {
            lock (_lock)
            {
                _active      = false;
                _agents      = new List<LiveSubAgent>();
                _toolResults = null;
            }
        }

        /// <summary>
        /// True when the batch is fully stopped: at least one Sub Agent really holds at its fence, every Sub Agent that
        /// has a slot holds (or is done), and the ones still queued have no free slot to start in.
        /// A batch where everything is already done is NOT frozen: the main loop is just about to continue.
        /// </summary>
        public bool IsFrozen
        {
            get
            {
                lock (_lock)
                {
                    if (!_active) return false;

                    int running = 0, held = 0, queued = 0;
                    foreach (LiveSubAgent a in _agents)
                    {
                        switch (a.State)
                        {
                            case LiveSubAgent.StateRunning:
                                running++;
                                if (a.AtFence) held++; else return false;
                                break;
                            case LiveSubAgent.StateQueued:
                                queued++;
                                break;
                        }
                    }

                    if (held == 0) return false;
                    if (queued > 0 && running < _maxParallel) return false; // a slot is free: a queued one is about to start
                    return true;
                }
            }
        }

        /// <summary>Copy of the batch state. Only call it while <see cref="IsFrozen"/> is true.</summary>
        public DispatchResumeState? Snapshot()
        {
            lock (_lock)
            {
                if (!_active || _toolResults == null) return null;
                var s = new DispatchResumeState { ToolResults = (JsonArray)_toolResults.DeepClone() };
                foreach (LiveSubAgent a in _agents) s.Agents.Add(a);
                return s;
            }
        }

        /// <summary>Ids of the twin sessions of the Sub Agents that are running (for the host to save them).</summary>
        public List<string> RunningTwinIds()
        {
            var ids = new List<string>();
            lock (_lock)
            {
                foreach (LiveSubAgent a in _agents)
                    if (a.State == LiveSubAgent.StateRunning && a.TwinSessionId != null) ids.Add(a.TwinSessionId);
            }
            return ids;
        }
    }
}
