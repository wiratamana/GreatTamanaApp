#if !CO_WORKER_USE
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace GTA.App;

/// <summary>Lightweight TCP command server that runs on a background thread while the Avalonia window is alive.</summary>
public sealed partial class TcpCommandServer : IDisposable
{
    /// <summary>Port the server listens on.</summary>
    public const int Port = 9876;

    private const int CommandTimeoutSeconds = 5;

    private readonly Window          _window;
    private readonly string          _screenshotOutputDir;
    private TcpListener?             _listener;
    private CancellationTokenSource? _cts;
    private bool                     _disposed;

    public TcpCommandServer(
        Window window,
        string? screenshotOutputDir = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _screenshotOutputDir = string.IsNullOrWhiteSpace(screenshotOutputDir)
            ? Path.GetTempPath()
            : screenshotOutputDir;
    }

    /// <summary>Starts the TCP listener on a background task. Returns false (and logs why) if the port cannot be bound.</summary>
    public bool Start()
    {
        try
        {
            _cts      = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            GTA.Core.SessionFileLogger.LogException(
                $"TcpCommandServer could not listen on 127.0.0.1:{Port} (another instance running?). Continuing without the TCP command server", ex);
            try { _listener?.Stop(); } catch { /* best effort */ }
            _listener = null;
            return false;
        }

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return true;
    }

    /// <summary>Stops the listener and cancels all pending work.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Swallow transient accept errors (e.g. listener restarted).
            }
        }
    }

    /// <summary>Reads one newline-terminated command, dispatches it, writes the reply.</summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCt)
    {
        using var _ = client;

        using var cmdCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        cmdCts.CancelAfter(TimeSpan.FromSeconds(CommandTimeoutSeconds));
        var ct = cmdCts.Token;

        System.Net.Sockets.NetworkStream? stream = null;
        System.IO.StreamReader?           reader = null;
        System.IO.StreamWriter?           writer = null;

        try
        {
            stream = client.GetStream();
            reader = new System.IO.StreamReader(stream, leaveOpen: true);
            writer = new System.IO.StreamWriter(stream, leaveOpen: true) { AutoFlush = true };

            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) return;

            int    space = line.IndexOf(' ');
            string verb  = (space < 0 ? line : line[..space]).Trim().ToUpperInvariant();
            string arg   = space < 0 ? string.Empty : line[(space + 1)..].Trim();

            LogToWindow($"CMD [{client.Client.RemoteEndPoint}]  {verb}" +
                        (arg.Length > 0 ? $"  {arg}" : string.Empty));

            await DispatchCommandAsync(verb, arg, writer).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (writer != null)
                try { await writer.WriteLineAsync("ERROR Command timed out").ConfigureAwait(false); }
                catch { /* stream may already be dead */ }
        }
        catch (Exception ex)
        {
            if (writer != null)
                try { await writer.WriteLineAsync($"ERROR {ex.Message}").ConfigureAwait(false); }
                catch { /* stream may already be dead */ }
        }
        finally
        {
            reader?.Dispose();
            writer?.Dispose();
            stream?.Dispose();
        }
    }

    private async Task DispatchCommandAsync(string verb, string arg, System.IO.StreamWriter writer)
    {
        switch (verb)
        {
            case "PING":
                await writer.WriteLineAsync("PONG").ConfigureAwait(false);
                break;

            case "STATUS":
                await writer.WriteLineAsync("RUNNING").ConfigureAwait(false);
                break;

            case "SCREENSHOT":
                await HandleScreenshotAsync(arg, writer).ConfigureAwait(false);
                break;

            case "EXIT":
                await writer.WriteLineAsync("OK").ConfigureAwait(false);
                Dispatcher.UIThread.Post(ShutdownApp);
                break;

            case "SEND_MESSAGE":
                await HandleSendMessageAsync(arg, writer).ConfigureAwait(false);
                break;

            case "SEND_MESSAGE_WITH_FILE":
                await HandleSendMessageWithFileAsync(arg, writer).ConfigureAwait(false);
                break;

            case "BUSY":
                await HandleBusyStatusAsync(writer).ConfigureAwait(false);
                break;

            case "GET_AGENT":
                await HandleGetAgentAsync(writer).ConfigureAwait(false);
                break;

            case "SET_AGENT":
                await HandleSetAgentAsync(arg, writer).ConfigureAwait(false);
                break;

            case "SEND_MESSAGE_AGENT":
                await HandleSendMessageWithAgentAsync(arg, writer).ConfigureAwait(false);
                break;

            case "SEND_MESSAGE_FILE_AGENT":
                await HandleSendMessageFileWithAgentAsync(arg, writer).ConfigureAwait(false);
                break;

            default:
                await writer.WriteLineAsync($"ERROR Unknown command: {verb}").ConfigureAwait(false);
                break;
        }
    }

    private static void ShutdownApp()
    {
        if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime app)
            app.Shutdown(0);
    }

    private string ResolveScreenshotPath(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return Path.Combine(_screenshotOutputDir, $"screenshot_{ts}.png");
        }

        return Path.IsPathRooted(arg)
            ? arg
            : Path.Combine(_screenshotOutputDir, arg);
    }

    private void LogToWindow(string message)
    {
        if (_window is LlmChatWindow cw)
            cw.AppendLog(message);
    }
}
#endif
