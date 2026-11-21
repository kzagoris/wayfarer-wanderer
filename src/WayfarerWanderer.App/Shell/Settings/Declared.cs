// From Passenger 2044e49b src/Passenger.App/Settings/Declared.cs, MIT (notices/Passenger/LICENSE): the settings Wayfarer's
// Settings shows; the rest cut.
using D = WayfarerWanderer.App.Shell.Settings.SettingsDialog;

namespace WayfarerWanderer.App.Shell.Settings;

/// <summary>Every setting of one value, each declared once: its own row in Settings (none where the dialog builds the row
/// round it), its range or its choices. <see cref="AppSettings.Clamped"/> brings each into what it allows, and the
/// dialog makes each row from here.</summary>
public static class Declared
{
    /// <summary>Every declaration, each adding itself as it is made, so this comes before them.</summary>
    internal static readonly List<Setting> All = [];

    /// <summary>Every declaration, in the order made.</summary>
    public static IReadOnlyList<Setting> Every => All;

    /// <summary>The settings with every value a theme gives filled in as it stands where the person gave none.</summary>
    public static AppSettings InForce(AppSettings settings) => All.Aggregate(settings, (filled, setting) => setting.FilledIn(filled));

    public static readonly ThemeSetting Look = new(code: false, s => s.Look, (s, v) => s with { Look = v },
        new(D.Appearance, "Theme", "colour color theme look palette source repository passenger-themes " + string.Join(" ", ThemeSource.FormerlyCarried.Select(n => n.ToLowerInvariant()))));

    public static readonly ChoiceSetting<ThemeChoice> Theme = new(s => s.Theme, (s, v) => s with { Theme = v },
        [(ThemeChoice.System, "System"), (ThemeChoice.Dark, "Dark"), (ThemeChoice.Light, "Light")],
        new(D.Appearance, "Dark or light", "colour color theme dark light system sync os appearance mode"));

    public static readonly FontSetting WindowFontFamily = new(nameof(AppSettings.WindowFontFamily), s => s.WindowFontFamily, (s, v) => s with { WindowFontFamily = v }, null,
        FontChoices.Window, new(D.Appearance, "Font family", "font family typeface ui interface window words system segoe san francisco inter",
            "The font of the window's words: menus, tool windows, tabs, dialogs and popups. The system font is Segoe UI on Windows, "
            + "San Francisco on macOS and the desktop's sans-serif on Linux. Code keeps the editor's font."));

    public static readonly SizeSetting DisplayScale = new(s => s.DisplayScale, (s, v) => s with { DisplayScale = double.IsFinite(v) ? v : 1 }, [1, 1.25, 1.5, 1.75, 2, 2.5, 3],
        new(D.Appearance, "Display scale", "display scale zoom font size bigger larger interface ui accessibility dpi hidpi 4k monitor percent",
            "How large the window draws everything: menus, tool windows, tabs, dialogs, popups, editors and icons, on top of the "
            + "system's own display scaling. 100% is Wayfarer's own size; 125% or 150% suits a dense screen."));

    public static readonly ToggleSetting MainMenuBar = new(s => s.MainMenuBar, (s, v) => s with { MainMenuBar = v },
        new(D.Appearance, "Show main menu as a menu bar", "main menu bar hamburger file edit view toolbar ribbon menus",
            "File, Edit, View and the rest side by side on the toolbar rather than behind its button. Those the toolbar has no room for go behind the button after them."));

    public static readonly ToggleSetting ReopenProjectsOnStartup = new(s => s.ReopenProjectsOnStartup, (s, v) => s with { ReopenProjectsOnStartup = v },
        new(D.SystemPage, "Reopen projects on startup", "reopen last project folder startup start launch welcome screen recent"));

    public static readonly ThemeSetting ColourScheme = new(code: true, s => s.ColourScheme, (s, v) => s with { ColourScheme = v },
        new(D.Appearance, "Colour scheme", "colour color scheme syntax highlighting code colours editor keywords strings comments " + ThemeSource.FormerScheme.ToLowerInvariant(),
            "The colours the code is drawn in: the theme's own, or a code colour scheme's, Dark or Light as the theme is. The editor keeps the theme's background."));

    public static readonly FontSetting EditorFontFamily = new(nameof(AppSettings.EditorFontFamily), s => s.EditorFontFamily, (s, v) => s with { EditorFontFamily = v }, null,
        FontChoices.Code, new(D.EditorPage, "Font family", "font family typeface monospace monospaced code",
            "The font code is shown in: the editor's, the terminal's unless it has its own, and code's in popups and tool windows."));

    public static readonly NumberSetting CaretWidth = new(s => s.CaretWidth, (s, v) => s with { CaretWidth = v }, 1, 8, 1,
        new(D.EditorPage, "Caret width", "caret cursor insertion point text cursor width thickness thick broad bold visibility accessibility terminal",
            "How broad the editor's caret is, and the terminal's Line and Underline cursors."), "px");
}
