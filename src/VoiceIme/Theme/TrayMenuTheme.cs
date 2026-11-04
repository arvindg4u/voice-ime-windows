using System.Drawing;
using System.Windows.Forms;
using Color = System.Drawing.Color;

namespace VoiceIme.Theme;

/// <summary>
/// Applies the app palette to the WinForms tray menu.
///
/// <para>The tray menu is a <see cref="ContextMenuStrip"/> — WinForms, not WPF —
/// so it does not participate in the resource-dictionary theme swap at all. Left
/// alone it renders the system default light chrome, which in dark mode is a
/// bright rectangle popping out of an otherwise dark app. This is the same class
/// of gap as the un-templated WPF <c>ComboBox</c> it sits next to.</para>
///
/// <para>WinForms has no design-token system, so the palette is mirrored here
/// explicitly. Keep the values in step with <see cref="Palette"/>.</para>
/// </summary>
internal static class TrayMenuTheme
{
    /// <summary>
    /// Applies the current theme's renderer to a menu. Safe to call repeatedly;
    /// the renderer is cheap and the menu is rebuilt on every state change.
    /// </summary>
    public static void Apply(ContextMenuStrip menu)
    {
        if (menu is null)
        {
            return;
        }

        menu.Renderer = new ToolStripProfessionalRenderer(new PaletteColorTable());
        menu.ForeColor = Ink();
        menu.BackColor = Surface();
    }

    private static bool Dark => ThemeManager.CurrentTheme == HandyTheme.Dark;

    // Mirrors Palette.Light / Palette.Dark. WinForms menus cannot resolve WPF
    // brushes, so these are the literals.
    private static Color Surface() => Dark
        ? Color.FromArgb(0x23, 0x22, 0x21)   // SurfaceCard
        : Color.FromArgb(0xFF, 0xFF, 0xFF);

    private static Color Ink() => Dark
        ? Color.FromArgb(0xF2, 0xF1, 0xEF)   // TextPrimary
        : Color.FromArgb(0x1A, 0x1A, 0x1A);

    private static Color Hover() => Dark
        ? Color.FromArgb(0x2E, 0x2D, 0x2B)   // SurfaceHover
        : Color.FromArgb(0xF1, 0xF1, 0xF3);

    private static Color Border() => Dark
        ? Color.FromArgb(0x35, 0x33, 0x2F)   // BorderSubtle
        : Color.FromArgb(0xE6, 0xE6, 0xE8);

    private static Color DisabledInk() => Dark
        ? Color.FromArgb(0x80, 0x7E, 0x7A)   // TextDisabled
        : Color.FromArgb(0x86, 0x86, 0x88);

    /// <summary>
    /// Professional-renderer colour table wired to the app palette. Overriding
    /// the few entries the menu actually paints is enough — the defaults for
    /// everything else are never reached with a flat menu like this one.
    /// </summary>
    private sealed class PaletteColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface();
        public override Color MenuItemSelected => Hover();
        public override Color MenuItemSelectedGradientBegin => Hover();
        public override Color MenuItemSelectedGradientEnd => Hover();
        public override Color MenuItemBorder => Hover();
        public override Color MenuBorder => Border();
        public override Color ImageMarginGradientBegin => Surface();
        public override Color ImageMarginGradientMiddle => Surface();
        public override Color ImageMarginGradientEnd => Surface();
        public override Color SeparatorDark => Border();
        public override Color SeparatorLight => Border();
    }
}
