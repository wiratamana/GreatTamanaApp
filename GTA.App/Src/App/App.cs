using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using GTA.Core;
using GTA.Tools;

namespace GTA.App;

/// <summary>Avalonia application root, configured entirely in C# (no AXAML).</summary>
public class App : Application
{
    /// <summary>Default SCREENSHOT output dir (project folder, 4 levels above the bin dir).</summary>
    public static string ScreenshotOutputDir { get; set; } =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

#if !CO_WORKER_USE
    private TcpCommandServer? _tcpServer;
#endif

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ToastNotifier.Register();

            var window = new LlmChatWindow();
            desktop.MainWindow = window;

            // toFile:false keeps the long registration list out of the session log file
            ToolRegistry.Initialize(msg => window.AppendLog(msg, toFile: false));
            window.AppendLog($"ToolRegistry ready — {ToolRegistry.Tools.Count} tool(s) registered.");

            // Must run after ToolRegistry.Initialize(), which resets the registry state
            AvaloniaOnlyToolRegistrar.RegisterAll(msg => window.AppendLog(msg, toFile: false));

            // Agents dir: next to the exe first, then the solution root (dev scenario)
            string agentsDir = Path.Combine(AppContext.BaseDirectory, "agents");
            if (!Directory.Exists(agentsDir))
            {
                agentsDir = Path.GetFullPath(
                    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "agents"));
            }
            AgentRegistry.LoadFromDirectory(agentsDir, msg => window.AppendLog(msg));
            window.AppendLog($"AgentRegistry ready — {AgentRegistry.All.Count} agent(s) available.");

            window.RefreshAgentSelector();

#if CO_WORKER_USE
            // Encrypted credentials were loaded in Program.Main. Credentials are optional; Settings opens by itself only for a damaged file.
            window.AppendLog("[Settings] " + SecureCredentialStore.StatusMessage, toFile: false);
            window.Opened += async (_, _) => await window.PromptForCredentialsIfNeededAsync();
#endif

#if !CO_WORKER_USE
            // TCP command server: removed in the co-worker build
            // (the TCP server lets another local process drive the chat; the avalonia_* tools are its only client).
            _tcpServer = new TcpCommandServer(window, ScreenshotOutputDir);
            bool tcpOk = _tcpServer.Start();

            window.AppendLog(tcpOk
                ? $"TcpCommandServer listening on 127.0.0.1:{TcpCommandServer.Port}"
                : $"⚠ TcpCommandServer NOT started: port {TcpCommandServer.Port} is in use (another instance running?). TCP commands are unavailable in this instance.");
            window.AppendLog($"Screenshot dir → {ScreenshotOutputDir}");
#endif

            desktop.ShutdownRequested += async (_, _) =>
            {
                SessionFileLogger.Log("ShutdownRequested — disposing servers...");

                window.ShutdownAllSessions();

#if !CO_WORKER_USE
                _tcpServer?.Dispose();
#endif
                await System.Threading.Tasks.Task.CompletedTask;
                SessionFileLogger.Log("Servers disposed.");

                ToastNotifier.Unregister();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
