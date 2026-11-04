using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace GTA.Core
{
    public sealed class HttpDiagnosticsEventListener : EventListener
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private static bool IsInteresting(string sourceName) =>
            sourceName is "System.Net.Http"
                       or "System.Net.Security"
                       or "System.Net.NameResolution";

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (IsInteresting(eventSource.Name))
            {
                EnableEvents(eventSource, EventLevel.Informational);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (!SessionFileLogger.IsVerbose) return;

            try
            {
                string payload = "";
                if (eventData.Payload != null && eventData.Payload.Count > 0)
                {
                    var parts = new string[eventData.Payload.Count];
                    for (int i = 0; i < eventData.Payload.Count; i++)
                    {
                        string name = (eventData.PayloadNames != null && i < eventData.PayloadNames.Count)
                            ? eventData.PayloadNames[i]
                            : $"arg{i}";
                        parts[i] = $"{name}={eventData.Payload[i]}";
                    }
                    payload = string.Join(", ", parts);
                }

                SessionFileLogger.LogVerbose("NetDiag",
                    $"t={_clock.Elapsed.TotalMilliseconds:F0}ms {eventData.EventSource.Name}/{eventData.EventName}({payload})");
            }
            catch
            {
                // Diagnostics must never throw or destabilize the real call.
            }
        }
    }
}
