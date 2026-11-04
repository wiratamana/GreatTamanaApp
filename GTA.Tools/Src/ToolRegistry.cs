using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using SessionFileLogger = GTA.Core.SessionFileLogger;

namespace GTA.Tools
{
    /// <summary>Mark any static method with this to register it as a tool.</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class ToolInfoAttribute : Attribute
    {
        public string Name        { get; }
        public string Description { get; }

        public ToolInfoAttribute(string name, string description)
        {
            Name        = name;
            Description = description;
        }
    }

    /// <summary>Describes one input field of a tool.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public sealed class ParameterAttribute : Attribute
    {
        public int    Index       { get; }
        public string Name        { get; }
        public string Type        { get; }   // JSON Schema type: "string", "integer", "boolean", ...
        public string Description { get; }
        public bool   Required    { get; }

        public ParameterAttribute(int index, string name, string type, string description, bool required = true)
        {
            Index       = index;
            Name        = name;
            Type        = type;
            Description = description;
            Required    = required;
        }
    }

    /// <summary>Tags a tool method with one or more category labels.</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class ToolTagAttribute : Attribute
    {
        public string[] Tags { get; }

        public ToolTagAttribute(params string[] tags)
        {
            Tags = tags ?? Array.Empty<string>();
        }
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class FileMutatingAttribute : Attribute
    {
    }

    /// <summary>Marks a tool method as DANGEROUS / worth tracing: every call of it is streamed to the session log file (logs/session_*.log) AS IT HAPPENS.</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class RecordToLogAttribute : Attribute
    {
    }

    public static class ToolRegistry
    {
        // ── Internal storage ──────────────────────────────────────────────────────

        private static readonly Dictionary<string, Func<JsonObject, object>> _tools =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Maps tool name → declared (parameterName, JSON-schema type) pairs. Used by <see cref="CoerceInput"/>.</summary>
        private static readonly Dictionary<string, (string Name, string Type)[]> _paramTypes =
            new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Maps tool name → its ToolTag tags (empty array = always included).</summary>
        private static readonly Dictionary<string, string[]> _toolTags =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Set of tool names carrying <see cref="FileMutatingAttribute"/>.</summary>
        private static readonly HashSet<string> _fileMutatingTools =
            new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Set of tool names carrying <see cref="RecordToLogAttribute"/>.</summary>
        private static readonly HashSet<string> _recordToLogTools =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tool names that must be registered and callable but hidden from the schema.</summary>
        private static readonly HashSet<string> _hiddenTools =
            new(StringComparer.OrdinalIgnoreCase);

        // ── Public surface ────────────────────────────────────────────────────────

        /// <summary>Registered tools keyed by tool name (case-insensitive).</summary>
        public static IReadOnlyDictionary<string, Func<JsonObject, object>> Tools => _tools;

        /// <summary>JSON schema array for ALL registered tools.</summary>
        public static JsonArray Schema { get; private set; } = new JsonArray();

        // ── Active-session tool array (lazy tool loading support) ────────────────

        /// <summary>AsyncLocal backing store for <see cref="ActiveTools"/>.</summary>
        private static readonly AsyncLocal<JsonArray?> _activeTools = new();

        public static JsonArray? ActiveTools
        {
            get => _activeTools.Value;
            set => _activeTools.Value = value;
        }

        // ── Live per-tool progress reporting (see IAgentLogger.LogToolProgress) ──

        private static readonly AsyncLocal<Action<string>?> _progressSink = new();

        public static Action<string>? ProgressSink
        {
            get => _progressSink.Value;
            set => _progressSink.Value = value;
        }

        /// <summary>Report one line of live progress for whichever tool is CURRENTLY executing on this async call-flow (see <see cref="ProgressSink"/>).</summary>
        public static void ReportProgress(string message) => _progressSink.Value?.Invoke(message);

        // ── Initialize ────────────────────────────────────────────────────────────

        /// <summary>Scans every type in the executing assembly for static methods decorated with [ToolInfo].</summary>
        public static void Initialize(Action<string>? log = null)
        {
            _tools.Clear();
            _paramTypes.Clear();
            _toolTags.Clear();
            _fileMutatingTools.Clear();
            _recordToLogTools.Clear();
            var schemaList = new JsonArray();

            IEnumerable<MethodInfo> methods = Assembly.GetExecutingAssembly()
                .GetTypes()
                .SelectMany(t => t.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .Where(m => m.GetCustomAttribute<ToolInfoAttribute>() != null)
                .OrderBy(m => m.DeclaringType?.Name)
                .ThenBy(m => m.Name);

            foreach (MethodInfo method in methods)
            {
                ToolInfoAttribute info = method.GetCustomAttribute<ToolInfoAttribute>()!;

                // Collect tags (empty if no [ToolTag] attribute — tool is always included).
                ToolTagAttribute? tagAttr = method.GetCustomAttribute<ToolTagAttribute>();
                string[] tags = tagAttr?.Tags ?? Array.Empty<string>();

                // See FileMutatingAttribute's own doc comment — dispatched Sub Agent gating.
                bool isFileMutating = method.GetCustomAttribute<FileMutatingAttribute>() != null;

                // See RecordToLogAttribute's own doc comment — streamed to the session log.
                bool recordToLog = method.GetCustomAttribute<RecordToLogAttribute>() != null;

                List<ParameterAttribute> parms = method
                    .GetCustomAttributes<ParameterAttribute>()
                    .OrderBy(p => p.Index)
                    .ToList();

                // ── Build JSON schema for this tool ───────────────────────────────
                var properties = new JsonObject();
                var required   = new JsonArray();

                foreach (ParameterAttribute p in parms)
                {
                    properties[p.Name] = new JsonObject
                    {
                        ["type"]        = p.Type,
                        ["description"] = p.Description
                    };
                    if (p.Required)
                        required.Add(JsonValue.Create(p.Name));
                }

                schemaList.Add(new JsonObject
                {
                    ["name"]         = info.Name,
                    ["description"]  = info.Description,
                    ["input_schema"] = new JsonObject
                    {
                        ["type"]       = "object",
                        ["properties"] = properties,
                        ["required"]   = required
                    }
                });

                // ── Register delegate ─────────────────────────────────────────────
                try
                {
                    var del = (Func<JsonObject, object>)Delegate.CreateDelegate(
                        typeof(Func<JsonObject, object>), method);

                    _tools[info.Name]   = del;
                    _paramTypes[info.Name] = parms.Select(p => (p.Name, p.Type)).ToArray();
                    _toolTags[info.Name] = tags;
                    if (isFileMutating) _fileMutatingTools.Add(info.Name);
                    else                _fileMutatingTools.Remove(info.Name);
                    if (recordToLog)    _recordToLogTools.Add(info.Name);
                    else                _recordToLogTools.Remove(info.Name);

                    string tagHint = tags.Length > 0 ? $" [{string.Join(", ", tags)}]" : "";
                    string mutHint = (isFileMutating ? " [FILE-MUTATING]" : "") + (recordToLog ? " [RECORD-TO-LOG]" : "");
                    log?.Invoke($"  [ToolRegistry] + {info.Name,-22} ({method.DeclaringType?.Name}.{method.Name}){tagHint}{mutHint}");
                }
                catch (Exception ex)
                {
                    log?.Invoke(
                        $"  [ToolRegistry] WARN: Could not register '{info.Name}' " +
                        $"({method.DeclaringType?.Name}.{method.Name}): {ex.Message}");
                }
            }

            Schema = schemaList;
            log?.Invoke($"  [ToolRegistry] {_tools.Count} tool(s) ready.\n");
        }

        // ── Tag-filtered schema ───────────────────────────────────────────────────

        public static JsonArray GetSchemaForTags(IReadOnlySet<string> tags)
        {
            var filtered = new JsonArray();

            foreach (JsonNode? entry in Schema)
            {
                if (entry == null) continue;

                string name = entry["name"]?.GetValue<string>() ?? "";

                // No ToolTag recorded (untagged tool) → always include.
                if (!_toolTags.TryGetValue(name, out string[]? toolTags) || toolTags.Length == 0)
                {
                    filtered.Add(entry.DeepClone());
                    continue;
                }

                // Include if the tool's tags intersect with the requested set.
                bool match = false;
                foreach (string t in toolTags)
                {
                    if (tags.Contains(t))
                    {
                        match = true;
                        break;
                    }
                }

                if (match)
                    filtered.Add(entry.DeepClone());
            }

            return filtered;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>Safely execute a tool by name.</summary>
        public static (bool found, object result) TryExecute(string name, JsonObject input)
        {
            if (!_tools.TryGetValue(name, out Func<JsonObject, object>? fn))
                return (false, $"Error: Unknown tool '{name}'");

            bool record = _recordToLogTools.Contains(name);
            var  sw     = Stopwatch.StartNew();

            if (record)
                SessionFileLogger.Log("TOOL_CALL",
                    $"START {name} input={OneLine(SafeToJson(input), MaxLoggedInputChars)}");

            try
            {
                CoerceInput(name, input);
                object result = fn(input);

                if (record)
                    SessionFileLogger.Log("TOOL_CALL",
                        $"END   {name} ok in {sw.ElapsedMilliseconds}ms result={DescribeResult(result)}");

                return (true, result);
            }
            catch (Exception ex)
            {
                SessionFileLogger.LogException($"Tool '{name}' threw after {sw.ElapsedMilliseconds}ms", ex);
                return (true, $"Tool error ({name}): {ex.Message}");
            }
        }

        // ── Smart input coercion ──────────────────────────────────────────────────

        /// <summary>
        /// LLMs often send "7" instead of 7, "true" instead of true, or 7 instead of "7".
        /// JsonNode.GetValue&lt;T&gt;() is strict and throws on these. This fixes the input IN PLACE,
        /// before the tool runs, using the type declared in [Parameter]. Values that cannot be
        /// converted are left unchanged (the tool then reports its own error).
        /// An empty string / "null" for a non-string parameter is treated as "not provided".
        /// </summary>
        private static void CoerceInput(string toolName, JsonObject input)
        {
            if (!_paramTypes.TryGetValue(toolName, out var parms)) return;

            foreach (var (pName, pType) in parms)
            {
                if (!input.TryGetPropertyValue(pName, out JsonNode? node) || node == null) continue;
                if (node is not JsonValue val) continue;   // arrays / objects: leave alone

                switch (pType.ToLowerInvariant())
                {
                    case "integer":
                    {
                        if (val.TryGetValue(out string? s))
                        {
                            s = s?.Trim();
                            if (string.IsNullOrEmpty(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase))
                                input.Remove(pName);   // use the tool's default
                            else if (int.TryParse(s, System.Globalization.NumberStyles.Integer,
                                         System.Globalization.CultureInfo.InvariantCulture, out int i))
                                input[pName] = JsonValue.Create(i);
                            else if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture, out double d) &&
                                     d == Math.Truncate(d) && Math.Abs(d) <= int.MaxValue)
                                input[pName] = JsonValue.Create((int)d);   // "7.0"
                        }
                        else if (val.TryGetValue(out double dn) &&
                                 dn == Math.Truncate(dn) && Math.Abs(dn) <= int.MaxValue)
                        {
                            input[pName] = JsonValue.Create((int)dn);      // 7.0 → 7
                        }
                        break;
                    }

                    case "number":
                    {
                        if (val.TryGetValue(out string? s))
                        {
                            s = s?.Trim();
                            if (string.IsNullOrEmpty(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase))
                                input.Remove(pName);
                            else if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture, out double d))
                                input[pName] = JsonValue.Create(d);
                        }
                        break;
                    }

                    case "boolean":
                    {
                        if (val.TryGetValue(out string? s))
                        {
                            s = s?.Trim().ToLowerInvariant();
                            if (string.IsNullOrEmpty(s) || s == "null")
                                input.Remove(pName);
                            else if (s is "true" or "1" or "yes" or "y" or "on")
                                input[pName] = JsonValue.Create(true);
                            else if (s is "false" or "0" or "no" or "n" or "off")
                                input[pName] = JsonValue.Create(false);
                        }
                        else if (val.TryGetValue(out double dn))
                        {
                            if (dn == 1) input[pName] = JsonValue.Create(true);
                            else if (dn == 0) input[pName] = JsonValue.Create(false);
                        }
                        break;
                    }

                    case "string":
                    {
                        // Number / bool sent where a string is expected (e.g. folder_id: 0).
                        if (!val.TryGetValue(out string? _))
                            input[pName] = JsonValue.Create(val.ToJsonString().Trim('"'));
                        break;
                    }
                }
            }
        }

