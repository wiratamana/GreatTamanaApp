using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GTA.Core;

namespace GTA.Builder
{
    /// <summary>
    /// Keeps the prompt cache of the conversation alive while the tools of ONE round run.
    ///
    /// Problem: the cache lives 5 minutes. A tool that runs longer (cmake, tests, a long CLI) lets it expire, and the
    /// next request must write the whole history again (expensive).
    /// Fix: a background loop sends a tiny "ping" (see <see cref="ILlmClient.KeepCacheWarmAsync"/>) when the last
    /// cache touch is older than CACHE_KEEPALIVE_INTERVAL_SECONDS. A ping is a cache READ (cheap) and restarts the timer.
    /// A tool that finishes in a short time never causes a ping, so normal rounds cost nothing extra.
    ///
    /// Use: create after the model answered with tool_use (the history then ends with the assistant message),
    /// call <see cref="Stop"/> after the tools are done and add the returned usage to the session total.
    /// </summary>
    internal sealed class CacheKeepAlive : IDisposable
    {
        private readonly ILlmClient   _client;
        private readonly JsonArray    _messages;
        private readonly JsonArray    _tools;
        private readonly string       _systemPrompt;
        private readonly string       _thinking;
        private readonly string       _effort;
        private readonly IAgentLogger _logger;

        private readonly CancellationTokenSource _cts = new();
        private readonly object _lock = new();

        private long       _lastTouchTimestamp;   // Stopwatch timestamp of the last cache touch (real call start or ping)
        private int        _suspended;            // > 0 => do not ping (e.g. a Sub Agent keeps the cache warm itself)
        private TokenUsage _usage;
        private int        _pings;
        private bool       _stopped;

        private CacheKeepAlive(
            ILlmClient client, JsonArray messages, JsonArray tools, string systemPrompt,
            string thinking, string effort, IAgentLogger logger, long lastTouchTimestamp)
        {
            _client             = client;
            _messages           = messages;
            _tools              = tools;
            _systemPrompt       = systemPrompt;
            _thinking           = thinking;
            _effort             = effort;
            _logger             = logger;
            _lastTouchTimestamp = lastTouchTimestamp;
        }

        /// <param name="lastTouchTimestamp">
        /// <see cref="Stopwatch.GetTimestamp"/> taken right BEFORE the LLM call that produced the tool_use
        /// (that call is what last refreshed the cache).
        /// </param>
        public static CacheKeepAlive Start(
            ILlmClient client, JsonArray messages, JsonArray tools, string systemPrompt,
            string thinking, string effort, IAgentLogger logger, long lastTouchTimestamp)
        {
            var ka = new CacheKeepAlive(client, messages, tools, systemPrompt, thinking, effort, logger, lastTouchTimestamp);

            // 1h TTL already survives long tools, so no ping is needed then.
            bool enabled = AppConfig.CACHE_KEEPALIVE && !AppConfig.CACHE_TTL_1H && client.SupportsCacheKeepAlive;
            if (enabled)
                _ = Task.Run(() => ka.LoopAsync(ka._cts.Token));

            return ka;
        }

        /// <summary>Pause pinging until the returned object is disposed (used while a Sub Agent runs and keeps the cache warm itself).</summary>
        public IDisposable Suspend()
        {
            Interlocked.Increment(ref _suspended);
            return new Resume(this);
        }

        private sealed class Resume : IDisposable
        {
            private CacheKeepAlive? _owner;
            public Resume(CacheKeepAlive owner) => _owner = owner;
            public void Dispose()
            {
                CacheKeepAlive? o = Interlocked.Exchange(ref _owner, null);
                if (o == null) return;
                Volatile.Write(ref o._lastTouchTimestamp, Stopwatch.GetTimestamp()); // the Sub Agent just used the cache
                Interlocked.Decrement(ref o._suspended);
            }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            try
            {
                int intervalSeconds = AppConfig.CACHE_KEEPALIVE_INTERVAL_SECONDS;

                int attempts = 0;

                while (!ct.IsCancellationRequested)
                {
                    // Keep-alive was switched off meanwhile (3 rejected pings in a row): nothing more to do.
                    if (!_client.SupportsCacheKeepAlive) break;

                    double sinceTouch = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastTouchTimestamp)).TotalSeconds;
                    double wait       = intervalSeconds - sinceTouch;

                    // Short sleeps: a Suspend()/resume or a Stop() is noticed within seconds.
                    if (wait > 0 || Volatile.Read(ref _suspended) > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 1, 5)), ct).ConfigureAwait(false);
                        continue;
                    }

                    attempts++;
                    TokenUsage? u = await _client.KeepCacheWarmAsync(
                        _messages, _tools, _systemPrompt, _thinking, _effort, _logger, ct).ConfigureAwait(false);

                    // Restart the interval even when the ping failed, so a broken ping is not repeated every 5 seconds.
                    Volatile.Write(ref _lastTouchTimestamp, Stopwatch.GetTimestamp());

                    // The tool finished while the ping was in flight: nothing to report.
                    if (ct.IsCancellationRequested) break;

                    if (u.HasValue)
                    {
                        lock (_lock)
                        {
                            if (!_stopped) { _usage += u.Value; _pings++; }
                        }
                    }

                    // Tell the GUI (a visible "cache kept warm" note on the round). Never let a UI problem break the loop.
                    try
                    {
                        _logger.LogCacheKeepAlive(
                            attempts, u.HasValue, u ?? default,
                            u.HasValue ? null : (_client.LastKeepAliveError ?? "no ping was sent (see the log)"));
                    }
                    catch { /* ignore */ }
                }
            }
            catch (OperationCanceledException) { /* tool finished */ }
            catch (Exception ex)
            {
                try { _logger.LogWarning($"[cache keep-alive] loop stopped: {ex.Message}"); } catch { /* never break the run */ }
            }
        }

        /// <summary>Stop pinging. Returns the usage of all pings so far (add it to the session total). Safe to call twice.</summary>
        public TokenUsage Stop()
        {
            lock (_lock)
            {
                if (!_stopped)
                {
                    _stopped = true;
                    try { _cts.Cancel(); } catch { /* ignore */ }
                    if (_pings > 0)
                        _logger.LogInfo($"[cache keep-alive] {_pings} ping(s) kept the cache warm during this tool phase.");
                }
                return _usage;
            }
        }

        public void Dispose() => Stop();
    }
}
