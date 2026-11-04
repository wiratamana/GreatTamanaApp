using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GTA.Core;

public static class JsonUtils
{
    private static readonly JsonSerializerOptions _indentedOpts =
        new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder       = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

    /// <summary>Attempt to pretty-print a JSON string.</summary>
    public static string TryPrettyPrintJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            var node = JsonNode.Parse(json);
            var expanded = ExpandNestedJsonStrings(node);
            return expanded?.ToJsonString(_indentedOpts) ?? json;
        }
        catch
        {
            return json;   // malformed fragment — show raw
        }
    }

    private static JsonNode? ExpandNestedJsonStrings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var kv in obj)
                    result[kv.Key] = ExpandNestedJsonStrings(kv.Value?.DeepClone());
                return result;
            }
            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var item in arr)
                    result.Add(ExpandNestedJsonStrings(item?.DeepClone()));
                return result;
            }
            case JsonValue val when val.TryGetValue<string>(out var s):
            {
                string trimmed = s.TrimStart();
                if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
                {
                    try
                    {
                        var inner = JsonNode.Parse(s);
                        return ExpandNestedJsonStrings(inner);
                    }
                    catch { /* looked like JSON but wasn't — keep as plain string */ }
                }
                return node;
            }
            default:
                return node;
        }
    }
}
