using System.Text.Json;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Common;
using GTA.Tools.Impl.Jira;

namespace GTA.Tools
{
    // Base of the jira_* tools: shared defaults, thin transport wrappers, attachment URL resolver.
    internal static partial class ToolsJiraApi
    {
        // Default Jira tenant, read from Config\init.ini (key: jira_base_url).
        private static string JiraDefaultBaseUrl => GTA.Core.InitSettings.Get("jira_base_url", "");

        private static readonly JsonSerializerOptions JsonOptions = ToolsCommonPipe.JsonOptions;

        private static string SendCommand(object requestObj)
            => JiraDirectTransport.SendCommand(requestObj);

        private static object SendCommandForImage(object requestObj)
            => JiraDirectTransport.SendCommandForImage(requestObj);

        private static object SendCommandForFileBase64(object requestObj)
            => JiraDirectTransport.SendCommandForFileBase64(requestObj);

        private static object SendCommandForFileBase64WithRetry(object requestObj)
            => JiraDirectTransport.SendCommandForFileBase64WithRetry(requestObj);

        /// <summary>Resolves the attachment download URL from 'url' or 'attachment_id' + optional 'jira_base_url'.</summary>
        private static string? ResolveJiraAttachmentUrl(JsonObject input)
        {
            string? directUrl = input["url"]?.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(directUrl)) return directUrl;

            string? attachmentId = input["attachment_id"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(attachmentId)) return null;

            string baseUrl = (input["jira_base_url"]?.GetValue<string>()
                              ?? JiraDefaultBaseUrl).TrimEnd('/');
            return $"{baseUrl}/rest/api/3/attachment/content/{attachmentId}";
        }
    }
}
