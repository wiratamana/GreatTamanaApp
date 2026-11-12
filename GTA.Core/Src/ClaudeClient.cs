using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GTA.Core
{
    public sealed class ClaudeClient : ILlmClient
    {
        /// <inheritdoc/>
        public string ProviderName => "Claude";

        /// <inheritdoc/>
        /// <remarks>Matches the section name expected in model_pricing.ini.</remarks>
        public string ModelName => AppConfig.MODEL;

        public Task<JsonObject?> CallAsync(
            JsonArray         messages,
            JsonArray         tools,
            string            systemPrompt,
            int               maxTokens,
            string            thinking,
            string            effort,
            bool              debug,
            IAgentLogger      logger,
            IStreamRenderer   renderer,
            CancellationToken ct = default)
            => AzureApiClient.CallAzureAsync(
                   messages, tools, systemPrompt,
                   maxTokens, thinking, effort,
                   debug, logger, renderer, ct);

        /// <inheritdoc/>
        public bool SupportsCacheKeepAlive => !AzureApiClient.KeepAliveDisabled;

        /// <inheritdoc/>
        public string? LastKeepAliveError => AzureApiClient.LastKeepAliveError;

        /// <inheritdoc/>
        public Task<TokenUsage?> KeepCacheWarmAsync(
            JsonArray         messages,
            JsonArray         tools,
            string            systemPrompt,
            string            thinking,
            string            effort,
            IAgentLogger      logger,
            CancellationToken ct = default)
            => AzureApiClient.KeepCacheWarmAsync(messages, tools, systemPrompt, thinking, effort, logger, ct);
    }
}
