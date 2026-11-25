// From Passenger 8a1df7e4 src/Passenger.App/Settings/Declared.cs, MIT (notices/Passenger/LICENSE): the settings Wayfarer's
// Settings shows, its Code Folding page XML's and JSON's two; the rest cut.
using Passenger.Core;
using Passenger.Vcs;
using D =WayfarerWanderer.App.Shell.Settings.SettingsDialog;

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

    public static readonly ToggleSetting CheckForUpdates = new(s => s.CheckForUpdates, (s, v) => s with { CheckForUpdates = v },
        new(D.SystemPage, "Check for updates automatically", "release update download restart"));

    public static readonly ToggleSetting MainMenuBar = new(s => s.MainMenuBar, (s, v) => s with { MainMenuBar = v },
        new(D.Appearance, "Show main menu as a menu bar", "main menu bar hamburger file edit view toolbar ribbon menus",
            "File, Edit, View and the rest side by side on the toolbar rather than behind its button. Those the toolbar has no room for go behind the button after them."));

    public static readonly ThemedChoiceSetting<EditorTabsPlace> EditorTabs = new(s => s.EditorTabs, s => s.TabsPlace(), (s, v) => s with { EditorTabs = v },
        [(EditorTabsPlace.Top, "Top"), (EditorTabsPlace.Left, "Left"), (EditorTabsPlace.Right, "Right")],
        new(D.Appearance, "Editor tabs", "editor tabs tab well strip placement position vertical side left right top layout arrangement",
            "Where the editor's tabs stand: a row across its top, or a column down its left or right side, which fits more of them."));

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
        new(D.EditorPage, "Show navigation bar", "breadcrumbs path package namespace type member outline structure jump appearance",
            "Over the editor, where its caret is: a Java or Kotlin file's package, type and member, each listing what stands beside it."));

    public static readonly ThemedChoiceSetting<ToolWindowLook> ToolWindowLook = new(s => s.ToolWindowLook, s => s.ToolWindowsAs(), (s, v) => s with { ToolWindowLook = v },
        [(Shell.ToolWindowLook.StripeIcons, "Stripe icons"), (Shell.ToolWindowLook.TitledTabs, "Titled tabs")],
        new(D.Appearance, "Tool windows", "tool windows docked tabs titles names stripe icons wells side bottom panels layout arrangement",
            "How tool windows are named and gone between: by their icons on the stripes down the window's sides, or by tabs with "
            + "their names, no stripe standing. A drag on an icon, a tab or a header moves the window."));

    public static readonly ThemedChoiceSetting<ClosedToolWindows> ClosedToolWindows = new(s => s.ClosedToolWindows, s => s.ClosedWindows(),
        (s, v) => s with { ClosedToolWindows = v },
        [(Shell.ClosedToolWindows.KeepIcons, "Keep their icons or tabs"), (Shell.ClosedToolWindows.LeaveStripe, "Leave until opened again")],
        new(D.Appearance, "Closed tool windows", "tool windows close closed hide remove declutter stripe icons wells view menu reopen layout arrangement",
            "Whether a tool window closed by its × keeps its icon on the stripe (or its tab, under titled tabs), or leaves until it is "
            + "opened again from View | Tool Windows, Find Action or its keys. Hide always keeps it."));

    public static readonly ThemedChoiceSetting<FindResults> FindResults = new(s => s.FindResults, s => s.FindResultsIn(), (s, v) => s with { FindResults = v },
        [(Shell.FindResults.Popup, "In its popup"), (Shell.FindResults.FindWindow, "Kept in the Find window")],
        new(D.Appearance, "Find in Files results", "find in files search results matches occurrences find window docked keep kept popup tool window layout arrangement",
            "Where Find in Files' matches stay once one is gone to: in its popup, which closes, or kept in the Find window as well, "
            + "a tab of their own to go back to."));

    public static readonly NumberSetting EditorFontSize = new(s => s.CodeSize(), (s, v) => s with { EditorFontSize = v },
        AppSettings.MinFontSize, AppSettings.MaxFontSize, 1, new(D.EditorPage, "Font size", "font size text zoom",
            "The code's size in points. Until you set one, the theme's size, else 13; View | Active Editor | Reset Font Size goes back to it."),
        given: s => s.EditorFontSize is not null);

    public static readonly NumberSetting EditorLineSpacing = new(s => s.EditorLineSpacing, (s, v) => s with { EditorLineSpacing = v }, 0.8, 3, 0.1,
        new(D.EditorPage, "Line spacing", "font line height"));

    public static readonly ToggleSetting Ligatures = new(s => s.Ligatures, (s, v) => s with { Ligatures = v }, new(D.EditorPage, "Enable ligatures", "font arrows"));

    public static readonly ToggleSetting ShowLineNumbers = new(s => s.ShowLineNumbers, (s, v) => s with { ShowLineNumbers = v },
        new(D.EditorPage, "Show line numbers", "gutter appearance"));

    public static readonly ToggleSetting RainbowBraces = new(s => s.RainbowBraces, (s, v) => s with { RainbowBraces = v },
        new(D.EditorPage, "Rainbow braces", "brackets parentheses colour color nesting"));

    public static readonly ToggleSetting CloseTags = new(s => s.CloseTags, (s, v) => s with { CloseTags = v },
        new(D.EditorPage, "Close tags on '>' and '</'", "auto close closing end tags markup xml pom maven elements",
            "In XML (a POM, an .iml, a JavaFX layout), a start tag's '>' writes its end tag after the caret, and '</' the open element's name."));

    public static readonly ToggleSetting ParameterHints = new(s => s.ParameterHints, (s, v) => s with { ParameterHints = v },
        new(D.EditorPage, "Show parameter name hints", "inlay hints parameter names arguments literals java kotlin",
            "A parameter's name before an argument that does not say it: in Java before a literal, in Kotlin as the call's named argument would read."));

    public static readonly ToggleSetting TypeHints = new(s => s.TypeHints, (s, v) => s with { TypeHints = v },
        new(D.EditorPage, "Show type name hints", "inlay hints type names var val inferred lambda parameters it return java kotlin",
            "The type of a name that does not write it: a Java var or lambda parameter, a Kotlin val, var, lambda parameter or expression-bodied function."));

    public static readonly ToggleSetting SoftWrapIndent = new(s => s.SoftWrapIndent, (s, v) => s with { SoftWrapIndent = v },
        new(D.EditorPage, "Use the original line's indent for wrapped fragments", "soft wrap wrapped indent indentation fragments"));

    public static readonly ToggleSetting ShowWhitespaces = new(s => s.ShowWhitespaces, (s, v) => s with { ShowWhitespaces = v },
        new(D.EditorPage, "Show whitespaces", "whitespace spaces tabs dots arrows invisible characters appearance",
            "Spaces drawn as dots and tabs as arrows in every file; View | Active Editor | Show Whitespaces turns it on or off for one."));

    public static readonly ToggleSetting WheelZoom = new(s => s.WheelZoom, (s, v) => s with { WheelZoom = v },
        new(D.EditorPage, $"Change font size with {KeyWords.Held(Passenger.Editor.Keystroke.Command, "Mouse Wheel")}",
            "zoom font size mouse wheel scroll ctrl control command cmd"));

    public static readonly ToggleSetting FoldComments = new(s => s.FoldComments, (s, v) => s with { FoldComments = v },
        new(D.CodeFoldingPage, "Comments", $"{D.FoldingWords} block comments xml json",
            "Fold by default: what folds as a file opens. Comments over several lines, in XML and JSON."));

    public static readonly ToggleSetting FoldXmlTags = new(s => s.FoldXmlTags, (s, v) => s with { FoldXmlTags = v },
        new(D.CodeFoldingPage, "XML tags", $"{D.FoldingWords} xml elements tags pom maven iml fxml"));

    public static readonly NumberSetting TerminalFontSize = new(s => s.TerminalFontSize, (s, v) => s with { TerminalFontSize = v },
        AppSettings.MinFontSize, AppSettings.MaxFontSize, 1, new(D.TerminalPage, "Font size", "font size shell"));

    public static readonly FontSetting TerminalFontFamily = new(nameof(AppSettings.TerminalFontFamily), s => s.TerminalFontFamily, (s, v) => s with { TerminalFontFamily = v },
        "Same as the editor", FontChoices.Code, new(D.TerminalPage, "Font family", "font family typeface monospace monospaced shell"));

    public static readonly ProgramSetting TerminalShell = new(s => s.TerminalShell, (s, v) => s with { TerminalShell = v },
        () => Passenger.Terminal.TerminalSession.DefaultShell().App, new(D.TerminalPage, "Shell path", "shell zsh bash powershell pwsh cmd program",
            "The program a new terminal tab starts, its arguments after it. Empty, your login shell."));

    public static readonly ToggleSetting TerminalCopyOnSelection = new(s => s.TerminalCopyOnSelection, (s, v) => s with { TerminalCopyOnSelection = v },
        new(D.TerminalPage, "Copy on selection", "copy selection clipboard mouse drag word"));

    public static readonly ToggleSetting TerminalCursorBlinking = new(s => s.TerminalCursorBlinking, (s, v) => s with { TerminalCursorBlinking = v },
        new(D.TerminalPage, "Cursor blinking", "cursor caret blink blinking", "Programs can override this setting until the terminal is reset."));

    public static readonly ChoiceSetting<Passenger.Terminal.CursorShape> TerminalCursorStyle = new(s => s.TerminalCursorStyle, (s, v) => s with { TerminalCursorStyle = v },
        [(Passenger.Terminal.CursorShape.Block, "Block"), (Passenger.Terminal.CursorShape.Bar, "Line"), (Passenger.Terminal.CursorShape.Underline, "Underline")],
        new(D.TerminalPage, "Cursor style", "cursor caret type style shape block line bar underline",
            "Programs can override this setting until the terminal is reset. Caret width (Editor) sets how broad Line and Underline are."));

    public static readonly ChoiceSetting<DiffPlace> OpenDiffsIn = new(s => s.OpenDiffsIn, (s, v) => s with { OpenDiffsIn = v },
        [(DiffPlace.Tab, "An editor tab"), (DiffPlace.Window, "A separate window")],
        new(D.VersionControlPage, "Open diffs in", "diff diffs show diff compare with revision branch commit log files window tab editor separate git vcs",
            "Where Show Diff, Compare With and a commit's file in the Git log show the two versions."));

    public static readonly ChoiceSetting<DiffHighlighting> DiffHighlighting = new(s => s.DiffHighlighting, (s, v) => s with { DiffHighlighting = v },
        Vcs.DiffView.HighlightingWays,
        new(D.VersionControlPage, "Highlighting differences", "diff highlight highlighting differences words characters lines inline changed git vcs",
            "What a diff marks inside the lines that changed. A diff's own Settings changes it too."));

    public static readonly ToggleSetting TraceProtocols = new(s => s.TraceProtocols, (s, v) => s with { TraceProtocols = v },
        new(D.DiagnosticsPage, "Trace protocol messages", "trace log dap json-rpc debugger adapter messages verbose",
            "Every message to and from the debugger is written to dap.log in the logs folder."));

    public static readonly ToggleSetting LogEveryProgram = new(s => s.LogEveryProgram, (s, v) => s with { LogEveryProgram = v },
        new(D.DiagnosticsPage, "Log every program started", "debug log processes programs commands git java gradle maven exit code time",
            $"Each program the IDE runs (git, the JDK's tools, the servers) is written to {IdeLog.Ide}.log with its exit code and time. "
            + "Otherwise only those that fail or take a second or more are."));
}
