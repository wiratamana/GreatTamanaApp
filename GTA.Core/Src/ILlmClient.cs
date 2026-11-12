using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GTA.Core
{
    public interface ILlmClient
    {
        /// <summary>Human-readable name shown in log lines (e.g. "Claude", "GPT").</summary>
        string ProviderName { get; }

        /// <summary>Exact model/deployment name string sent to the API</summary>
        string ModelName { get; }

        /// <summary>Send one agentic round and return the assistant response.</summary>
        Task<JsonObject?> CallAsync(
            JsonArray         messages,
            JsonArray         tools,
            string            systemPrompt,
            int               maxTokens,
            string            thinking,
            string            effort,
            bool              debug,
            IAgentLogger      logger,
            IStreamRenderer   renderer,
            CancellationToken ct = default);

        /// <summary>
        /// True when this client can keep its prompt cache warm while a long tool runs (see <see cref="KeepCacheWarmAsync"/>).
        /// Default: no.
        /// </summary>
        bool SupportsCacheKeepAlive => false;

        /// <summary>Why the last keep-alive ping failed (null when the last ping was fine or none was sent). For the GUI.</summary>
        string? LastKeepAliveError => null;

        /// <summary>
        /// Send ONE tiny request that re-reads the cached prefix so the provider's cache timer restarts.
        /// <paramref name="messages"/> is the live history, which still ENDS WITH the assistant tool_use message
        /// (not answered yet) — the implementation drops it. Returns the usage of the ping (to be added to the
        /// session total), or null when nothing was sent / it failed. Never throws for API errors.
        /// </summary>
        Task<TokenUsage?> KeepCacheWarmAsync(
            JsonArray         messages,
            JsonArray         tools,
            string            systemPrompt,
            string            thinking,
            string            effort,
            IAgentLogger      logger,
            CancellationToken ct = default)
            => Task.FromResult<TokenUsage?>(null);
    }
}
