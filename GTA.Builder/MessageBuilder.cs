using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using GTA.Core;
using GTA.Tools;

namespace GTA.Builder
{
    internal static class MessageBuilder
    {
        public static JsonObject MakeUserMessage(string text) =>
            new JsonObject { ["role"] = "user", ["content"] = text };

        public static JsonObject MakeUserMessageWithFiles(
            string          prompt,
            List<string>    files,
            IAgentLogger?   logger = null)
        {
            var content = new JsonArray();
            content.Add(new JsonObject { ["type"] = "text", ["text"] = prompt });

            foreach (string filePath in files)
            {
                string ext = Path.GetExtension(filePath);
                if (AppConfig.IMAGE_EXTS.Contains(ext))
                {
                    byte[] bytes = File.ReadAllBytes(filePath);
                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"]       = "base64",
                            ["media_type"] = ToolsIOHelpers.GetMediaType(filePath),
                            ["data"]       = System.Convert.ToBase64String(bytes)
                        }
                    });
                    logger?.LogInfo(
                        $"  [File] Attached image: {Path.GetFileName(filePath)} ({bytes.Length / 1024} KB)");
                }
                else
                {
                    string text = File.ReadAllText(filePath, Encoding.UTF8);
                    content.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"[Attached file: {Path.GetFileName(filePath)}]\n{text}"
                    });
                    logger?.LogInfo(
                        $"  [File] Attached text:  {Path.GetFileName(filePath)} ({text.Length} chars)");
                }
            }

            return new JsonObject { ["role"] = "user", ["content"] = content };
        }

        public static JsonObject MakeTextToolResult(string toolUseId, string text) =>
            new JsonObject
            {
                ["type"]        = "tool_result",
                ["tool_use_id"] = toolUseId,
                ["content"]     = text
            };

        public static JsonObject MakeToolResult(string toolUseId, JsonNode? content) =>
            new JsonObject
            {
                ["type"]        = "tool_result",
                ["tool_use_id"] = toolUseId,
                ["content"]     = content
            };
    }
}
