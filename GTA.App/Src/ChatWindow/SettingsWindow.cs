#if CO_WORKER_USE
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GTA.Core;

namespace GTA.App;

/// <summary>
/// CO_WORKER_USE only. Settings window with two parts:
///  1. The three account credentials. Passwords use a masked TextBox (PasswordChar; Avalonia has no PasswordBox
///     control) and are never pre-filled. Values are saved, encrypted, to Credentials\my_credentials.dat
///     through <see cref="SecureCredentialStore"/>.
///  2. An additional system prompt (free text, empty by default). It is added AFTER the built-in system prompt.
///     Saved to Credentials\additional_system_prompt.txt through <see cref="AdditionalPromptStore"/>.
/// One Save button saves both parts.
/// </summary>
public sealed class SettingsWindow : Window
{
    private sealed class CredentialRow
    {
        public required string Title   { get; init; }
        public required string Hint    { get; init; }
        public required string IdKey   { get; init; }
        public required string PwKey   { get; init; }
        public TextBox IdBox { get; set; } = null!;
        public TextBox PwBox { get; set; } = null!;
        public bool HadPassword { get; set; }
    }

    private static readonly SolidColorBrush ColError = new(Color.FromRgb(230, 120, 120));

    private readonly bool _storeOk;
    private readonly List<CredentialRow> _rows;
    private readonly TextBlock _messageLabel;
    private readonly Button _saveBtn;

    // Additional system prompt editor
    private readonly TextBox _promptBox;
    private readonly TextBlock _promptStatus;

