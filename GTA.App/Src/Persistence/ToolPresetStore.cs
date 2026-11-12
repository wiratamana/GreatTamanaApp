using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GTA.App;

/// <summary>One named list of tools ("My preset") that the user can load with one click.</summary>
internal sealed class ToolPreset
{
    public string         Name       { get; set; } = "";
    public List<string>   Tools      { get; set; } = new();
    public DateTime       CreatedUtc { get; set; }
    public DateTime       UpdatedUtc { get; set; }

    public ToolPreset Clone() => new()
    {
        Name       = Name,
        Tools      = new List<string>(Tools),
        CreatedUtc = CreatedUtc,
        UpdatedUtc = UpdatedUtc,
    };
}

/// <summary>
/// Saves the user's tool presets in ONE json file next to the app: "tool_presets.json".
/// Shared by every tool picker in the app (the file is the single source of truth).
///
/// - Thread-safe (one lock).
/// - Atomic write: written to a .tmp file first, then moved over the real file, so a crash
///   can never leave a half-written file.
/// - A corrupt file is NEVER overwritten silently: it is renamed to "tool_presets.json.corrupt-yyyyMMdd-HHmmss"
///   and the store starts empty.
/// - Every change raises <see cref="Changed"/> so open pickers refresh their preset list.
/// - Preset names are unique, compared case-insensitively.
/// </summary>
internal static class ToolPresetStore
{
    private sealed class FileModel
    {
        public int              Version { get; set; } = 1;
        public List<ToolPreset> Presets { get; set; } = new();
    }

    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
    };

    private static List<ToolPreset>? _cache;

    /// <summary>Raised after every successful change (save / rename / delete). Raised on the calling thread.</summary>
    public static event Action? Changed;

    /// <summary>Full path of the json file.</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "tool_presets.json");

    /// <summary>Set when the file could not be read (for example it was corrupt). Null when everything is fine.</summary>
    public static string? LoadWarning { get; private set; }

    // ── Read ──────────────────────────────────────────────────────────────

    /// <summary>A copy of all presets, in the order they were created.</summary>
    public static IReadOnlyList<ToolPreset> GetAll()
    {
        lock (_lock)
            return Presets().Select(p => p.Clone()).ToList();
    }

    public static ToolPreset? Find(string name)
    {
        lock (_lock)
            return Presets().FirstOrDefault(p => SameName(p.Name, name))?.Clone();
    }

    // ── Write ─────────────────────────────────────────────────────────────

    /// <summary>Creates the preset, or overwrites its tool list when the name already exists. Returns an error text, or null on success.</summary>
    public static string? Save(string name, IEnumerable<string> tools)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return "The preset name is empty.";

        List<string> list = Normalize(tools);

        string? error = Mutate(presets =>
        {
            DateTime now = DateTime.UtcNow;
            ToolPreset? existing = presets.FirstOrDefault(p => SameName(p.Name, name));
            if (existing == null)
            {
                presets.Add(new ToolPreset { Name = name, Tools = list, CreatedUtc = now, UpdatedUtc = now });
            }
            else
            {
                existing.Name       = name;
                existing.Tools      = list;
                existing.UpdatedUtc = now;
            }
            return null;
        });

        return error;
    }

    /// <summary>Renames a preset. Returns an error text, or null on success.</summary>
    public static string? Rename(string oldName, string newName)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) return "The preset name is empty.";

        return Mutate(presets =>
        {
            ToolPreset? p = presets.FirstOrDefault(x => SameName(x.Name, oldName));
            if (p == null) return "That preset does not exist any more.";

            bool clash = presets.Any(x => !ReferenceEquals(x, p) && SameName(x.Name, newName));
            if (clash) return "A preset with this name already exists.";

            p.Name       = newName;
            p.UpdatedUtc = DateTime.UtcNow;
            return null;
        });
    }

    /// <summary>Deletes a preset. Returns an error text, or null on success.</summary>
    public static string? Delete(string name) =>
        Mutate(presets =>
        {
            int n = presets.RemoveAll(p => SameName(p.Name, name));
            return n == 0 ? "That preset does not exist any more." : null;
        });

    // ── Internals ─────────────────────────────────────────────────────────

    public static bool SameName(string a, string b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static List<string> Normalize(IEnumerable<string> tools) =>
        tools.Where(t => !string.IsNullOrWhiteSpace(t))
             .Select(t => t.Trim())
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
             .ToList();

    private static List<ToolPreset> Presets() => _cache ??= Load();

    /// <summary>Applies change to a COPY of the list, writes it to disk, and only then makes it the live list.</summary>
    private static string? Mutate(Func<List<ToolPreset>, string?> change)
    {
        string? error;
        lock (_lock)
        {
            List<ToolPreset> copy = Presets().Select(p => p.Clone()).ToList();

            error = change(copy);
            if (error != null) return error;

            error = Write(copy);
            if (error != null) return error;

            _cache = copy;
        }

        try { Changed?.Invoke(); }
        catch { /* a broken listener must never break saving */ }
        return null;
    }

    private static List<ToolPreset> Load()
    {
        LoadWarning = null;
        string path = FilePath;
        if (!File.Exists(path)) return new List<ToolPreset>();

        try
        {
            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return new List<ToolPreset>();

            FileModel? model = JsonSerializer.Deserialize<FileModel>(text, _json);
            var result = new List<ToolPreset>();
            if (model?.Presets == null) return result;

            foreach (ToolPreset p in model.Presets)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Name)) continue;
                if (result.Any(x => SameName(x.Name, p.Name))) continue; // duplicate name: keep the first

                p.Name  = p.Name.Trim();
                p.Tools = Normalize(p.Tools ?? new List<string>());
                result.Add(p);
            }
            return result;
        }
        catch (Exception ex)
        {
            // Keep the unreadable file for the user instead of overwriting it with an empty list later.
            try
            {
                string backup = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(path, backup);
                LoadWarning = $"tool_presets.json could not be read ({ex.Message}). It was kept as \"{Path.GetFileName(backup)}\".";
            }
            catch
            {
                LoadWarning = $"tool_presets.json could not be read ({ex.Message}).";
            }
            return new List<ToolPreset>();
        }
    }

    private static string? Write(List<ToolPreset> presets)
    {
        try
        {
            string path = FilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(new FileModel { Presets = presets }, _json);
            string tmp  = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not write {Path.GetFileName(FilePath)}: {ex.Message}";
        }
    }
}
