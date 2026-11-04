namespace GTA.Core
{
    /// <summary>Callback interface for rendering SSE stream events to a UI.</summary>
    public interface IStreamRenderer
    {
        /// <summary>Called once when a new content block starts arriving.</summary>
        System.Threading.Tasks.Task OnBlockStart(int blockIndex, string blockType, string toolId, string toolName);

        /// <summary>Called for each text_delta chunk arriving inside a text block.</summary>
        void OnTextChunk(int blockIndex, string chunk);

        /// <summary>Called for each thinking_delta chunk arriving inside a thinking block.</summary>
        void OnThinkingChunk(int blockIndex, string chunk);

        /// <summary>Called for each input_json_delta chunk arriving inside a tool_use block.</summary>
        void OnToolInputChunk(int blockIndex, string toolName, string partialJson);

        /// <summary>Called once when a content block finishes.</summary>
        void OnBlockStop(int blockIndex, string blockType, int charCount);

        /// <summary>Called for log lines that originate during stream parsing (stream errors, unhandled event types).</summary>
        void OnLogLine(string message);
    }
}
