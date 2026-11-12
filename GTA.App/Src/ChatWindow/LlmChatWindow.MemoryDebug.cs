#if !CO_WORKER_USE
using Avalonia.Controls;
using Avalonia.Layout;

namespace GTA.App;

/// <summary>Developer-only "🧠 Memory" button + window wiring. Compiled out in the CO_WORKER_USE build.</summary>
public partial class LlmChatWindow
{
    private MemoryDebugWindow? _memoryDebugWindow;

    private Button BuildMemoryDebugButton()
    {
        var btn = new Button
        {
            Content           = "🧠 Memory",
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Avalonia.Thickness(8, 0, 0, 0),
            Padding           = new Avalonia.Thickness(10, 6, 10, 6),
        };
        UiTheme.StyleButton(btn, BgAiBubble, ColText,
            hoverBackground: BgHoverItem, pressedBackground: BgActiveItem, borderBrush: ColDivider);
        ToolTip.SetTip(btn, "Memory debug: live memory details + heap analysis by type (uses a temporary dump that is deleted again)");
        btn.Click += (_, _) => OpenMemoryDebugWindow();
        return btn;
    }

    private void OpenMemoryDebugWindow()
    {
        if (_memoryDebugWindow != null)
        {
            _memoryDebugWindow.WindowState = WindowState.Normal;
            _memoryDebugWindow.Activate();
            return;
        }

        var w = new MemoryDebugWindow();
        _memoryDebugWindow = w;
        w.Closed += (_, _) => _memoryDebugWindow = null;
        w.Show(this);
    }
}
#endif