        // ── Session-log helpers (see TryExecute) ──────────────────────────────────

        private const int MaxLoggedInputChars  = 2000;
        private const int MaxLoggedResultChars = 300;

        private static string SafeToJson(JsonObject input)
        {
            try   { return input.ToJsonString(); }
            catch { return "(input could not be serialized)"; }
        }

        /// <summary>Flatten to ONE log line and cap the length.</summary>
        private static string OneLine(string text, int maxChars)
        {
            string flat = text.Replace("\r", "").Replace("\n", "\\n");
            return flat.Length <= maxChars
                ? flat
                : flat[..maxChars] + $"… (+{flat.Length - maxChars} chars)";
        }

        /// <summary>Short, one-line description of a tool result.</summary>
        private static string DescribeResult(object? result) => result switch
        {
            null          => "(null)",
            string s      => $"[{s.Length} chars] " + OneLine(s, MaxLoggedResultChars),
            JsonNode      => "(JsonNode result, not logged)",
            _             => $"({result.GetType().Name}, not logged)",
        };

        /// <summary>Convert a tool's return value to a JsonNode for ["content"] assignment.</summary>
        public static JsonNode? ToContentNode(object? result)
        {
            return result switch
            {
                JsonNode node => node,
                string   s    => JsonValue.Create(s),
                _             => JsonValue.Create(result?.ToString() ?? string.Empty)
            };
        }

