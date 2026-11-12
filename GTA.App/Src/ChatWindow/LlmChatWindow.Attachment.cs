using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using GTA.Core;

namespace GTA.App;

public partial class LlmChatWindow
{
    /// <summary>Add a file path to the pending attachment list and refresh the strip.</summary>
    private void AddAttachment(string filePath)
    {
        if (_pendingAttachments.Contains(filePath)) return;
        _pendingAttachments.Add(filePath);
        RefreshAttachmentStrip();
    }

    /// <summary>Remove a file path from the pending attachment list and refresh the strip.</summary>
    private void RemoveAttachment(string filePath)
    {
        _pendingAttachments.Remove(filePath);
        RefreshAttachmentStrip();
    }

    /// <summary>Rebuild the attachment preview strip from the current _pendingAttachments list.</summary>
    private void RefreshAttachmentStrip()
    {
        _attachmentInnerPanel.Children.Clear();
        foreach (var path in _pendingAttachments)
            _attachmentInnerPanel.Children.Add(BuildAttachmentChip(path));

        if (_attachmentStripBorder != null)
            _attachmentStripBorder.IsVisible = _pendingAttachments.Count > 0;
    }

    /// <summary>Build a small visual chip for one pending attachment</summary>
    private Control BuildAttachmentChip(string filePath)
    {
        var row = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Spacing           = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        bool isImage = AppConfig.IMAGE_EXTS.Contains(Path.GetExtension(filePath));

        if (isImage)
        {
            // Small image thumbnail (fallback to emoji if load fails).
            try
            {
                var bmp = new Bitmap(filePath);
                var thumb = new Image
                {
                    Source            = bmp,
                    Width             = 28,
                    Height            = 28,
                    Stretch           = Stretch.UniformToFill,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ImageContextMenu.Attach(thumb, filePath);
                row.Children.Add(thumb);
            }
            catch
            {
                row.Children.Add(new TextBlock
                {
                    Text              = "🖼",
                    FontSize          = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
        }
        else
        {
            // Text-based (non-image) file
            row.Children.Add(new TextBlock
            {
                Text              = "📄",
                FontSize          = 18,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        row.Children.Add(new TextBlock
        {
            Text              = Path.GetFileName(filePath),
            FontSize          = 11,
            Foreground        = ColText,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth          = 130,
            TextTrimming      = TextTrimming.CharacterEllipsis,
        });

        var removeBtn = new Button
        {
            Content           = "×",
            FontSize          = 14,
            Padding           = new Thickness(4, 0, 4, 0),
            BorderThickness   = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        UiTheme.StyleLinkButton(removeBtn, ColMuted);
        string capturedPath = filePath;
        removeBtn.Click += (_, _) => RemoveAttachment(capturedPath);
        row.Children.Add(removeBtn);

        return new Border
        {
            Background   = BgAiBubble,
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(8, 4, 8, 4),
            Margin       = new Thickness(0, 2, 0, 2),
            Child        = row,
        };
    }

    /// <summary>Open the system file picker to select one or more image and/or text-based files.</summary>
    private async Task OpenFilePickerAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        var picked = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Attach File",
            AllowMultiple  = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp" }
                },
                new FilePickerFileType("Text files")
                {
                    Patterns = new[]
                    {
                        "*.txt", "*.md", "*.log", "*.json", "*.xml", "*.csv",
                        "*.cs", "*.py", "*.js", "*.ts", "*.html", "*.css",
                        "*.yaml", "*.yml", "*.ini", "*.cfg",
                    }
                },
                new FilePickerFileType("All files")
                {
                    Patterns = new[] { "*.*" }
                },
            }
        });

        foreach (var file in picked)
        {
            string localPath = file.Path.LocalPath;
            AddAttachment(localPath);
        }
    }

    // ── Drag-and-drop (image or text file dropped onto the input area) ──────

    /// <summary>Called continuously while a drag operation hovers over the input area.</summary>
    private void OnInputAreaDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    /// <summary>Called when the user drops one or more files onto the input area (e.g. from Windows Explorer).</summary>
    private void OnInputAreaDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;

        var items = e.Data.GetFiles();
        if (items == null) return;

        foreach (var item in items)
        {
            string? localPath = item.TryGetLocalPath();
            if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
                AddAttachment(localPath);
        }

        e.Handled = true;
    }

    // Clipboard image paste (Ctrl+V)

    /// <summary>Invoked when the user presses Ctrl+V while the prompt textbox is focused.</summary>
    private async Task HandleClipboardPasteAsync()
    {
        IClipboard? clipboard = null;
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            clipboard = topLevel?.Clipboard;
            if (clipboard == null) return;

            string? imagePath = await ClipboardImageHelper.TryExtractClipboardImageAsync(
                clipboard, CurrentPastedImagesDir);
            if (imagePath != null)
            {
                AddAttachment(imagePath);
                AppendLog($"🖼  Pasted clipboard image attached → {Path.GetFileName(imagePath)}");
            }
            else
            {
                // No image on the clipboard — fall back to a normal text paste.
                await PasteTextFallbackAsync(clipboard);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"✗  Clipboard paste failed: {ex.Message}");
            // Still try a plain text paste so the keystroke isn't a dead end.
            try
            {
                if (clipboard != null) await PasteTextFallbackAsync(clipboard);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>Manual text paste into the prompt box.</summary>
    private async Task PasteTextFallbackAsync(IClipboard clipboard)
    {
        string? text = await clipboard.GetTextAsync();
        if (string.IsNullOrEmpty(text)) return;

        string current = _inputBox.Text ?? string.Empty;
        int start = Math.Clamp(Math.Min(_inputBox.SelectionStart, _inputBox.SelectionEnd), 0, current.Length);
        int end   = Math.Clamp(Math.Max(_inputBox.SelectionStart, _inputBox.SelectionEnd), 0, current.Length);

        _inputBox.Text = current[..start] + text + current[end..];
        _inputBox.CaretIndex = start + text.Length;
    }
}
