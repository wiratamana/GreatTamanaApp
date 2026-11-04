using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace GTA.Core
{
    /// <summary>Central configuration. Secrets and environment-specific values are read from Config\init.ini.</summary>
    public static class AppConfig
    {
        // ── Azure ──
        public static string AZURE_KEY  => InitSettings.Get("azure_key");
        public static string AZURE_BASE => InitSettings.Get("azure_anthropic_endpoint");
        public const string MODEL       = "claude-sonnet-5-5";

        // ── GPT (Azure OpenAI Responses API) ──
        public static string GPT_BASE => InitSettings.Get("azure_cognitive_endpoint");
        public const string GPT_MODEL          = "gpt-6.1-sol";
        public const string GPT_DEFAULT_EFFORT = "medium";

        // ── Gemini ──
        public static string GEMINI_API_KEY => InitSettings.Get("gemini_api_key");
        public const string GEMINI_MODEL          = "gemini-3.7-flash";
        public const string GEMINI_DEFAULT_EFFORT = "medium";

        // ── Proxy: nothing is configured. See SystemProxy / ProxyBridge (Windows system proxy + current Windows login). ──

        // ── Init file values ──
        /// <summary>GitHub PAT for the git_* online tools (key 'github_pat'); tools may override per call.</summary>
        public static string GITHUB_PAT => InitSettings.Get("github_pat");

        // ── API-enforced output token ceiling ──
        public const int MAX_TOKENS_HARD_CAP = 128_000;

        // ── Defaults ──
        public const bool   DEFAULT_SEARCH     = false; // web tools are Anthropic-direct only; Azure returns 400
        public const string DEFAULT_SYSTEM     = "You are a helpful and knowledgeable assistant.";
        public const int    DEFAULT_MAX_TOKENS = 128_000;
        public const string DEFAULT_THINKING   = "adaptive";
        public const string DEFAULT_EFFORT     = "high";
        public const bool   DEFAULT_DEBUG      = false;
        public const bool   DEFAULT_VERBOSE    = false;

        // ── Retry ──
        public const int MAX_RETRIES    = 99;
        public const int RETRY_DELAY_MS = 500;

        // ── Timeouts ──
        /// <summary>Full SSE stream-body read duration.</summary>
        public const int STREAM_TIMEOUT_SECONDS  = 600;
        /// <summary>Default timeout for non-streaming HTTP requests.</summary>
        public const int DEFAULT_TIMEOUT_SECONDS = 30;
        /// <summary>Fail-fast timeout for TCP/TLS/proxy-auth connect.</summary>
        public const int CONNECT_TIMEOUT_SECONDS = 20;
        /// <summary>Bounds connect + proxy-auth + TLS + upload + wait for response headers.</summary>
        public const int HEADERS_TIMEOUT_SECONDS = 90;
        /// <summary>Max lifetime of a pooled connection (kept under the 5-minute prompt-cache TTL).</summary>
        public const int POOLED_CONNECTION_LIFETIME_MINUTES = 4;
        /// <summary>Immediate connect-level retries inside one outer attempt.</summary>
        public const int MAX_CONNECT_RETRIES = 555;
        public const int CONNECT_RETRY_DELAY_MS = 300;

        /// <summary>Last-resort cap on tool-call rounds; intentionally effectively unlimited.</summary>
        public const int MAX_AGENTIC_ROUNDS = 999_999;

        // ── Valid value sets ──
        public static readonly HashSet<string> VALID_THINKING =
            new(StringComparer.OrdinalIgnoreCase) { "adaptive", "disabled" };

        public static readonly HashSet<string> VALID_EFFORT =
            new(StringComparer.OrdinalIgnoreCase) { "none", "minimal", "low", "medium", "high", "xhigh", "max" };

        public static readonly HashSet<string> IMAGE_EXTS =
            new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

        public static readonly JsonArray WEB_TOOLS = JsonNode.Parse("""
        [
          {
            "type": "web_search_20260209",
            "name": "web_search"
          },
          {
            "type": "web_fetch_20260209",
            "name": "web_fetch"
          }
        ]
        """)!.AsArray();
    }
}
