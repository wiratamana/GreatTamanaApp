using System;
using Avalonia.Controls;
using Avalonia.Media;

namespace GTA.App;

internal static class UiTheme
{
    // ── Button ────────────────────────────────────────────────────────────────

    /// <summary>Re-skins a Button for every interaction state so it never falls back to the light theme.</summary>
    public static void StyleButton(
        Button   button,
        IBrush   background,
        IBrush   foreground,
        IBrush?  hoverBackground    = null,
        IBrush?  pressedBackground  = null,
        IBrush?  disabledBackground = null,
        IBrush?  hoverForeground    = null,
        IBrush?  pressedForeground  = null,
        IBrush?  disabledForeground = null,
        IBrush?  borderBrush        = null)
    {
        var hoverBg    = hoverBackground    ?? Lighten(background, 0.18);
        var pressedBg  = pressedBackground  ?? Darken(background, 0.18);
        var disabledBg = disabledBackground ?? LlmChatWindow.ColDisabled;
        var disabledFg = disabledForeground ?? LlmChatWindow.ColMuted;
        var border     = borderBrush        ?? Brushes.Transparent;

        // Local values — authoritative for the NORMAL state.
        button.Background = background;
        button.Foreground = foreground;

        // Resource overrides cover every non-normal pseudo-class state.
        var res = button.Resources;
        res["ButtonBackground"]            = background;
        res["ButtonBackgroundPointerOver"] = hoverBg;
        res["ButtonBackgroundPressed"]     = pressedBg;
        res["ButtonBackgroundDisabled"]    = disabledBg;

        res["ButtonForeground"]            = foreground;
        res["ButtonForegroundPointerOver"] = hoverForeground   ?? foreground;
        res["ButtonForegroundPressed"]     = pressedForeground ?? foreground;
        res["ButtonForegroundDisabled"]    = disabledFg;

        res["ButtonBorderBrush"]            = border;
        res["ButtonBorderBrushPointerOver"] = border;
        res["ButtonBorderBrushPressed"]     = border;
        res["ButtonBorderBrushDisabled"]    = border;
    }

    /// <summary>Styles a flat, transparent "link"-style toggle/action button.</summary>
    public static void StyleLinkButton(
        Button  button,
        IBrush  foreground,
        IBrush? hoverBackground   = null,
        IBrush? pressedBackground = null)
    {
        StyleButton(
            button,
            background:         Brushes.Transparent,
            foreground:         foreground,
            hoverBackground:    hoverBackground   ?? LlmChatWindow.BgHoverItem,
            pressedBackground:  pressedBackground ?? LlmChatWindow.BgActiveItem,
            disabledBackground: Brushes.Transparent,
            hoverForeground:    foreground,
            pressedForeground:  foreground,
            disabledForeground: LlmChatWindow.ColMuted);
    }

    // ── ComboBox ──────────────────────────────────────────────────────────────

    /// <summary>Re-skins a ComboBox (box and drop-down popup) so it never falls back to the light theme.</summary>
    public static void StyleComboBox(
        ComboBox combo,
        IBrush   background,
        IBrush   foreground,
        IBrush?  hoverBackground   = null,
        IBrush?  pressedBackground = null,
        IBrush?  borderBrush       = null)
    {
        var hoverBg   = hoverBackground   ?? Lighten(background, 0.18);
        var pressedBg = pressedBackground ?? Darken(background, 0.18);
        var border    = borderBrush ?? LlmChatWindow.ColDivider;

        combo.Background  = background;
        combo.Foreground  = foreground;
        combo.BorderBrush = border;

        var res = combo.Resources;
        res["ComboBoxBackground"]            = background;
        res["ComboBoxBackgroundPointerOver"] = hoverBg;
        res["ComboBoxBackgroundPressed"]     = pressedBg;
        res["ComboBoxBackgroundDisabled"]    = LlmChatWindow.ColDisabled;

        res["ComboBoxForeground"]            = foreground;
        res["ComboBoxForegroundFocused"]     = foreground;
        res["ComboBoxForegroundDisabled"]    = LlmChatWindow.ColMuted;

        res["ComboBoxBorderBrush"]            = border;
        res["ComboBoxBorderBrushPointerOver"] = border;
        res["ComboBoxBorderBrushPressed"]     = border;
        res["ComboBoxBorderBrushDisabled"]    = border;

        res["ComboBoxDropDownBackground"]            = background;
        res["ComboBoxDropDownBackgroundPointerOver"] = hoverBg;
        res["ComboBoxItemBackgroundSelected"]              = pressedBg;
        res["ComboBoxItemBackgroundSelectedPointerOver"]   = hoverBg;
        res["ComboBoxItemForegroundSelected"]              = foreground;
        res["ComboBoxPlaceholderForeground"]               = LlmChatWindow.ColMuted;
    }

