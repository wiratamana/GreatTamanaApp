using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GTA.Tools;
using GTA.Core;

namespace GTA.App;

public partial class LlmChatWindow
{

    internal static Control BuildToolResultContent(string result)
    {
        if (string.IsNullOrEmpty(result))
            return MakeResultTextBlock("(empty result)");

        var parts = ExtractResultControls(result);

        if (parts.Count == 0)
        {
            return MakeResultTextBlock(JsonUtils.TryPrettyPrintJson(result));
        }

        if (parts.Count == 1)
            return parts[0];

        var panel = new StackPanel { Spacing = 8 };
        foreach (var c in parts)
            panel.Children.Add(c);
        return panel;
    }

    private static List<Control> ExtractResultControls(string result)
    {
        var controls = new List<Control>();

        string trimmed = result.TrimStart();
        bool looksLikeJson = trimmed.StartsWith("[") || trimmed.StartsWith("{");

        if (!looksLikeJson && ImageEvidenceStore.ContainsMarker(result))
        {
            controls.Add(RenderPossibleMarkerText(result));
            return controls;
        }

        try
        {
            if (looksLikeJson)
            {
                var node = JsonNode.Parse(result);

                JsonArray? blocks = node switch
                {
                    JsonArray  arr => arr,
                    JsonObject obj => new JsonArray { obj.DeepClone() },
                    _ => null,
                };

                if (blocks != null)
                {
                    bool hasStructuredBlocks = false;

                    foreach (var item in blocks)
                    {
                        string? type = item?["type"]?.GetValue<string>();

                        if (type == "image")
                        {
                            hasStructuredBlocks = true;
                            var source = item!["source"];
                            if (source?["type"]?.GetValue<string>() == "base64")
                            {
                                string? data = source["data"]?.GetValue<string>();
                                if (!string.IsNullOrEmpty(data))
                                    controls.Add(TryBuildImageControl(data));
                            }
                        }
                        else if (type == "text")
                        {
                            hasStructuredBlocks = true;
                            string? text = item!["text"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(text))
                                controls.Add(RenderPossibleMarkerText(text));
                        }
                    }

                    if (hasStructuredBlocks && controls.Count > 0)
                        return controls;

                    controls.Clear();
                }
            }
        }
        catch { /* not valid JSON — fall through */ }

        if (ImageUtils.IsLikelyBase64(result))
        {
            try
            {
                string clean = result.Replace("\n", "").Replace("\r", "").Replace(" ", "");
                byte[] bytes = Convert.FromBase64String(clean);
                if (ImageUtils.DetectImageMagic(bytes) != null)
                {
                    controls.Add(TryBuildImageControl(clean));
                    return controls;
                }
            }
            catch { /* not decodeable or not an image */ }
        }

        return controls; // empty → caller renders as plain text
    }

    private static Control RenderPossibleMarkerText(string text)
    {
        if (!ImageEvidenceStore.ContainsMarker(text))
            return MakeResultTextBlock(text);

        var refs = new List<ImageEvidenceStore.SavedImageRef>(ImageEvidenceStore.FindAll(text));
        if (refs.Count == 0)
            return MakeResultTextBlock(text);

        var panel = new StackPanel { Spacing = 6 };
        foreach (var r in refs)
            panel.Children.Add(BuildSavedImageControl(r.Path, r.MediaType, r.SizeText));
        return panel;
    }

    private static Control BuildSavedImageControl(string path, string mediaType, string sizeText)
    {
        try
        {
            if (File.Exists(path))
            {
                var bmp = new Bitmap(path);
                var img = new Image
                {
                    Source              = bmp,
                    MaxWidth            = 620,
                    MaxHeight           = 440,
                    Stretch             = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin              = new Thickness(0, 4, 0, 2),
                };

                var caption = new SelectableTextBlock
                {
                    Text         = $"📷 {Path.GetFileName(path)}"
                                    + (string.IsNullOrEmpty(sizeText) ? "" : $"  ({sizeText})")
                                    + $"\n{path}",
                    FontSize     = 10,
                    Foreground   = ColMuted,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity      = 0.75,
                };

                var stack = new StackPanel { Spacing = 2 };
                stack.Children.Add(img);
                stack.Children.Add(caption);
                return stack;
            }
        }
        catch { /* fall through to text fallback below */ }

        return MakeResultTextBlock($"[image saved at: {path} — file not found or could not be loaded]");
    }

    private static Control TryBuildImageControl(string base64Data)
    {
        try
        {
            string clean = base64Data.Replace("\n", "").Replace("\r", "").Replace(" ", "");
            byte[] bytes = Convert.FromBase64String(clean);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);

            return new Image
            {
                Source              = bmp,
                MaxWidth            = 620,
                MaxHeight           = 440,
                Stretch             = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin              = new Thickness(0, 4, 0, 4),
            };
        }
        catch
        {
            return MakeResultTextBlock("[image — could not decode base64 data]");
        }
    }

    private static Control MakeResultTextBlock(string text) =>
        BuildTruncatableTextBlock(text, ColMuted, 0.9);
    private sealed class LazyToolResultHost
    {
        private readonly StackPanel _panel;
        private string? _rawResult;
        private bool _built;

        public LazyToolResultHost(StackPanel panel) => _panel = panel;

        public void SetResult(string rawResult, bool isExpanded)
        {
            _rawResult = rawResult;
            _built     = false;
            if (isExpanded)
                Build();
            else
                _panel.Children.Clear();
        }

        public void OnExpand()
        {
            if (!_built && _rawResult != null)
                Build();
        }

        public void OnCollapse()
        {
            if (!_built)
                return;
            DisposeImages(_panel);
            _panel.Children.Clear();
            _built = false;
        }

        private void Build()
        {
            DisposeImages(_panel);
            _panel.Children.Clear();
            _panel.Children.Add(BuildToolResultContent(_rawResult ?? string.Empty));
            _built = true;
        }

        private static void DisposeImages(Control root)
        {
            switch (root)
            {
                case Image img:
                    (img.Source as IDisposable)?.Dispose();
                    img.Source = null;
                    break;
                case Panel p:
                    foreach (var child in p.Children)
                        if (child is Control c) DisposeImages(c);
                    break;
                case Border { Child: Control bc }:
                    DisposeImages(bc);
                    break;
                case ContentControl { Content: Control cc }:
                    DisposeImages(cc);
                    break;
            }
        }
    }
}
