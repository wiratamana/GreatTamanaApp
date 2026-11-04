#if !CO_WORKER_USE
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace GTA.App;

public sealed partial class TcpCommandServer
{
    /// <summary>Resolves the output path, renders the window to PNG, and writes the reply.</summary>
    private async Task HandleScreenshotAsync(string argPath, StreamWriter writer)
    {
        string path = ResolveScreenshotPath(argPath);

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                int w = Math.Max(1, (int)_window.Bounds.Width);
                int h = Math.Max(1, (int)_window.Bounds.Height);

                using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
                rtb.Render(_window);

                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                rtb.Save(path);

                LlmChatWindow.Instance?.NotifyScreenshot(path);

            }, DispatcherPriority.Render);

            await writer.WriteLineAsync($"OK {path}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await writer.WriteLineAsync($"ERROR {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>SEND_MESSAGE &lt;text&gt; — posts a plain message to the current session.</summary>
    private async Task HandleSendMessageAsync(string message, StreamWriter writer)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            await writer.WriteLineAsync("ERROR message text is required").ConfigureAwait(false);
            return;
        }

        if (_window is LlmChatWindow cw)
        {
            cw.SendMessageExternal(message);
            await writer.WriteLineAsync("OK message posted").ConfigureAwait(false);
        }
        else
        {
            await writer.WriteLineAsync("ERROR window is not an LlmChatWindow").ConfigureAwait(false);
        }
    }

    /// <summary>SEND_MESSAGE_WITH_FILE &lt;filepath&gt;|&lt;text&gt; — posts a message with one attached image file.</summary>
    private async Task HandleSendMessageWithFileAsync(string arg, StreamWriter writer)
    {
        int sep = arg.IndexOf('|');
        if (sep < 0)
        {
            await writer.WriteLineAsync(
                "ERROR invalid format: use SEND_MESSAGE_WITH_FILE <filepath>|<message>")
                .ConfigureAwait(false);
            return;
        }

        string filePath = arg[..sep].Trim();
        string message  = arg[(sep + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(message))
        {
            await writer.WriteLineAsync(
                "ERROR both filepath and message text are required")
                .ConfigureAwait(false);
            return;
        }

        if (!File.Exists(filePath))
        {
            await writer.WriteLineAsync($"ERROR file not found: {filePath}")
                .ConfigureAwait(false);
            return;
        }

        if (_window is LlmChatWindow cw)
        {
            cw.SendMessageExternalWithFiles(message, new List<string> { filePath });
            await writer.WriteLineAsync("OK message posted with file").ConfigureAwait(false);
        }
        else
        {
            await writer.WriteLineAsync("ERROR window is not an LlmChatWindow")
                .ConfigureAwait(false);
        }
    }

    /// <summary>BUSY — reports whether the LLM is currently processing a request.</summary>
    private async Task HandleBusyStatusAsync(StreamWriter writer)
    {
        bool busy = _window is LlmChatWindow cw && cw.IsLlmRunning;
        await writer.WriteLineAsync(busy ? "BUSY" : "IDLE").ConfigureAwait(false);
    }

    /// <summary>GET_AGENT — returns the name of the currently selected agent.</summary>
    private async Task HandleGetAgentAsync(StreamWriter writer)
    {
        if (_window is LlmChatWindow cw)
        {
            string agentName = cw.GetCurrentAgentName();
            await writer.WriteLineAsync($"OK {agentName}").ConfigureAwait(false);
        }
        else
        {
            await writer.WriteLineAsync("ERROR window is not an LlmChatWindow")
                .ConfigureAwait(false);
        }
    }

    /// <summary>SET_AGENT &lt;agent_name&gt; — changes the active agent in the UI.</summary>
    private async Task HandleSetAgentAsync(string agentName, StreamWriter writer)
    {
        if (string.IsNullOrWhiteSpace(agentName))
        {
            await writer.WriteLineAsync("ERROR agent name is required")
                .ConfigureAwait(false);
            return;
        }

        if (_window is LlmChatWindow cw)
        {
            bool ok = cw.SetCurrentAgent(agentName);
            if (ok)
                await writer.WriteLineAsync($"OK {agentName}").ConfigureAwait(false);
            else
                await writer.WriteLineAsync(
                    $"ERROR agent not found: '{agentName}'. " +
                    "Use list_agents to see all available agent names.")
                    .ConfigureAwait(false);
        }
        else
        {
            await writer.WriteLineAsync("ERROR window is not an LlmChatWindow")
                .ConfigureAwait(false);
        }
    }

    /// <summary>SEND_MESSAGE_AGENT &lt;agent_name&gt;|&lt;text&gt; — posts a message using a specific agent for this message only.</summary>
    private async Task HandleSendMessageWithAgentAsync(string arg, StreamWriter writer)
    {
        int sep = arg.IndexOf('|');
        if (sep < 0)
        {
            await writer.WriteLineAsync(
                "ERROR invalid format: use SEND_MESSAGE_AGENT <agent_name>|<message>")
                .ConfigureAwait(false);
            return;
        }

        string agentName = arg[..sep].Trim();
        string message   = arg[(sep + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(message))
        {
            await writer.WriteLineAsync(
                "ERROR both agent_name and message text are required")
                .ConfigureAwait(false);
            return;
        }

        if (_window is LlmChatWindow cw)
        {
            bool agentFound = cw.SendMessageExternalWithAgent(message, agentName);
            if (agentFound)
                await writer.WriteLineAsync("OK message posted").ConfigureAwait(false);
            else
                await writer.WriteLineAsync(
                    $"ERROR agent not found: '{agentName}'. Message not sent.")
                    .ConfigureAwait(false);
        }
        else
        {
            await writer.WriteLineAsync("ERROR window is not an LlmChatWindow")
                .ConfigureAwait(false);
        }
    }

    /// <summary>SEND_MESSAGE_FILE_AGENT &lt;agent_name&gt;|&lt;filepath&gt;|&lt;text&gt; — posts a message with file + agent override.</summary>
    private async Task HandleSendMessageFileWithAgentAsync(string arg, StreamWriter writer)
    {
        int sep1 = arg.IndexOf('|');
        if (sep1 < 0)
        {
            await writer.WriteLineAsync(
                "ERROR invalid format: use SEND_MESSAGE_FILE_AGENT <agent_name>|<filepath>|<message>")
                .ConfigureAwait(false);
            return;
        }

        string agentName = arg[..sep1].Trim();
        string rest      = arg[(sep1 + 1)..];

        int sep2 = rest.IndexOf('|');
        if (sep2 < 0)
        {
            await writer.WriteLineAsync(
                "ERROR invalid format: use SEND_MESSAGE_FILE_AGENT <agent_name>|<filepath>|<message>")
                .ConfigureAwait(false);
            return;
        }

        string filePath = rest[..sep2].Trim();
        string message  = rest[(sep2 + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(agentName) ||
            string.IsNullOrWhiteSpace(filePath)  ||
            string.IsNullOrWhiteSpace(message))
        {
            await writer.WriteLineAsync(
                "ERROR agent_name, filepath and message text are all required")
                .ConfigureAwait(false);
            return;
        }

        if (!File.Exists(filePath))
        {
            await writer.WriteLineAsync($"ERROR file not found: {filePath}")
                .ConfigureAwait(false);
            return;
        }

        if (_window is LlmChatWindow cw)
        {
            bool agentFound = cw.SendMessageExternalWithFilesAndAgent(
                message, new List<string> { filePath }, agentName);

            if (agentFound)
                await writer.WriteLineAsync("OK message posted with file").ConfigureAwait(false);
            else
                await writer.WriteLineAsync(
                    $"ERROR agent not found: '{agentName}'. Message not sent.")
                    .ConfigureAwait(false);
        }
        else
        {
            await writer.WriteLineAsync("ERROR window is not an LlmChatWindow")
                .ConfigureAwait(false);
        }
    }
}
#endif
