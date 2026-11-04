using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GTA.App;

public partial class LlmChatWindow
{
    private const int TextTruncateLimit = 4000;

    /// <summary>Selectable text block for tool-call text; long text is truncated with a "Show All" button.</summary>
    private static Control BuildTruncatableTextBlock(string text, IBrush foreground, double opacity = 0.9)
    {
        text ??= "";

        bool needsTruncation = text.Length > TextTruncateLimit;

        var textBlock = new SelectableTextBlock
        {
            Text         = needsTruncation ? text.Substring(0, TextTruncateLimit) + " …" : text,
            TextWrapping = TextWrapping.Wrap,
            Foreground   = foreground,
            FontSize     = 12,
            FontFamily   = new FontFamily("Consolas, Courier New, monospace"),
            LineHeight   = 18,
            Opacity      = opacity,
        };

        if (!needsTruncation)
            return textBlock;

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(textBlock);

        int hiddenChars = text.Length - TextTruncateLimit;
        var showAllBtn = new Button
        {
            Content             = $"---- [ Show All ] ({hiddenChars:N0} more characters) ----",
            FontSize            = 11,
            BorderThickness     = new Thickness(0),
            Padding             = new Thickness(2, 2, 2, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor              = new Cursor(StandardCursorType.Hand),
        };
        UiTheme.StyleLinkButton(showAllBtn, ColMuted);

        showAllBtn.Click += (_, _) =>
        {
            textBlock.Text = text;
            panel.Children.Remove(showAllBtn);
        };

        panel.Children.Add(showAllBtn);
        return panel;
    }
}
