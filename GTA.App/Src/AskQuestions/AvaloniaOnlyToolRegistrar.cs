using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using GTA.Tools;

namespace GTA.App;

internal static class AvaloniaOnlyToolRegistrar
{
    public static int RegisterAll(Action<string>? log = null)
    {
        int count = 0;

        IEnumerable<MethodInfo> methods = Assembly.GetExecutingAssembly()
            .GetTypes()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<AvaloniaOnlyToolAttribute>() != null)
            .OrderBy(m => m.DeclaringType?.Name)
            .ThenBy(m => m.Name);

        foreach (MethodInfo method in methods)
        {
            AvaloniaOnlyToolAttribute info = method.GetCustomAttribute<AvaloniaOnlyToolAttribute>()!;

            string[] tags = string.IsNullOrWhiteSpace(info.Tags)
                ? Array.Empty<string>()
                : info.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            List<ParameterAttribute> parms = method
                .GetCustomAttributes<ParameterAttribute>()
                .OrderBy(p => p.Index)
                .ToList();

            var properties = new JsonObject();
            var required   = new JsonArray();

            foreach (ParameterAttribute p in parms)
            {
                properties[p.Name] = new JsonObject
                {
                    ["type"]        = p.Type,
                    ["description"] = p.Description,
                };
                if (p.Required)
                    required.Add(JsonValue.Create(p.Name));
            }

            var schemaEntry = new JsonObject
            {
                ["name"]         = info.Name,
                ["description"]  = info.Description,
                ["input_schema"] = new JsonObject
                {
                    ["type"]       = "object",
                    ["properties"] = properties,
                    ["required"]   = required,
                },
            };

            try
            {
                var del = (Func<JsonObject, object>)Delegate.CreateDelegate(
                    typeof(Func<JsonObject, object>), method);

                ToolRegistry.RegisterExternal(info.Name, schemaEntry, del, tags, info.Hidden, info.FileMutating);

                count++;
                string tagHint = tags.Length > 0 ? $" [{string.Join(", ", tags)}]" : "";
                string hiddenHint = info.Hidden ? " (hidden from AI discovery)" : "";
                log?.Invoke($"  [AvaloniaOnlyTools] + {info.Name,-22} ({method.DeclaringType?.Name}.{method.Name}){tagHint}{hiddenHint}");
            }
            catch (Exception ex)
            {
                log?.Invoke(
                    $"  [AvaloniaOnlyTools] WARN: Could not register '{info.Name}' " +
                    $"({method.DeclaringType?.Name}.{method.Name}): {ex.Message}");
            }
        }

        log?.Invoke($"  [AvaloniaOnlyTools] {count} Avalonia-exclusive tool(s) ready.\n");
        return count;
    }
}