    // ── TextBox ───────────────────────────────────────────────────────────────

    /// <summary>Re-skins a TextBox for hover/focus so it never falls back to the light theme.</summary>
    public static void StyleTextBox(
        TextBox textBox,
        IBrush  background,
        IBrush  foreground,
        IBrush? hoverBackground      = null,
        IBrush? focusBackground      = null,
        IBrush? borderBrush          = null,
        IBrush? focusBorderBrush     = null,
        IBrush? watermarkForeground  = null)
    {
        var hoverBg = hoverBackground  ?? Lighten(background, 0.10);
        var focusBg = focusBackground  ?? background;
        var border  = borderBrush      ?? LlmChatWindow.ColDivider;
        var focusBd = focusBorderBrush ?? LlmChatWindow.ColAccent;

        textBox.Background  = background;
        textBox.Foreground  = foreground;
        textBox.BorderBrush = border;
        textBox.CaretBrush  = foreground;

        var res = textBox.Resources;
        res["TextControlBackground"]            = background;
        res["TextControlBackgroundPointerOver"] = hoverBg;
        res["TextControlBackgroundFocused"]     = focusBg;
        res["TextControlBackgroundDisabled"]    = LlmChatWindow.ColDisabled;

        res["TextControlForeground"]            = foreground;
        res["TextControlForegroundPointerOver"] = foreground;
        res["TextControlForegroundFocused"]     = foreground;
        res["TextControlForegroundDisabled"]    = LlmChatWindow.ColMuted;

        res["TextControlBorderBrush"]            = border;
        res["TextControlBorderBrushPointerOver"] = border;
        res["TextControlBorderBrushFocused"]     = focusBd;

        res["TextControlPlaceholderForeground"]         = watermarkForeground ?? LlmChatWindow.ColMuted;
        res["TextControlPlaceholderForegroundPointerOver"] = watermarkForeground ?? LlmChatWindow.ColMuted;
        res["TextControlPlaceholderForegroundFocused"]     = watermarkForeground ?? LlmChatWindow.ColMuted;
    }

    // ── Expander ──────────────────────────────────────────────────────────────

    /// <summary>Re-skins an Expander (header and content area) so it never falls back to the light theme.</summary>
    public static void StyleExpander(
        Expander expander,
        IBrush   headerBackground,
        IBrush   headerForeground,
        IBrush   contentBackground,
        IBrush?  headerHoverBackground   = null,
        IBrush?  headerPressedBackground = null,
        IBrush?  borderBrush             = null)
    {
        var hoverBg   = headerHoverBackground   ?? Lighten(headerBackground, 0.12);
        var pressedBg = headerPressedBackground ?? Darken(headerBackground, 0.12);
        var border    = borderBrush ?? LlmChatWindow.ColDivider;

        expander.Background  = headerBackground;
        expander.Foreground  = headerForeground;
        expander.BorderBrush = border;

        var res = expander.Resources;
        res["ExpanderHeaderBackground"]            = headerBackground;
        res["ExpanderHeaderBackgroundPointerOver"] = hoverBg;
        res["ExpanderHeaderBackgroundPressed"]     = pressedBg;
        res["ExpanderHeaderBackgroundDisabled"]    = headerBackground;

        res["ExpanderHeaderForeground"]            = headerForeground;
        res["ExpanderHeaderForegroundDisabled"]    = LlmChatWindow.ColMuted;

        res["ExpanderHeaderBorderBrush"]            = border;
        res["ExpanderHeaderBorderBrushPointerOver"] = border;
        res["ExpanderHeaderBorderBrushPressed"]     = border;
        res["ExpanderHeaderBorderBrushDisabled"]    = border;

        res["ExpanderContentBackground"] = contentBackground;
        res["ExpanderContentBorderBrush"] = border;
        res["ExpanderDropDownBackground"] = contentBackground;
    }

