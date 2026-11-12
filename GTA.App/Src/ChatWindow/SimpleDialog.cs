using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GTA.App;

/// <summary>
/// Tiny dark-themed modal dialog: a text prompt (with validation) or a yes/no confirmation.
/// Usage:  string? name = await SimpleDialog.PromptTextAsync(owner, ...);   bool yes = await SimpleDialog.ConfirmAsync(owner, ...);
/// Enter = OK, Esc = Cancel.
/// </summary>
internal sealed class SimpleDialog : Window
{
    private static readonly SolidColorBrush ColDanger = new(Color.FromRgb(224, 96, 96));

    private readonly TextBox?                _input;
    private readonly TextBlock               _error;
    private readonly Func<string, string?>?  _validate;

    private bool    _accepted;
    private string? _text;

    private SimpleDialog(
        string title, string message, string? initialText, bool withInput,
        string okLabel, bool danger, Func<string, string?>? validate)
    {
        _validate = validate;

        Title                 = title;
        Width                 = 440;
        SizeToContent         = SizeToContent.Height;
        CanResize             = false;
        ShowInTaskbar         = false;
        Background            = LlmChatWindow.BgMain;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var stack = new StackPanel { Margin = new Thickness(22, 20, 22, 18), Spacing = 10 };

        stack.Children.Add(new TextBlock
        {
            Text       = title,
            FontSize   = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColText,
        });

        stack.Children.Add(new TextBlock
        {
            Text         = message,
            FontSize     = 12,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
        });

        _error = new TextBlock
        {
            FontSize     = 11,
            Foreground   = ColDanger,
            TextWrapping = TextWrapping.Wrap,
            IsVisible    = false,
        };

        if (withInput)
        {
            _input = new TextBox
            {
                Text      = initialText ?? "",
                MaxLength = 40,
                FontSize  = 13,
                Padding   = new Thickness(10, 8, 10, 8),
                Margin    = new Thickness(0, 4, 0, 0),
            };
            UiTheme.StyleTextBox(_input, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
                borderBrush: LlmChatWindow.ColDivider, focusBorderBrush: LlmChatWindow.ColAccent);
            _input.TextChanged += (_, _) => _error.IsVisible = false;
            stack.Children.Add(_input);
        }

        stack.Children.Add(_error);

        var cancelBtn = new Button { Content = "Cancel", FontSize = 12, Padding = new Thickness(16, 6, 16, 6) };
        UiTheme.StyleButton(cancelBtn, LlmChatWindow.ColDisabled, LlmChatWindow.ColText);
        cancelBtn.Click += (_, _) => Close();

        var okBtn = new Button { Content = okLabel, FontSize = 12, Padding = new Thickness(18, 6, 18, 6) };
        UiTheme.StyleButton(okBtn, danger ? ColDanger : LlmChatWindow.ColAccent, Brushes.White);
        okBtn.Click += (_, _) => Accept();

        stack.Children.Add(new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Margin              = new Thickness(0, 8, 0, 0),
            Children            = { cancelBtn, okBtn },
        });

        Content = stack;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)     { e.Handled = true; Close(); }
            else if (e.Key == Key.Enter) { e.Handled = true; Accept(); }
        };

        Opened += (_, _) =>
        {
            if (_input == null) return;
            _input.Focus();
            _input.SelectAll();
        };
    }

    private void Accept()
    {
        if (_input != null)
        {
            string text  = (_input.Text ?? "").Trim();
            string? problem = _validate?.Invoke(text);
            if (problem != null)
            {
                _error.Text      = problem;
                _error.IsVisible = true;
                _input.Focus();
                return;
            }
            _text = text;
        }

        _accepted = true;
        Close();
    }

    private Task ShowAsync(Window? owner)
    {
        if (owner != null) return ShowDialog(owner);

        var tcs = new TaskCompletionSource();
        Closed += (_, _) => tcs.TrySetResult();
        Show();
        return tcs.Task;
    }

    /// <summary>Asks for one line of text. Returns the trimmed text, or null when cancelled. validate returns an error text or null.</summary>
    public static async Task<string?> PromptTextAsync(
        Window? owner, string title, string message, string initialText,
        string okLabel = "OK", Func<string, string?>? validate = null)
    {
        var dlg = new SimpleDialog(title, message, initialText, withInput: true, okLabel, danger: false, validate);
        await dlg.ShowAsync(owner);
        return dlg._accepted ? dlg._text : null;
    }

    /// <summary>Yes/no question. Returns true only when the user pressed the OK button.</summary>
    public static async Task<bool> ConfirmAsync(
        Window? owner, string title, string message, string okLabel = "OK", bool danger = false)
    {
        var dlg = new SimpleDialog(title, message, null, withInput: false, okLabel, danger, validate: null);
        await dlg.ShowAsync(owner);
        return dlg._accepted;
    }
}
