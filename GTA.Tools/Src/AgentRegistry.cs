using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace GTA.Tools
{
    /// <summary>Global registry for named <see cref="AgentDefinition"/> objects.</summary>
    public static class AgentRegistry
    {
        // ── Storage ───────────────────────────────────────────────────────────

        private static readonly Dictionary<string, AgentDefinition> _agents =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Backing field for <see cref="DefaultAgent"/>.</summary>
        private static AgentDefinition? _defaultAgent;

        // ── Static constructor — bootstrap via reflection ──────────────────────

        static AgentRegistry()
        {
            LoadFromReflection(Assembly.GetExecutingAssembly());
        }

        // ── DefaultAgent property ─────────────────────────────────────────────

        /// <summary>The default agent — the one whose method is decorated with <see cref="DefaultAgentAttribute"/>.</summary>
        public static AgentDefinition DefaultAgent =>
            _defaultAgent ?? FallbackDefaultAgent;

        /// <summary>Hard-coded fallback used only when reflection finds no <see cref="DefaultAgentAttribute"/>.</summary>
        private static readonly AgentDefinition FallbackDefaultAgent = new()
        {
            Name         = "default",
            SystemPrompt = "You are a helpful and knowledgeable assistant.",
            ToolTags     = new[]
            {
                "essential", "io-specialist", "directory",
                "archive", "external", "image", "shell", "build",
                "avalonia", "agent-dispatch"
            },
            MaxTokens = 128_000,
            Thinking  = "adaptive",
            Effort    = "high",
        };

        // ── Public surface ────────────────────────────────────────────────────

        /// <summary>All registered agents keyed by name (case-insensitive).</summary>
        public static IReadOnlyDictionary<string, AgentDefinition> All => _agents;

        /// <summary>Register a single agent definition, overwriting any existing entry with the same name.</summary>
        public static void Register(AgentDefinition agent)
        {
            if (string.IsNullOrWhiteSpace(agent.Name))
                throw new ArgumentException("Agent name must not be empty.", nameof(agent));
            _agents[agent.Name] = agent;
        }

        /// <summary>Look up a registered agent by name (case-insensitive).</summary>
        public static AgentDefinition? TryGet(string name)
            => _agents.TryGetValue(name, out AgentDefinition? def) ? def : null;

        // ── LoadFromReflection ────────────────────────────────────────────────

        public static int LoadFromReflection(Assembly assembly, Action<string>? log = null)
        {
            if (assembly == null)
            {
                log?.Invoke("  [AgentRegistry] LoadFromReflection: assembly is null — skipped.");
                return 0;
            }

            bool defaultFound = false;
            int  loaded       = 0;

            IEnumerable<MethodInfo> methods;
            try
            {
                methods = assembly.GetTypes()
                    .SelectMany(t => t.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    .Where(m => m.GetCustomAttribute<AgentInfoAttribute>() != null)
                    .OrderBy(m => m.DeclaringType?.Name)
                    .ThenBy(m => m.Name);
            }
            catch (Exception ex)
            {
                log?.Invoke(
                    $"  [AgentRegistry] ERROR scanning assembly '{assembly.GetName().Name}': " +
                    ex.Message);
                return 0;
            }

            foreach (MethodInfo method in methods)
            {
                var agentAttr = method.GetCustomAttribute<AgentInfoAttribute>()!;
                string attrName = agentAttr.Name;

                // Validate: must be static, parameterless, return AgentDefinition
                if (!method.IsStatic)
                {
                    log?.Invoke(
                        $"  [AgentRegistry] WARN: '{method.DeclaringType?.Name}.{method.Name}' " +
                        "is not static — skipped.");
                    continue;
                }

                if (method.GetParameters().Length > 0)
                {
                    log?.Invoke(
                        $"  [AgentRegistry] WARN: '{method.DeclaringType?.Name}.{method.Name}' " +
                        "has parameters — skipped (must be parameterless).");
                    continue;
                }

                if (!typeof(AgentDefinition).IsAssignableFrom(method.ReturnType))
                {
                    log?.Invoke(
                        $"  [AgentRegistry] WARN: '{method.DeclaringType?.Name}.{method.Name}' " +
                        "does not return AgentDefinition — skipped.");
                    continue;
                }

                // Invoke the factory method
                AgentDefinition? def;
                try
                {
                    def = method.Invoke(null, null) as AgentDefinition;
                }
                catch (Exception ex)
                {
                    log?.Invoke(
                        $"  [AgentRegistry] ERROR invoking " +
                        $"'{method.DeclaringType?.Name}.{method.Name}': {ex.Message}");
                    continue;
                }

                if (def == null)
                {
                    log?.Invoke(
                        $"  [AgentRegistry] WARN: " +
                        $"'{method.DeclaringType?.Name}.{method.Name}' returned null — skipped.");
                    continue;
                }

                // If attr name differs from def.Name, log a warning but use attr name as key
                if (!string.Equals(def.Name, attrName, StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke(
                        $"  [AgentRegistry] WARN: [{method.DeclaringType?.Name}.{method.Name}] " +
                        $"AgentInfo name '{attrName}' differs from AgentDefinition.Name '{def.Name}'. " +
                        $"Registering under '{attrName}'.");
                    def = new AgentDefinition
                    {
                        Name         = attrName,
                        SystemPrompt = def.SystemPrompt,
                        ToolTags     = def.ToolTags,
                        MaxTokens    = def.MaxTokens,
                        Thinking     = def.Thinking,
                        Effort       = def.Effort,
                    };
                }

                _agents[attrName] = def;
                loaded++;

                // Check for [DefaultAgent] — only the FIRST one wins
                bool isDefault = !defaultFound &&
                    method.GetCustomAttribute<DefaultAgentAttribute>() != null;

                if (isDefault)
                {
                    _defaultAgent = def;
                    defaultFound  = true;
                }

                string tagList = def.ToolTags.Length > 0
                    ? string.Join(", ", def.ToolTags)
                    : "(none)";
                string defaultMark = isDefault ? " [DEFAULT]" : "";

                log?.Invoke(
                    $"  [AgentRegistry] + {def.Name,-26} " +
                    $"[{def.ToolTags.Length} tag(s): {tagList}]{defaultMark} " +
                    $"← {method.DeclaringType?.Name}.{method.Name}");
            }

            log?.Invoke(
                $"  [AgentRegistry] Reflection load: {loaded} agent(s) from " +
                $"'{assembly.GetName().Name}'. " +
                (defaultFound
                    ? $"Default agent: '{_defaultAgent?.Name}'."
                    : "No [DefaultAgent] found — using hardcoded fallback.") +
                $"  Total registered: {_agents.Count}.\n");

            return loaded;
        }

        // ── LoadFromDirectory ─────────────────────────────────────────────────

        /// <summary>Scan <paramref name="directory"/> for <c>*.json</c> agent script files and register each as an <see cref="AgentDefinition"/>.</summary>
        public static int LoadFromDirectory(string directory, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                log?.Invoke("  [AgentRegistry] ERROR: directory path is null or empty.");
                return 0;
            }

            if (!Directory.Exists(directory))
            {
                log?.Invoke(
                    $"  [AgentRegistry] Agents directory not found: '{directory}'. " +
                     "No JSON agents loaded (all built-in C# agents still available).");
                return 0;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                log?.Invoke($"  [AgentRegistry] ERROR scanning '{directory}': {ex.Message}");
                return 0;
            }

            if (files.Length == 0)
            {
                log?.Invoke(
                    $"  [AgentRegistry] No *.json files found in '{directory}'. " +
                     "No JSON agents loaded (all built-in C# agents still available).");
                return 0;
            }

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas         = true,
                ReadCommentHandling         = JsonCommentHandling.Skip,
            };

            int loaded = 0;

            foreach (string file in files)
            {
                string filename = Path.GetFileName(file);
                try
                {
                    string json = File.ReadAllText(file, System.Text.Encoding.UTF8);

                    AgentDefinition? def =
                        JsonSerializer.Deserialize<AgentDefinition>(json, jsonOptions);

                    if (def == null)
                    {
                        log?.Invoke(
                            $"  [AgentRegistry] WARN: '{filename}' parsed to null — skipped.");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(def.Name))
                    {
                        log?.Invoke(
                            $"  [AgentRegistry] WARN: '{filename}' has no 'name' field — skipped.");
                        continue;
                    }

                    _agents[def.Name] = def;

                    string tagList = def.ToolTags.Length > 0
                        ? string.Join(", ", def.ToolTags)
                        : "(none)";
                    log?.Invoke(
                        $"  [AgentRegistry] + {def.Name,-26} " +
                        $"[{def.ToolTags.Length} tag(s): {tagList}] ← {filename}");
                    loaded++;
                }
                catch (JsonException ex)
                {
                    log?.Invoke(
                        $"  [AgentRegistry] ERROR parsing '{filename}': {ex.Message}");
                }
                catch (Exception ex)
                {
                    log?.Invoke(
                        $"  [AgentRegistry] ERROR loading '{filename}': " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }

            log?.Invoke(
                $"  [AgentRegistry] {loaded}/{files.Length} agent script(s) loaded. " +
                $"Total registered: {_agents.Count} agent(s).\n");

            return loaded;
        }

        // ── Diagnostics ───────────────────────────────────────────────────────

        /// <summary>Format a human-readable summary of all registered agents.</summary>
        internal static string FormatSummary()
        {
            if (_agents.Count == 0) return "No agents registered.";

            var sb = new StringBuilder();
            sb.AppendLine($"Registered agents ({_agents.Count} total):");
            sb.AppendLine($"Default agent: {DefaultAgent.Name}");
            sb.AppendLine();

            foreach (KeyValuePair<string, AgentDefinition> kv in _agents)
            {
                AgentDefinition a    = kv.Value;
                string          tags = a.ToolTags.Length > 0
                    ? string.Join(", ", a.ToolTags)
                    : "(none)";
                string preview = a.SystemPrompt.Length > 130
                    ? a.SystemPrompt[..130] + "…"
                    : a.SystemPrompt;
                bool isDefault = string.Equals(
                    a.Name, DefaultAgent.Name, StringComparison.OrdinalIgnoreCase);

                sb.AppendLine($"  {a.Name}{(isDefault ? " [DEFAULT]" : "")}");
                sb.AppendLine($"    Tags       : {tags}");
                sb.AppendLine($"    MaxTokens  : {a.MaxTokens:N0}");
                sb.AppendLine($"    Thinking   : {a.Thinking}  |  Effort: {a.Effort}");
                sb.AppendLine($"    Prompt     : {preview}");
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
    }
}
