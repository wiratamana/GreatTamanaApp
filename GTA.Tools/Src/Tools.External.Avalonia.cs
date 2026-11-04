#if !CO_WORKER_USE
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static class ToolsExternalAvalonia
    {
        // ── Shared TCP constants ──────────────────────────────────────────────────

        private const string AvaloniaHost     = "127.0.0.1";
        private const int    AvaloniaPort     = 9876;
        private const int    ConnectTimeoutMs = 3_000;  // time to wait for connection
        private const int    ReadTimeoutMs    = 5_000;  // time to wait for reply

        // ── launch_avalonia ───────────────────────────────────────────────────────

        [ToolTag("avalonia")]
        [ToolInfo(
            name: "launch_avalonia",
            description:
                "Launch the GTA Avalonia GUI by executing the launch_avalonia.ps1 " +
                "PowerShell script at the specified path. " +
                "The script starts the app in a detached process and polls TCP port 9876 " +
                "(PING → PONG) until the app is ready, then exits. " +
                "If the Avalonia app is already running the tool returns immediately without " +
                "relaunching it. " +
                "On failure the tool returns a descriptive error with suggested next steps.")]
        [Parameter(
            index: 0,
            name: "script_path",
            type: "string",
            description:
                "Full absolute Windows path to launch_avalonia.ps1. " +
                "Example: C:\\path\\to\\GTA.App\\launch_avalonia.ps1",
            required: true)]
        public static object LaunchAvalonia(JsonObject input)
        {
            string? rawPath = input["script_path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rawPath))
                return
                    "Error: 'script_path' parameter is missing or empty. " +
                    "Provide the full path to launch_avalonia.ps1, e.g. " +
                    "C:\\...\\GTA.App\\launch_avalonia.ps1";

            string scriptPath = rawPath.Trim().Trim('"');

            if (!File.Exists(scriptPath))
                return
                    $"Error: PowerShell script not found at '{scriptPath}'. " +
                    "Verify the path and ensure the GTA.App project is built. " +
                    "Expected location: <solution>\\GTA.App\\launch_avalonia.ps1";

            string? pingReply = TrySendTcpCommand("PING", out _);
            if (pingReply == "PONG")
                return
                    $"Avalonia app is already running on {AvaloniaHost}:{AvaloniaPort}. " +
                    "No action taken. Use check_avalonia_status to confirm, or " +
                    "close_avalonia to shut it down first if a fresh launch is needed.";

            try
            {
                var psi = new ProcessStartInfo("powershell.exe")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding  = Encoding.UTF8,
                };
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(scriptPath);

                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("Process.Start returned null.");

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();

                string stdout   = stdoutTask.Result.Trim();
                string stderr   = stderrTask.Result.Trim();
                int    exitCode = proc.ExitCode;

                if (exitCode == 0)
                {
                    return string.IsNullOrWhiteSpace(stdout)
                        ? $"Avalonia app launched successfully (port {AvaloniaPort})."
                        : $"Avalonia app launched successfully.\n{stdout}";
                }

                var sb = new StringBuilder();
                sb.AppendLine($"Error: launch_avalonia.ps1 exited with code {exitCode}.");
                if (!string.IsNullOrWhiteSpace(stdout)) sb.AppendLine($"Script output : {stdout}");
                if (!string.IsNullOrWhiteSpace(stderr)) sb.AppendLine($"Script stderr : {stderr}");
                sb.AppendLine();
                sb.AppendLine("Suggested fixes:");
                sb.AppendLine("  1. Run dotnet_build on the GTA.App project first.");
                sb.AppendLine($"  2. Confirm the exe exists at the path referenced in '{scriptPath}'.");
                sb.AppendLine($"  3. Ensure TCP port {AvaloniaPort} is not blocked by a firewall or another process.");
                sb.AppendLine("  4. Check Windows Event Log for crash details if the process started but exited.");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return
                    $"Error: Failed to start PowerShell process: {ex.Message}\n" +
                    "Ensure 'powershell.exe' is available in PATH (standard on all Windows versions). " +
                    "If execution policy is blocked, ask IT to allow local scripts, or run once: Set-ExecutionPolicy -Scope CurrentUser RemoteSigned.";
            }
        }

        // ── check_avalonia_status ─────────────────────────────────────────────────

        [ToolTag("avalonia")]
        [ToolInfo(
            name: "check_avalonia_status",
            description:
                "Check whether the Avalonia UI app is currently running by sending a PING " +
                "to TCP port 9876 on localhost. " +
                "Returns RUNNING if the app responds with PONG, or NOT_RUNNING otherwise. " +
                "Use this before taking a screenshot or issuing any TCP command to confirm " +
                "the app is alive.")]
        public static object CheckAvaloniaStatus(JsonObject _input)
        {
            string? reply = TrySendTcpCommand("PING", out string? error);

            if (reply == "PONG")
                return
                    $"RUNNING — Avalonia app is active and responding on {AvaloniaHost}:{AvaloniaPort}.";

            string noConn = error
                ?? $"Unexpected reply from {AvaloniaHost}:{AvaloniaPort}: '{reply ?? "(null)"}'.";

            return
                $"NOT_RUNNING — {noConn}\n" +
                "To start the app call launch_avalonia with the path to launch_avalonia.ps1.";
        }

        // ── take_screenshot ───────────────────────────────────────────────────────

        [ToolTag("avalonia")]
        [ToolInfo(
            name: "take_screenshot",
            description:
                "Capture a screenshot of the running Avalonia UI app by sending a SCREENSHOT " +
                "command to TCP port 9876 on localhost. " +
                "Returns the absolute path of the saved PNG file on success, or an error " +
                "message if the app is not running or the screenshot could not be taken. " +
                "Optionally accepts an output path; if omitted, a timestamped PNG file is " +
                "saved to the app's default screenshot directory. " +
                "Use check_avalonia_status first to confirm the app is running before calling " +
                "this tool.")]
        [Parameter(
            index: 0,
            name: "output_path",
            type: "string",
            description:
                "Optional. The output file path for the screenshot. " +
                "Can be an absolute path (e.g. C:\\screenshots\\test.png) or a relative " +
                "filename (e.g. test.png) resolved against the app's default screenshot " +
                "directory. If omitted, a timestamped filename is auto-generated.",
            required: false)]
        public static object TakeScreenshot(JsonObject input)
        {
            string? pingReply = TrySendTcpCommand("PING", out string? pingError);

            if (pingReply != "PONG")
            {
                string reason = pingError
                    ?? $"No PONG received from {AvaloniaHost}:{AvaloniaPort}.";

                return
                    $"Error: Cannot take screenshot — Avalonia app is not running. {reason}\n" +
                    "Use launch_avalonia to start the app first.";
            }

            string? outputPath = input["output_path"]?.GetValue<string>()?.Trim().Trim('"');
            string command = string.IsNullOrWhiteSpace(outputPath)
                ? "SCREENSHOT"
                : $"SCREENSHOT {outputPath}";

            string? reply = TrySendTcpCommand(command, out string? tcpError);

            if (tcpError != null)
                return
                    $"Error: PING succeeded but SCREENSHOT command failed. {tcpError}\n" +
                    "The app may be in a bad state. Try check_avalonia_status and, if still " +
                    "running, attempt take_screenshot again.";

            if (reply != null && reply.StartsWith("OK ", StringComparison.OrdinalIgnoreCase))
            {
                string savedPath = reply[3..].Trim();
                return $"Screenshot saved successfully to: {savedPath}";
            }

            if (reply != null && reply.StartsWith("ERROR ", StringComparison.OrdinalIgnoreCase))
                return $"Error: Avalonia app reported: {reply[6..].Trim()}";

            return
                $"Warning: SCREENSHOT was sent but an unexpected reply was received: " +
                $"'{reply ?? "(null)"}'. " +
                "The screenshot may or may not have been saved. " +
                "Check the app's default screenshot directory to verify.";
        }

        // ── close_avalonia ────────────────────────────────────────────────────────

        [ToolTag("avalonia")]
        [ToolInfo(
            name: "close_avalonia",
            description:
                "Gracefully shut down the running Avalonia UI app by sending an EXIT command " +
                "to TCP port 9876 on localhost. " +
                "The tool first verifies the app is running (PING → PONG) before sending EXIT. " +
                "Returns an error if the app is not running so the LLM does not attempt a " +
                "double-close. After closing, use check_avalonia_status to confirm the app exited.")]
        public static object CloseAvalonia(JsonObject _input)
        {
            string? pingReply = TrySendTcpCommand("PING", out string? pingError);

            if (pingReply != "PONG")
            {
                string reason = pingError
                    ?? $"No PONG received from {AvaloniaHost}:{AvaloniaPort}.";

                return
                    $"Error: Cannot close — Avalonia app is not running. {reason}\n" +
                    "If you believe the app is running but not responding, check Task Manager " +
                    $"for 'GTA.App.exe' and terminate it manually.";
            }

            string? exitReply = TrySendTcpCommand("EXIT", out string? exitError);

            if (exitError != null)
                return
                    $"Error: PING succeeded but EXIT command failed. {exitError}\n" +
                    "The app may be in a bad state. Try check_avalonia_status and if still " +
                    "running, attempt close_avalonia again or kill the process manually.";

            if (exitReply == "OK")
                return
                    $"Avalonia app closed successfully " +
                    $"(received OK from {AvaloniaHost}:{AvaloniaPort}).";

            return
                $"Warning: EXIT was sent but an unexpected reply was received: '{exitReply ?? "(null)"}'. " +
                "The app may or may not have shut down. " +
                "Call check_avalonia_status to verify the current state.";
        }

        // ── avalonia_send_message ──────────────────────────────────────────────────

        [ToolTag("avalonia")]
        [ToolInfo(
            name: "avalonia_send_message",
            description:
                "Send a chat message to the running Avalonia UI app as if the user typed " +
                "it and pressed Enter. " +
                "The message is posted to the active chat session and triggers an LLM response. " +
                "Optionally specify an agent_name to run this specific message with a different " +
                "agent (does not change the UI's selected agent permanently). " +
                "By default the currently selected agent (or the default all-tools agent) is used. " +
                "Returns a success confirmation, or an error if the app is not running.")]
        [Parameter(
            index: 0,
            name: "message",
            type: "string",
            description: "The message text to send into the active chat session. Must be non-empty.",
            required: true)]
        [Parameter(
            index: 1,
            name: "agent_name",
            type: "string",
            description:
                "Optional. Name of the agent to use for this message only " +
                "(does not permanently change the UI's selected agent). " +
                "Use list_agents to see all available agent names. " +
                "If omitted, the currently selected agent in the UI is used.",
            required: false)]
        public static object AvaloniaSendMessage(JsonObject input)
        {
            string? message = input["message"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(message))
                return
                    "Error: 'message' parameter is missing or empty. " +
                    "Provide the text you want to send to the chat window.";

            string? pingReply = TrySendTcpCommand("PING", out string? pingError);
            if (pingReply != "PONG")
            {
                string reason = pingError
                    ?? $"No PONG received from {AvaloniaHost}:{AvaloniaPort}.";
                return
                    $"Error: Cannot send message — Avalonia app is not running. {reason}\n" +
                    "Use launch_avalonia to start the app first.";
            }

            // Use SEND_MESSAGE_AGENT if an agent override is requested
            string? agentName = input["agent_name"]?.GetValue<string>()?.Trim();
            string  command;

            if (!string.IsNullOrWhiteSpace(agentName))
                command = $"SEND_MESSAGE_AGENT {agentName}|{message}";
            else
                command = $"SEND_MESSAGE {message}";

            string? reply = TrySendTcpCommand(command, out string? tcpError);

            if (tcpError != null)
                return
                    $"Error: SEND_MESSAGE command failed. {tcpError}\n" +
                    "The app may be in a bad state. Try check_avalonia_status.";

            if (reply != null && reply.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                string agentNote = !string.IsNullOrWhiteSpace(agentName)
                    ? $" (using agent '{agentName}')"
                    : "";
                return $"Message sent successfully{agentNote}: \"{message}\"";
            }

            if (reply != null && reply.StartsWith("ERROR ", StringComparison.OrdinalIgnoreCase))
                return $"Error: Avalonia app reported: {reply[6..].Trim()}";

            return
                $"Warning: Unexpected reply from app: '{reply ?? "(null)"}'. " +
                "The message may or may not have been delivered.";
        }

        // ── avalonia_busy ──────────────────────────────────────────────────────────

        [ToolTag("avalonia")]
        [ToolInfo(
            name: "avalonia_busy",
            description:
                "Return whether the Avalonia UI app is currently busy (the LLM is working) " +
                "or idle (ready to accept a new message). " +
                "Returns 'BUSY' while an LLM request is in-flight, or 'IDLE' when the app " +
                "is waiting for user input. " +
                "Useful for debugging: poll this after avalonia_send_message to know when " +
                "the response is complete before taking a screenshot or sending a follow-up.")]
        public static object AvaloniaBusy(JsonObject _input)
        {
            string? pingReply = TrySendTcpCommand("PING", out string? pingError);
            if (pingReply != "PONG")
            {
                string reason = pingError
                    ?? $"No PONG received from {AvaloniaHost}:{AvaloniaPort}.";
                return
                    $"Error: Cannot check busy status — Avalonia app is not running. {reason}\n" +
                    "Use launch_avalonia to start the app first.";
            }

            string? reply = TrySendTcpCommand("BUSY", out string? tcpError);

            if (tcpError != null)
                return $"Error: BUSY command failed. {tcpError}";

            return reply switch
            {
                "BUSY" => "BUSY — the LLM is currently processing a request.",
                "IDLE" => "IDLE — the app is ready to accept a new message.",
                _      =>
                    $"Warning: Unexpected reply from app: '{reply ?? "(null)"}'. " +
                    "Try check_avalonia_status to confirm the app is still alive.",
            };
        }

        // ── avalonia_get_agent ────────────────────────────────────────────────────

        [ToolTag("avalonia")]
        [ToolInfo(
            name: "avalonia_get_agent",
            description:
                "Return the name of the agent currently selected in the running Avalonia UI app. " +
                "The selected agent determines which system prompt and tool set is used for " +
                "new chat messages sent from the UI. " +
                "Use this to confirm which agent is active before sending a message or " +
                "calling avalonia_set_agent.")]
        public static object AvaloniaGetAgent(JsonObject _input)
        {
            string? pingReply = TrySendTcpCommand("PING", out string? pingError);
            if (pingReply != "PONG")
            {
                string reason = pingError
                    ?? $"No PONG received from {AvaloniaHost}:{AvaloniaPort}.";
                return
                    $"Error: Cannot get agent — Avalonia app is not running. {reason}\n" +
                    "Use launch_avalonia to start the app first.";
            }

            string? reply = TrySendTcpCommand("GET_AGENT", out string? tcpError);

            if (tcpError != null)
                return $"Error: GET_AGENT command failed. {tcpError}";

            if (reply != null && reply.StartsWith("OK ", StringComparison.OrdinalIgnoreCase))
            {
                string agentName = reply[3..].Trim();
                return $"Currently selected agent: '{agentName}'";
            }

            if (reply != null && reply.StartsWith("ERROR ", StringComparison.OrdinalIgnoreCase))
                return $"Error: Avalonia app reported: {reply[6..].Trim()}";

            return
                $"Warning: Unexpected reply from GET_AGENT: '{reply ?? "(null)"}'. " +
                "The agent could not be determined.";
        }

        // ── avalonia_set_agent ────────────────────────────────────────────────────

        [ToolTag("avalonia")]
        [ToolInfo(
            name: "avalonia_set_agent",
            description:
                "Change the active agent in the running Avalonia UI app. " +
                "The selected agent determines which system prompt and tool set is used " +
                "for all subsequent chat messages sent from the UI. " +
                "Use list_agents (if available) or call avalonia_get_agent to see what agents " +
                "are registered. The default agent has all tools enabled.")]
        [Parameter(
            index: 0,
            name: "agent_name",
            type: "string",
            description:
                "Name of the agent to activate (case-insensitive). " +
                "Must match a registered agent name. " +
                "Use 'default' to restore the all-tools default agent.",
            required: true)]
        public static object AvaloniaSetAgent(JsonObject input)
        {
            string? agentName = input["agent_name"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(agentName))
                return
                    "Error: 'agent_name' parameter is missing or empty. " +
                    "Provide the name of the agent to activate, e.g. 'default', 'orchestrator'.";

            string? pingReply = TrySendTcpCommand("PING", out string? pingError);
            if (pingReply != "PONG")
            {
                string reason = pingError
                    ?? $"No PONG received from {AvaloniaHost}:{AvaloniaPort}.";
                return
                    $"Error: Cannot set agent — Avalonia app is not running. {reason}\n" +
                    "Use launch_avalonia to start the app first.";
            }

            string? reply = TrySendTcpCommand($"SET_AGENT {agentName}", out string? tcpError);

            if (tcpError != null)
                return $"Error: SET_AGENT command failed. {tcpError}";

            if (reply != null && reply.StartsWith("OK ", StringComparison.OrdinalIgnoreCase))
            {
                string confirmedName = reply[3..].Trim();
                return $"Agent changed successfully to: '{confirmedName}'";
            }

            if (reply != null && reply.StartsWith("ERROR ", StringComparison.OrdinalIgnoreCase))
                return $"Error: Avalonia app reported: {reply[6..].Trim()}";

            return
                $"Warning: Unexpected reply from SET_AGENT: '{reply ?? "(null)"}'. " +
                "The agent may or may not have been changed. " +
                "Use avalonia_get_agent to verify.";
        }

        // ── Shared TCP helper ─────────────────────────────────────────────────────

        private static string? TrySendTcpCommand(string command, out string? error)
        {
            error = null;
            var tcp = new TcpClient();

            try
            {
                bool connected = tcp.ConnectAsync(AvaloniaHost, AvaloniaPort)
                                    .Wait(ConnectTimeoutMs);

                if (!connected)
                {
                    error =
                        $"Connection to {AvaloniaHost}:{AvaloniaPort} timed out after " +
                        $"{ConnectTimeoutMs} ms. " +
                        "Avalonia app is likely not running or is busy.";
                    return null;
                }

                tcp.ReceiveTimeout = ReadTimeoutMs;
                tcp.SendTimeout    = ReadTimeoutMs;

                using var stream = tcp.GetStream();
                using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(stream, leaveOpen: true);

                writer.WriteLine(command);
                return reader.ReadLine();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                error =
                    $"Connection refused on {AvaloniaHost}:{AvaloniaPort}. " +
                    "Avalonia app is not running. Use launch_avalonia to start it.";
                return null;
            }
            catch (AggregateException aex)
                when (aex.InnerException is SocketException sex
                      && sex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                error =
                    $"Connection refused on {AvaloniaHost}:{AvaloniaPort}. " +
                    "Avalonia app is not running. Use launch_avalonia to start it.";
                return null;
            }
            catch (IOException ex)
            {
                error = $"Network I/O error while communicating with Avalonia app: {ex.Message}";
                return null;
            }
            catch (Exception ex)
            {
                error = $"Unexpected TCP error: {ex.GetType().Name}: {ex.Message}";
                return null;
            }
            finally
            {
                tcp.Dispose();
            }
        }
    }
}
#endif