        /// <summary>Return the <c>[ToolTag]</c> tags recorded for a tool name (case-insensitive).</summary>
        public static string[] GetTagsForTool(string name)
            => _toolTags.TryGetValue(name, out string[]? tags) ? tags : Array.Empty<string>();

        public static bool IsFileMutating(string name) => _fileMutatingTools.Contains(name);

        public static bool IsRecordToLog(string name) => _recordToLogTools.Contains(name);

        /// <summary>Find one tool's full schema entry by exact name (case-insensitive) in the unfiltered <see cref="Schema"/>.</summary>
        public static JsonObject? TryGetSchemaEntry(string name)
        {
            foreach (JsonNode? entry in Schema)
            {
                if (entry == null) continue;
                string? entryName = entry["name"]?.GetValue<string>();
                if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                    return (JsonObject)entry.DeepClone()!;
            }
            return null;
        }

        /// <summary>Register a tool whose implementation lives outside this assembly (so it was never discovered by <see cref="Initialize"/>'s reflection scan).</summary>
        public static void RegisterExternal(
            string name,
            JsonObject schemaEntry,
            Func<JsonObject, object> handler,
            IEnumerable<string>? tags = null,
            bool hidden = false,
            bool fileMutating = false,
            bool recordToLog = false)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Tool name must not be blank.", nameof(name));

            _tools[name]    = handler ?? throw new ArgumentNullException(nameof(handler));
            _toolTags[name] = tags?.ToArray() ?? Array.Empty<string>();

            if (hidden) _hiddenTools.Add(name);
            else        _hiddenTools.Remove(name);

            if (fileMutating) _fileMutatingTools.Add(name);
            else              _fileMutatingTools.Remove(name);

            if (recordToLog) _recordToLogTools.Add(name);
            else             _recordToLogTools.Remove(name);

            // Replace any existing schema entry with the same name (idempotent
            // re-registration), then add the fresh one.
            for (int i = Schema.Count - 1; i >= 0; i--)
            {
                string? existingName = Schema[i]?["name"]?.GetValue<string>();
                if (string.Equals(existingName, name, StringComparison.OrdinalIgnoreCase))
                    Schema.RemoveAt(i);
            }
            Schema.Add(schemaEntry.DeepClone());
        }

        /// <summary>True if the tool was registered via <see cref="RegisterExternal"/> with hidden: true.</summary>
        public static bool IsHidden(string name) => _hiddenTools.Contains(name);
    }
}