    // ── CheckBox ──────────────────────────────────────────────────────────────

    /// <summary>Re-skins a CheckBox for every check and interaction state.</summary>
    public static void StyleCheckBox(
        CheckBox checkBox,
        IBrush   foreground,
        IBrush?  accentBrush        = null,
        IBrush?  glyphForeground    = null,
        IBrush?  borderBrush        = null,
        IBrush?  disabledForeground = null)
    {
        var accent      = accentBrush        ?? LlmChatWindow.ColAccent;
        var glyph       = glyphForeground     ?? Brushes.White;
        var border      = borderBrush         ?? LlmChatWindow.ColMuted;
        var hoverBorder = Lighten(border, 0.25);
        var accentHover = Lighten(accent, 0.15);
        var accentPress = Darken(accent, 0.15);
        var disabledFg  = disabledForeground  ?? LlmChatWindow.ColMuted;
        var disabledBg  = LlmChatWindow.ColDisabled;

        // Local value — authoritative for the NORMAL/idle state.
        checkBox.Foreground = foreground;

        var res = checkBox.Resources;

        // ── Label text (every check-state × every pointer-state) ──────────
        foreach (string state in new[] { "Unchecked", "Checked", "Indeterminate" })
        {
            res[$"CheckBoxForeground{state}"]             = foreground;
            res[$"CheckBoxForeground{state}PointerOver"]  = foreground;
            res[$"CheckBoxForeground{state}Pressed"]      = foreground;
            res[$"CheckBoxForeground{state}Disabled"]     = disabledFg;

            res[$"CheckBoxBackground{state}"]              = Brushes.Transparent;
            res[$"CheckBoxBackground{state}PointerOver"]   = Brushes.Transparent;
            res[$"CheckBoxBackground{state}Pressed"]       = Brushes.Transparent;
            res[$"CheckBoxBackground{state}Disabled"]      = Brushes.Transparent;
        }

        // ── The little box itself: border / fill / checkmark glyph ────────
        res["CheckBoxBorderBrushUnchecked"]            = border;
        res["CheckBoxBorderBrushUncheckedPointerOver"] = hoverBorder;
        res["CheckBoxBorderBrushUncheckedPressed"]     = hoverBorder;
        res["CheckBoxBorderBrushUncheckedDisabled"]    = disabledBg;
        res["CheckBoxBorderBrushChecked"]              = accent;
        res["CheckBoxBorderBrushCheckedPointerOver"]   = accentHover;
        res["CheckBoxBorderBrushCheckedPressed"]       = accentPress;
        res["CheckBoxBorderBrushCheckedDisabled"]      = disabledBg;
        res["CheckBoxBorderBrushIndeterminate"]            = accent;
        res["CheckBoxBorderBrushIndeterminatePointerOver"] = accentHover;
        res["CheckBoxBorderBrushIndeterminatePressed"]     = accentPress;
        res["CheckBoxBorderBrushIndeterminateDisabled"]    = disabledBg;

        res["CheckBoxCheckBackgroundStrokeUnchecked"]            = border;
        res["CheckBoxCheckBackgroundStrokeUncheckedPointerOver"] = hoverBorder;
        res["CheckBoxCheckBackgroundStrokeUncheckedPressed"]     = hoverBorder;
        res["CheckBoxCheckBackgroundStrokeUncheckedDisabled"]    = disabledBg;
        res["CheckBoxCheckBackgroundStrokeCheckedPointerOver"]   = accentHover;
        res["CheckBoxCheckBackgroundStrokeCheckedPressed"]       = accentPress;
        res["CheckBoxCheckBackgroundStrokeCheckedDisabled"]      = disabledBg;
        res["CheckBoxCheckBackgroundStrokeIndeterminate"]            = accent;
        res["CheckBoxCheckBackgroundStrokeIndeterminatePointerOver"] = accentHover;
        res["CheckBoxCheckBackgroundStrokeIndeterminatePressed"]     = accentPress;
        res["CheckBoxCheckBackgroundStrokeIndeterminateDisabled"]    = disabledBg;

        res["CheckBoxCheckBackgroundFillUnchecked"]            = Brushes.Transparent;
        res["CheckBoxCheckBackgroundFillUncheckedPointerOver"] = Lighten(LlmChatWindow.BgCodeBlock, 0.15);
        res["CheckBoxCheckBackgroundFillUncheckedPressed"]     = Lighten(LlmChatWindow.BgCodeBlock, 0.25);
        res["CheckBoxCheckBackgroundFillUncheckedDisabled"]    = disabledBg;
        res["CheckBoxCheckBackgroundFillChecked"]              = accent;
        res["CheckBoxCheckBackgroundFillCheckedPointerOver"]   = accentHover;
        res["CheckBoxCheckBackgroundFillCheckedPressed"]       = accentPress;
        res["CheckBoxCheckBackgroundFillCheckedDisabled"]      = disabledBg;
        res["CheckBoxCheckBackgroundFillIndeterminate"]            = accent;
        res["CheckBoxCheckBackgroundFillIndeterminatePointerOver"] = accentHover;
        res["CheckBoxCheckBackgroundFillIndeterminatePressed"]     = accentPress;
        res["CheckBoxCheckBackgroundFillIndeterminateDisabled"]    = disabledBg;

        res["CheckBoxCheckGlyphForegroundUnchecked"]            = Brushes.Transparent;
        res["CheckBoxCheckGlyphForegroundUncheckedPointerOver"] = Brushes.Transparent;
        res["CheckBoxCheckGlyphForegroundUncheckedPressed"]     = Brushes.Transparent;
        res["CheckBoxCheckGlyphForegroundUncheckedDisabled"]    = Brushes.Transparent;
        res["CheckBoxCheckGlyphForegroundChecked"]              = glyph;
        res["CheckBoxCheckGlyphForegroundCheckedPointerOver"]   = glyph;
        res["CheckBoxCheckGlyphForegroundCheckedPressed"]       = glyph;
        res["CheckBoxCheckGlyphForegroundCheckedDisabled"]      = glyph;
        res["CheckBoxCheckGlyphForegroundIndeterminate"]            = glyph;
        res["CheckBoxCheckGlyphForegroundIndeterminatePointerOver"] = glyph;
        res["CheckBoxCheckGlyphForegroundIndeterminatePressed"]     = glyph;
        res["CheckBoxCheckGlyphForegroundIndeterminateDisabled"]    = glyph;
    }

