using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [FileMutating]
        [ToolInfo(
            name: "pptx_new_pptx",
            description:
                "Creates a brand-new, PowerPoint-openable .pptx file containing exactly ONE blank " +
                "slide, and SAVES it to 'path' (creating any missing parent directories; " +
                "overwrites an existing file at that path). This is always the first call when " +
                "building a presentation from scratch — follow it with pptx_new_slide to add more " +
                "slides and the pptx_shape_*/pptx_table/pptx_image/pptx_text_box/pptx_bullet_list " +
                "tools to populate them.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path where the new .pptx file will be created.",
            required: true)]
        [Parameter(index: 1, name: "aspect_ratio",
            type: "string",
            description:
                "Slide aspect ratio: \"16:9\" (default, widescreen, 13.333in x 7.5in) or \"4:3\" " +
                "(classic, 10in x 7.5in).",
            required: false)]
        public static object PptxNewPptx(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveNewPath(input, "path", out string path);
            if (err != null) return err;

            string aspectRatio = (input["aspect_ratio"]?.GetValue<string>() ?? "16:9").Trim();
            long widthEmu, heightEmu;
            switch (aspectRatio)
            {
                case "16:9": widthEmu = PptxCommon.WidescreenWidthEmu; heightEmu = PptxCommon.WidescreenHeightEmu; break;
                case "4:3":  widthEmu = PptxCommon.StandardWidthEmu;   heightEmu = PptxCommon.StandardHeightEmu;   break;
                default:
                    return $"Error: 'aspect_ratio' \"{aspectRatio}\" is not recognized — expected \"16:9\" or \"4:3\".";
            }

            PptxOperationResult result = PptxNewPptxTool.CreateBlankPresentationResult(widthEmu, heightEmu);
            if (!result.Success || result.ModifiedBytes == null)
                return result.ReportText;

            (string? saveErr, string savedPath) = ToolsPptxHelpers.SaveLocalPptx(result.ModifiedBytes, path, null);

            return saveErr != null
                ? $"{result.ReportText}\n\n{saveErr}"
                : $"{result.ReportText}\n\n💾 Saved to: {savedPath}";
        }
    }
}
