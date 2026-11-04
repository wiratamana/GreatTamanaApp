using System.Diagnostics;
using System.Text;

namespace GTA.Core;

public static class SessionFileLogger
{
    private static StreamWriter? _writer;
    private static readonly object _lock = new();

    // ── Public state ──────────────────────────────────────────────────────────

    /// <summary>Full absolute path of the current session log file.</summary>
    public static string? LogFilePath { get; private set; }

    /// <summary>True when verbose logging is enabled (env GTA_VERBOSE_LOG=1, or a <c>logs/verbose.flag</c> file exists).</summary>
    public static bool IsVerbose { get; private set; }

    // ── Initialise ────────────────────────────────────────────────────────────

    /// <summary>Open the per-session log file and register global crash handlers.</summary>
    public static void Initialize()
    {
        // Logs live next to the executable — works both in dev and publish.
        string primaryDir = Path.Combine(AppContext.BaseDirectory, "logs");
        string? primaryError = null;
        string? fallbackError = null;

        try
        {
            IsVerbose =
                Environment.GetEnvironmentVariable("GTA_VERBOSE_LOG") == "1" ||
                File.Exists(Path.Combine(primaryDir, "verbose.flag"));
        }
        catch { /* verbose stays off */ }

        bool opened = TryOpen(primaryDir, out primaryError);
        if (!opened)
        {
            // The exe folder is not usable: never run without a log, use %TEMP%\GTA_logs.
            string fallbackDir = Path.Combine(Path.GetTempPath(), "GTA_logs");
            opened = TryOpen(fallbackDir, out fallbackError);
        }

        if (!opened)
        {
            Debug.WriteLine($"[SessionFileLogger] Could not open log file. Primary: {primaryError}; fallback: {fallbackError}");
            return;     // do NOT register crash handlers if the file isn't open
        }

        Log("=== Session started ===");
        Log($"Log file  : {LogFilePath}");
        Log($"Executable: {Environment.ProcessPath ?? "(unknown)"}");
        Log($"Base dir  : {AppContext.BaseDirectory}");
        Log($"OS        : {Environment.OSVersion}");
        Log($".NET      : {Environment.Version}");
        Log($"PID       : {Environment.ProcessId}");
        Log($"Verbose   : {(IsVerbose ? "ON" : "off (set GTA_VERBOSE_LOG=1 or create logs\\verbose.flag to enable)")}");
        if (primaryError != null)
            Log($"WARNING   : could not open the log in '{primaryDir}' ({primaryError}). Using the fallback folder above.");

        // ── Global crash handlers ─────────────────────────────────────────────

        // Catches exceptions that escaped every try/catch on ANY thread
        // (CLR thread pool, Avalonia UI thread, etc.).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log($"=== UNHANDLED EXCEPTION (IsTerminating={e.IsTerminating}) ===");
            Log(e.ExceptionObject?.ToString() ?? "(null exception object)");
        };

        // Catches exceptions thrown inside Task / Task<T> that were never
        // awaited and therefore never observed by any caller.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("=== UNOBSERVED TASK EXCEPTION ===");
            Log(e.Exception?.ToString() ?? "(null)");
            // NOTE: intentionally NOT calling e.SetObserved() — let the runtime
            // decide whether to terminate (informational-only on modern .NET).
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Log("ProcessExit event fired (process is exiting by itself, not hard-killed)");
    }

    // ── Open helper ───────────────────────────────────────────────────────────

    /// <summary>Create a NEW session log in <paramref name="logsDir"/> and make it the active writer. Never throws.</summary>
    private static bool TryOpen(string logsDir, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(logsDir);

            // CreateNew + numeric suffix: two instances started in the same second never share one file.
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            FileStream? stream = null;
            string path = "";
            for (int attempt = 0; attempt < 100 && stream == null; attempt++)
            {
                path = Path.Combine(logsDir,
                    attempt == 0 ? $"session_{timestamp}.log" : $"session_{timestamp}_{attempt}.log");
                try
                {
                    // FileShare.Read: others can tail it, but nobody can delete, rename or overwrite it while the app runs.
                    stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // name already taken, try the next suffix
                }
            }
            if (stream == null)
            {
                error = "no free log file name";
                return false;
            }

            // AutoFlush = true → every WriteLine is persisted immediately.
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
            {
                AutoFlush = true,
            };
            LogFilePath = path;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    // ── Public logging surface ────────────────────────────────────────────────

    /// <summary>Write a timestamped line to the session log file.</summary>
    public static void Log(string message)
    {
        if (_writer == null) return;
        lock (_lock)
        {
            try
            {
                _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
            }
            catch
            {
                // Swallow — the app must not crash because logging fails.
            }
        }
    }

    /// <summary>Write a categorised, timestamped line: <c>[timestamp] [CATEGORY] message</c>.</summary>
    public static void Log(string category, string message)
        => Log($"[{category.ToUpperInvariant()}] {message}");

    /// <summary>Like <see cref="Log(string)"/>, but ONLY written when verbose logging is enabled (see class header).</summary>
    public static void LogVerbose(string message)
    {
        if (IsVerbose) Log(message);
    }

    /// <summary>Like <see cref="Log(string,string)"/>, but only when verbose logging is on.</summary>
    public static void LogVerbose(string category, string message)
    {
        if (IsVerbose) Log(category, message);
    }

    public static void LogException(string context, Exception? ex)
    {
        try
        {
            Log("EXCEPTION", $"{context}{(ex == null ? "" : $" — {ex.GetType().Name}: {ex.Message}")}");
            if (ex != null)
                Log(ex.ToString());
        }
        catch
        {
            // Swallow — logging must never throw.
        }
    }

    // ── Shutdown ──────────────────────────────────────────────────────────────

    /// <summary>Write a closing banner, flush, and close the log file.</summary>
    public static void Close()
    {
        Log("=== Session ended ===");
        lock (_lock)
        {
            try { _writer?.Flush(); } catch { /* ignore */ }
            try { _writer?.Close(); } catch { /* ignore */ }
            _writer     = null;
            LogFilePath = null;
        }
    }
}
