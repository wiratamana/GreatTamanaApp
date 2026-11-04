using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Avalonia;
using GTA.Core;

namespace GTA.App;

// Custom args (stripped before Avalonia sees them): --screenshot-dir <path> / -sd <path>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        SessionFileLogger.Initialize();

        NativeAppId.SetCurrentProcessExplicitAppUserModelId();

        SessionFileLogger.Log($"Startup args: [{string.Join(" ", args)}]");
        // A second copy would lose the TCP port and fight over logs/sessions.
        if (!AcquireSingleInstance(args))
        {
            const string msg = "Another instance of this app is already running. This copy exits. " +
                               "(Start with --allow-multiple to run two instances on purpose.)";
            SessionFileLogger.Log(msg);
            Console.Error.WriteLine(msg);
            SessionFileLogger.Close();
            return 2;
        }
#if CO_WORKER_USE
        // Decrypt Credentials\my_credentials.dat into memory. Never throws; a bad file is reported in Settings.
        SecureCredentialStore.Load();
        SessionFileLogger.Log($"Secure credential store: {SecureCredentialStore.Status} ({SecureCredentialStore.StatusMessage})");
#endif


        var (screenshotDir, avaloniaArgs) = ExtractArg(args, "--screenshot-dir", "-sd");

        if (!string.IsNullOrWhiteSpace(screenshotDir))
        {
            App.ScreenshotOutputDir = Path.GetFullPath(screenshotDir);
            SessionFileLogger.Log($"Screenshot dir override: {App.ScreenshotOutputDir}");
        }

        int exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(avaloniaArgs);

        SessionFileLogger.Log($"Avalonia exit code: {exitCode}");
        SessionFileLogger.Close();
        return exitCode;
    }

    // Held for the whole process lifetime (never released on purpose).
    private static Mutex? _singleInstanceMutex;

    /// <summary>
    /// False if another instance of THIS exe path is running. Bypass: --allow-multiple or GTA_ALLOW_MULTIPLE=1.
    /// Fails open: if the mutex cannot be created, the app starts anyway.
    /// </summary>
    private static bool AcquireSingleInstance(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--allow-multiple", StringComparison.OrdinalIgnoreCase))
            || Environment.GetEnvironmentVariable("GTA_ALLOW_MULTIPLE") == "1")
        {
            SessionFileLogger.Log("Single-instance guard bypassed (--allow-multiple / GTA_ALLOW_MULTIPLE).");
            return true;
        }

        try
        {
            // One mutex per exe path, so a copy of the app in another folder is not blocked.
            string exePath = (Environment.ProcessPath ?? AppContext.BaseDirectory).ToLowerInvariant();
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exePath)))[..16];
            var mutex = new Mutex(initiallyOwned: true, $"Local\\GTA.App.{key}", out bool createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }
            _singleInstanceMutex = mutex;
            return true;
        }
        catch (Exception ex)
        {
            SessionFileLogger.LogException("Single-instance guard failed; starting anyway", ex);
            return true;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>Removes the first flag matching any of names plus its value; returns the value and the remaining args.</summary>
    private static (string? value, string[] remaining) ExtractArg(
        string[] args, params string[] names)
    {
        var nameSet   = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var remaining = new List<string>();
        string? value = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (value is null && nameSet.Contains(args[i]) && i + 1 < args.Length)
                value = args[++i];
            else
                remaining.Add(args[i]);
        }

        return (value, remaining.ToArray());
    }
}
