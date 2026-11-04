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
    }
}