    // ── Colour math ───────────────────────────────────────────────────────────

    /// <summary>Moves the brush colour toward white by amount (0..1).</summary>
    public static SolidColorBrush Lighten(IBrush brush, double amount) =>
        new(LightenColor(ToColor(brush), amount));

    /// <summary>Moves the brush colour toward black by amount (0..1).</summary>
    public static SolidColorBrush Darken(IBrush brush, double amount) =>
        new(DarkenColor(ToColor(brush), amount));

    private static Color ToColor(IBrush brush) =>
        brush is ISolidColorBrush solid ? solid.Color : Colors.Gray;

    private static Color LightenColor(Color c, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        byte r = (byte)Math.Clamp(c.R + (255 - c.R) * amount, 0, 255);
        byte g = (byte)Math.Clamp(c.G + (255 - c.G) * amount, 0, 255);
        byte b = (byte)Math.Clamp(c.B + (255 - c.B) * amount, 0, 255);
        return Color.FromArgb(c.A, r, g, b);
    }

    private static Color DarkenColor(Color c, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        byte r = (byte)Math.Clamp(c.R * (1.0 - amount), 0, 255);
        byte g = (byte)Math.Clamp(c.G * (1.0 - amount), 0, 255);
        byte b = (byte)Math.Clamp(c.B * (1.0 - amount), 0, 255);
        return Color.FromArgb(c.A, r, g, b);
    }
}
