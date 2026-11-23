// From Passenger 8a1df7e4 src/Passenger.App/Settings/Declared.cs, MIT (notices/Passenger/LICENSE): the settings Wayfarer's
// Settings shows; the rest cut.
using Passenger.Core;
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
        new(D.Appearance, "Theme", "colour color theme look palette source repository passenger-themes " + string.Join(" ", ThemeSource.FormerlyCarried.Select(n => n.ToLowerInvariant())))
        {
            When = new(s => !(s.FollowSystemContrast && SystemContrast.InForce.High), $"The system asks for high contrast, so {ThemeSource.HighContrast} shows in its place"),
        });

    public static readonly ToggleSetting FollowSystemContrast = new(s => s.FollowSystemContrast, (s, v) => s with { FollowSystemContrast = v },
        new(D.AccessibilityPage, "Use High Contrast while the system asks for it", "high contrast accessibility increase contrast contrast themes low vision aaa colour color blind windows macos gnome",
            $"While the system asks for high contrast (a Windows contrast theme, macOS's Increase contrast, GNOME's High Contrast), {ThemeSource.HighContrast} "
            + "shows in the chosen theme's and colour scheme's place, dark or light as the system is, and they come back once it no longer asks."));

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

    public static readonly ChoiceSetting<StatePalette> StatePalette = new(s => s.StatePalette, (s, v) => s with { StatePalette = v },
        [(Shell.StatePalette.Theme, "The theme's own"), (Shell.StatePalette.ColourBlindSafe, "Colour-blind safe")],
        new(D.AccessibilityPage, "Colours of states", "colour color blind blindness deuteranopia protanopia tritanopia red green palette diff test passed failed added changed conflict git accessibility",
            "The colours tests' results, changes (in a diff, the gutter and the lists of changed files) and signatures take. Colour-blind safe: passed "
            + "and added in blue, changed and running in orange, failed and conflicts in vermilion, new files in purple, told apart without red and green, "
            + "each as clear on its ground as the theme's own. Each state has a shape, sign or letter of its own as well."));

    public static readonly ChoiceSetting<SoundCuesPlayed> SoundCues = new(s => s.SoundCues, (s, v) => s with { SoundCues = v },
        [(SoundCuesPlayed.WithScreenReader, "While a screen reader runs"), (SoundCuesPlayed.Always, "Always"), (SoundCuesPlayed.Never, "Never")],
        new(D.AccessibilityPage, "Play sound cues", "sound cue audio signal beep tone screen reader voiceover narrator nvda jaws orca accessibility blind",
            "Short sounds for what the caret comes to and for what ends, each turned on below. While a screen reader runs, they play only while the "
            + "system says one does (VoiceOver, Narrator, NVDA, JAWS or Orca), so a person who sees the screen hears none."));

    public static readonly ToggleSetting SoundOnErrorLine = Cue(s => s.SoundOnErrorLine, (s, v) => s with { SoundOnErrorLine = v },
        "An error on the caret's line", "error problem line caret");

    public static readonly ToggleSetting SoundOnWarningLine = Cue(s => s.SoundOnWarningLine, (s, v) => s with { SoundOnWarningLine = v },
        "A warning on the caret's line", "warning problem line caret");

    public static readonly ToggleSetting SoundOnBreakpointLine = Cue(s => s.SoundOnBreakpointLine, (s, v) => s with { SoundOnBreakpointLine = v },
        "A breakpoint on the caret's line", "breakpoint debugger line caret");

    public static readonly ToggleSetting SoundOnFoldedLine = Cue(s => s.SoundOnFoldedLine, (s, v) => s with { SoundOnFoldedLine = v },
        "A folded region at the caret's line", "fold folded folding collapsed region line caret");

    public static readonly ToggleSetting SoundOnChangedLine = Cue(s => s.SoundOnChangedLine, (s, v) => s with { SoundOnChangedLine = v },
        "A line changed since the last commit", "change changed modified added git diff line caret");

    public static readonly ToggleSetting SoundOnBuildEnd = Cue(s => s.SoundOnBuildEnd, (s, v) => s with { SoundOnBuildEnd = v },
        "A build passing or failing", "build passed failed succeeded end finished");

    public static readonly ToggleSetting SoundOnTestsEnd = Cue(s => s.SoundOnTestsEnd, (s, v) => s with { SoundOnTestsEnd = v },
        "A test run passing or failing", "tests test run passed failed end finished");

    public static readonly ToggleSetting SoundOnDebuggerStop = Cue(s => s.SoundOnDebuggerStop, (s, v) => s with { SoundOnDebuggerStop = v },
        "The debugger stopping", "debugger stop stopped breakpoint hit exception step paused");

    public static readonly ToggleSetting SoundOnTerminalFailure = Cue(s => s.SoundOnTerminalFailure, (s, v) => s with { SoundOnTerminalFailure = v },
        "A command in the terminal failing", "terminal shell command failed exit status code error");

    public static readonly ToggleSetting SoundOnSaveRefused = Cue(s => s.SoundOnSaveRefused, (s, v) => s with { SoundOnSaveRefused = v },
        "A save refused", "save saving refused read-only held locked write failed");

    /// <summary>A sound cue's row on the Accessibility page, voided while the cues play never.</summary>
    private static ToggleSetting Cue(Func<AppSettings, bool> get, Func<AppSettings, bool, AppSettings> set, string label, string words) =>
        new(get, set, new(D.AccessibilityPage, label, $"sound cue audio signal {words}")
        {
            When = new(s => s.SoundCues != SoundCuesPlayed.Never, "Play sound cues is Never"),
        });

    public static readonly ToggleSetting ReopenProjectsOnStartup = new(s => s.ReopenProjectsOnStartup, (s, v) => s with { ReopenProjectsOnStartup = v },
        new(D.SystemPage, "Reopen projects on startup", "reopen last project folder startup start launch welcome screen recent"));

    public static readonly ChoiceSetting<ConfirmClosing> ConfirmBeforeClosing = new(s => s.ConfirmBeforeClosing, (s, v) => s with { ConfirmBeforeClosing = v },
        [(ConfirmClosing.Never, "Never"), (ConfirmClosing.WhenQuitting, "When quitting"), (ConfirmClosing.EveryWindow, "Every window")],
        new(D.SystemPage, "Confirm before closing", "confirm ask prompt question before closing close quit exit window are you sure accidental",
            $"Whether {Product.Name} asks you before it quits ({(OperatingSystem.IsMacOS() ? $"Quit {Product.Name}" : "File | Exit")} or closing its last "
            + "window) or before any of its windows closes. The question's Don't ask again sets this back to Never."));

    public static readonly ThemeSetting ColourScheme = new(code: true, s => s.ColourScheme, (s, v) => s with { ColourScheme = v },
        new(D.Appearance, "Colour scheme", "colour color scheme syntax highlighting code colours editor keywords strings comments " + ThemeSource.FormerScheme.ToLowerInvariant(),
            "The colours the code is drawn in: the theme's own, or a code colour scheme's, Dark or Light as the theme is. The editor keeps the theme's background."));

    public static readonly FontSetting EditorFontFamily = new(nameof(AppSettings.EditorFontFamily), s => s.EditorFontFamily, (s, v) => s with { EditorFontFamily = v }, null,
        FontChoices.Code, new(D.EditorPage, "Font family", "font family typeface monospace monospaced code",
            "The font code is shown in: the editor's, the terminal's unless it has its own, and code's in popups and tool windows."));

    public static readonly NumberSetting CaretWidth = new(s => s.CaretWidth, (s, v) => s with { CaretWidth = v }, 1, 8, 1,
        new(D.EditorPage, "Caret width", "caret cursor insertion point text cursor width thickness thick broad bold visibility accessibility terminal",
            "How broad the editor's caret is, and the terminal's Line and Underline cursors."), "px");

    public static readonly ToggleSetting ShowNavigationBar = new(s => s.ShowNavigationBar, (s, v) => s with { ShowNavigationBar = v },
        new(D.EditorPage, "Show navigation bar", "breadcrumbs path package type member outline structure jump appearance",
            "Over the editor, where its caret is: the file's folders and name, then a Java or Kotlin file's type and member, each listing what stands beside it."));
}