    public SettingsWindow()
    {

        Title                 = "⚙ Settings";
        Width                 = 680;
        Height                = 780;
        MinWidth              = 520;
        MinHeight             = 480;
        Background            = LlmChatWindow.BgMain;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize             = true;

        _rows = new List<CredentialRow>
        {
            new() { Title = "Jira API token",  Hint = "Atlassian account e-mail + API token",
                    IdKey = "cred_jiraapitoken_id",   PwKey = "cred_jiraapitoken_password" },
            new() { Title = "Primary account", Hint = "Proxy / SSO login",
                    IdKey = "cred_primaryaccount_id", PwKey = "cred_primaryaccount_password" },
            new() { Title = "Mail account",    Hint = "Cloud (mail) login",
                    IdKey = "cred_mailaccount_id",    PwKey = "cred_mailaccount_password" },
        };

        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 12), Spacing = 6 };

        root.Children.Add(new TextBlock
        {
            Text       = "Settings",
            FontSize   = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColText,
        });

        root.Children.Add(new TextBlock
        {
            Text = "Credentials are optional. If you leave one empty, the matching service cannot be used " +
                   "(the AI will tell you the credential is not set). You can fill it in later. " +
                   "Saved encrypted in Credentials\\my_credentials.dat. " +
                   "Leave a password field empty to keep the saved password.",
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 6),
        });

        _storeOk = SecureCredentialStore.Status == SecureStoreStatus.Ok;
        bool storeOk = _storeOk;
        _messageLabel = new TextBlock
        {
            FontSize     = 11,
            Foreground   = ColError,
            TextWrapping = TextWrapping.Wrap,
            IsVisible    = !storeOk,
            Text         = storeOk ? "" : SecureCredentialStore.StatusMessage +
                           "  Credentials cannot be saved, so the file is not overwritten. " +
                           "Restore Credentials\\my_credentials.dat from your original release and restart the app.",
        };
        _messageLabel.Margin = new Thickness(20, 8, 20, 0);   // shown in the fixed footer, not in the scroll area

        foreach (var row in _rows)
        {
            string savedPw = SecureCredentialStore.Get(row.PwKey);
            row.HadPassword = !string.IsNullOrEmpty(savedPw);

            root.Children.Add(new TextBlock
            {
                Text       = row.Title,
                FontSize   = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = LlmChatWindow.ColText,
                Margin     = new Thickness(0, 8, 0, 0),
            });
            root.Children.Add(new TextBlock
            {
                Text       = row.Hint,
                FontSize   = 10,
                Foreground = LlmChatWindow.ColMuted,
            });

            row.IdBox = new TextBox
            {
                Text      = SecureCredentialStore.Get(row.IdKey),
                Watermark = "ID / e-mail",
                FontSize  = 13,
                Padding   = new Thickness(10, 7, 10, 7),
                IsEnabled = storeOk,
            };
            UiTheme.StyleTextBox(row.IdBox, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
                borderBrush: LlmChatWindow.ColDivider, focusBorderBrush: LlmChatWindow.ColAccent);

            row.PwBox = new TextBox
            {
                PasswordChar   = '●',
                RevealPassword = false,
                Watermark      = row.HadPassword ? "Saved - leave empty to keep" : "Password / token",
                FontSize       = 13,
                Padding        = new Thickness(10, 7, 10, 7),
                IsEnabled      = storeOk,
            };
            UiTheme.StyleTextBox(row.PwBox, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
                borderBrush: LlmChatWindow.ColDivider, focusBorderBrush: LlmChatWindow.ColAccent);

            root.Children.Add(row.IdBox);
            root.Children.Add(row.PwBox);
        }

        // ── Additional system prompt section ──────────────────────────────────
        root.Children.Add(new Border
        {
            Height     = 1,
            Background = LlmChatWindow.ColDivider,
            Margin     = new Thickness(0, 18, 0, 10),
        });

        root.Children.Add(new TextBlock
        {
            Text       = "Additional system prompt",
            FontSize   = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColText,
        });

        root.Children.Add(new TextBlock
        {
            Text = "Your own standing instructions for the AI. This text is added AFTER the built-in system prompt " +
                   "(it does not replace it) and is sent with every message. " +
                   "Example: \"Always answer in Japanese.\" Leave empty to use only the built-in prompt. " +
                   "A bad instruction can make the AI worse or unsafe. If something goes wrong, clear this box and save. " +
                   "The change is used for the next message you send.",
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 6),
        });

        string loadError = AdditionalPromptStore.LoadError;
        if (loadError.Length > 0)
        {
            root.Children.Add(new TextBlock
            {
                Text         = loadError,
                FontSize     = 11,
                Foreground   = ColError,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        _promptBox = new TextBox
        {
            Text                = AdditionalPromptStore.GetAdditional(),
            Watermark           = "(empty)",
            AcceptsReturn       = true,
            AcceptsTab          = false,
            TextWrapping        = TextWrapping.Wrap,
            MaxLength           = AdditionalPromptStore.MaxLength,
            FontSize            = 12,
            FontFamily          = new FontFamily("Consolas, Cascadia Mono, Courier New, monospace"),
            Height              = 200,
            Padding             = new Thickness(10, 8, 10, 8),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_promptBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        UiTheme.StyleTextBox(_promptBox, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
            borderBrush: LlmChatWindow.ColDivider, focusBorderBrush: LlmChatWindow.ColAccent);
        root.Children.Add(_promptBox);

        _promptStatus = new TextBlock
        {
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
        };
        root.Children.Add(_promptStatus);

        _promptBox.TextChanged += (_, _) => UpdatePromptStatus();
        UpdatePromptStatus();

        // ── Footer (always visible, outside the scroll area) ──────────────────
        var cancelBtn = new Button { Content = "Cancel", FontSize = 12, Padding = new Thickness(16, 7, 16, 7) };
        UiTheme.StyleButton(cancelBtn, LlmChatWindow.ColDisabled, LlmChatWindow.ColText);
        cancelBtn.Click += (_, _) => Close(false);

        _saveBtn = new Button
        {
            Content    = "💾  Save",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(18, 7, 18, 7),
        };
        UiTheme.StyleButton(_saveBtn, LlmChatWindow.ColAccent, Brushes.White);
        _saveBtn.Click += (_, _) => TrySave();

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Margin              = new Thickness(20, 10, 20, 14),
        };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(_saveBtn);

        // Fixed footer: message (errors are always visible, even when the form is scrolled) + buttons.
        var footer = new StackPanel();
        footer.Children.Add(_messageLabel);
        footer.Children.Add(buttons);

        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var scroll = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 0);
        Grid.SetRow(footer, 1);
        layout.Children.Add(scroll);
        layout.Children.Add(footer);

        Content = layout;

        // Never leave typed secrets sitting in the controls after the window is gone.
        Closed += (_, _) => ClearPasswordBoxes();
    }

    // ── Additional prompt ─────────────────────────────────────────────────────

    private void UpdatePromptStatus()
    {
        int len = AdditionalPromptStore.Normalize(_promptBox.Text).Trim().Length;
        _promptStatus.Text = len == 0
            ? "Empty: only the built-in prompt is used."
            : $"{len:N0} / {AdditionalPromptStore.MaxLength:N0} characters. Added after the built-in prompt when you press Save.";
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    private void TrySave()
    {
        var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Credentials are optional: any field may stay empty. An empty ID is saved as empty.
        // An empty password field means "keep the saved password".
        if (_storeOk)
        {
            foreach (var row in _rows)
            {
                string id = row.IdBox.Text?.Trim() ?? "";
                string pw = row.PwBox.Text ?? "";

                updates[row.IdKey] = id;
                if (pw.Length > 0)
                    updates[row.PwKey] = pw;
            }
        }

        string additional    = AdditionalPromptStore.Normalize(_promptBox.Text).Trim();
        bool   promptChanged = additional != AdditionalPromptStore.GetAdditional();

        if (additional.Length > AdditionalPromptStore.MaxLength)
        {
            ShowError($"The additional prompt is too long ({additional.Length:N0} characters). Maximum is {AdditionalPromptStore.MaxLength:N0}.");
            return;
        }

        if (_storeOk)
        {
            try
            {
                SecureCredentialStore.Save(updates);
            }
            catch (Exception ex)
            {
                SessionFileLogger.LogException("Settings: could not save credentials", ex);
                ShowError("Could not save credentials: " + ex.Message);
                return;
            }
            SessionFileLogger.Log("Settings: credentials saved (encrypted).");
        }

        if (promptChanged)
        {
            try
            {
                AdditionalPromptStore.Save(additional);
            }
            catch (Exception ex)
            {
                SessionFileLogger.LogException("Settings: could not save additional prompt", ex);
                ShowError((_storeOk ? "Credentials were saved, but the additional prompt could not be saved: "
                                    : "Could not save the additional prompt: ") + ex.Message);
                return;
            }
            SessionFileLogger.Log(additional.Length == 0
                ? "Settings: additional prompt removed."
                : $"Settings: additional prompt saved ({additional.Length} characters).");
        }

        Close(true);
    }

    private void ShowError(string message)
    {
        _messageLabel.Text      = message;
        _messageLabel.IsVisible = true;
    }

    private void ClearPasswordBoxes()
    {
        foreach (var row in _rows)
            row.PwBox.Text = string.Empty;
    }
}
#endif
