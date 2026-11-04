#if CO_WORKER_USE
using System;
using System.IO;
using System.Text;

namespace GTA.Core;

/// <summary>
/// CO_WORKER_USE only. Holds the "additional system prompt" that a co-worker types in the Settings window.
/// It does NOT replace the built-in prompt. It is added after it:
///     final prompt = built-in prompt + additional prompt (+ the runtime block the app always adds last).
///
/// File: Credentials\additional_system_prompt.txt  (plain UTF-8 text, NOT encrypted: a prompt is not a secret).
/// It lives in the Credentials folder on purpose: that folder is already kept when a PC is updated to a new release,
/// so the co-worker's text survives updates. No file (or an empty file) = nothing is added.
/// </summary>
public static class AdditionalPromptStore
{
    public const string FileName = "additional_system_prompt.txt";

    /// <summary>Hard limit. The text is sent with EVERY request, so a huge text costs tokens on every message.</summary>
    public const int MaxLength = 4000;

    private static readonly object _lock = new();
    private static string _text = "";
    private static bool   _loaded;
    private static string _loadError = "";

    public static string FilePath => Path.Combine(CredentialManager.CredentialsFolder, FileName);

    /// <summary>Empty when the file was read fine (or does not exist). Otherwise why it could not be read.</summary>
    public static string LoadError { get { EnsureLoaded(); return _loadError; } }

    /// <summary>Line endings made the same (\n).</summary>
    public static string Normalize(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>The saved additional text ("" when there is none).</summary>
    public static string GetAdditional()
    {
        EnsureLoaded();
        lock (_lock)
            return _text;
    }

    /// <summary>
    /// Built-in prompt + the user's additional text. Returns <paramref name="builtIn"/> itself when there is no
    /// additional text. Never throws: a broken file must never stop the chat.
    /// </summary>
    public static string Apply(string builtIn)
    {
        try
        {
            string extra = GetAdditional();
            if (string.IsNullOrWhiteSpace(extra))
                return builtIn;

            return builtIn.TrimEnd() +
                   "\n\nADDITIONAL INSTRUCTIONS (added by the user in Settings):\n" +
                   extra.Trim();
        }
        catch
        {
            return builtIn;
        }
    }

    /// <summary>
    /// Saves the additional text. Empty or blank text deletes the file.
    /// Throws on IO errors; the in-memory value is only changed after the file was written.
    /// </summary>
    public static void Save(string? text)
    {
        lock (_lock)
        {
            EnsureLoaded();

            string clean = Normalize(text).Trim();
            if (clean.Length > MaxLength)
                throw new ArgumentException($"The text is too long ({clean.Length:N0} characters). Maximum is {MaxLength:N0}.");

            string path = FilePath;

            if (clean.Length == 0)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // Temp file first, then replace: a crash can never leave a half-written file behind.
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, clean, new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
            }

            _text      = clean;
            _loadError = "";
        }
    }

    private static void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;

            _text      = "";
            _loadError = "";

            string path = FilePath;
            if (!File.Exists(path)) return;

            try
            {
                string text = Normalize(File.ReadAllText(path, Encoding.UTF8)).Trim();
                if (text.Length > MaxLength)
                    text = text.Substring(0, MaxLength);   // someone edited the file by hand: never send more than the limit
                _text = text;
            }
            catch (Exception ex)
            {
                _loadError = $"\"{path}\" could not be read: {ex.Message} No additional prompt is used.";
            }
        }
    }
}
#endif
