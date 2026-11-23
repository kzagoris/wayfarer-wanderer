// From Passenger 8a1df7e4 src/Passenger.App/Settings/SettingsDialog.cs, MIT (notices/Passenger/LICENSE): its Appearance,
// System Settings, Trusted Folders and Keymap pages; the .NET, MCP, template, code style and tool pages cut.
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Editor;

namespace WayfarerWanderer.App.Shell.Settings;

/// <summary>Settings: the pages down the left under a search box, the chosen page's options on the right, OK, Cancel
/// and Apply below. A search keeps the pages with an option whose words match and shows only those options. The options
/// edit a copy, previewing its visual changes until Apply or OK commits them or closing discards them.</summary>
public sealed partial class SettingsDialog : IdeWindow
{
    public const string Appearance = "Appearance";
    public const string SystemPage = "System Settings";
    public const string AccessibilityPage = "Accessibility";
    public const string EditorPage = "Editor";
    public const string KeymapPage = "Keymap";

    /// <summary>A page as a message sends a person to it, by its name above as the dialog lists it:
    /// "Settings | Appearance".</summary>
    public static string PlaceOfPage(string page) => $"Settings | {page}";

    private static readonly string[] Order = [Appearance, SystemPage, AccessibilityPage, TrustPage, EditorPage, KeymapPage];

    /// <summary>Puts what the options changed in force: from the settings as they were applied last, to the options'.</summary>
    private readonly Action<AppSettings, AppSettings> apply;
    private readonly SettingsPreview? preview;

    /// <summary>The settings as the dialog opened on them or last applied them, which what is changed since is told
    /// from, so another window's change meanwhile is not undone.</summary>
    private AppSettings applied;

    /// <summary>The window's actions, which the keymap page lists.</summary>
    private readonly ActionRegistry actions;
    private readonly List<Option> options = [];
    private readonly TextBox search;
    private readonly ListBox pages;
    private readonly TextBlock heading;
    private readonly StackPanel body;
    private readonly ToolWindows.EmptyState nothing;

    /// <summary>The page's room at its right: its gutter, where its scroll bar shows over the page, clear of what it shows.</summary>
    private const double Gutter = Gaps.Page;

    /// <summary>The search field's room in the pages' column, the pages' bands as wide as it below.</summary>
    private static readonly Thickness PagesInset = new(Gaps.Normal);

    /// <summary>What a page keeps above its options as they scroll (the Keymap page's toolbar).</summary>
    private readonly Decorator pageTop = new() { Margin = new Thickness(0, 0, Gutter, 0) };

    /// <summary>OK and Apply, which put the options in force: greyed while an option holds a value Settings refuses.</summary>
    private readonly Button ok = DialogFooter.Default();
    private readonly Button applyButton = new() { Content = "Apply" };

    /// <param name="preview">An optional temporary visual overlay, released when this dialog closes.</param>
    /// <param name="trust">The folders the person answered for, which the Trusted Folders page shows, where the window keeps them.</param>
    public SettingsDialog(AppSettings current, ActionRegistry actions, Action<AppSettings, AppSettings> apply, SettingsPreview? preview = null, TrustAnswers? trust = null)
    {
        this.apply = apply;
        this.trust = trust;
        this.preview = preview;
        Closed += (_, _) => preview?.Dispose();
        this.actions = actions;
        Working = current;
        applied = current;
        Title = "Settings";
        Width = DialogWidths.Workspace;
        Height = 700;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AddOptions();
        search = SearchBox.Of("Search settings");
        search.Margin = new Thickness(PagesInset.Left, PagesInset.Top, PagesInset.Right, Gaps.Small);
        search.TextChanged += (_, _) => Filter();
        pages = new ListBox { Classes = { StyleClasses.Tree, StyleClasses.Pages }, Padding = new Thickness(PagesInset.Left, 0) };
        pages.SelectionChanged += (_, _) => ShowPage();
        heading = new TextBlock { Classes = { StyleClasses.PageTitle }, Margin = new Thickness(0, 0, Gutter, Gaps.Wide) };
        // The scroll bar shows over the scrolled page's right, so the gutter is inside it: the bar stands there, beside the rows.
        body = new StackPanel { Spacing = Gaps.Wide, Margin = new Thickness(0, 0, Gutter, 0) };
        nothing = new ToolWindows.EmptyState("Nothing matches") { IsVisible = false };

        ok.Click += (_, _) => Ok();
        applyButton.Click += (_, _) => Apply();

        DockPanel left = new() { Width = 240 };
        DockPanel.SetDock(search, Dock.Top);
        left.Children.Add(search);
        left.Children.Add(new Panel { Children = { pages, nothing } });
        Border leftPane = new() { BorderThickness = Hairlines.After, Child = left };
        leftPane.Bind(Border.BorderBrushProperty, leftPane.GetResourceObservable(TokenKeys.Divider));
        DockPanel right = new() { Margin = new Thickness(Gaps.Page, Gaps.Group, 0, 0) };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(pageTop, Dock.Top);
        right.Children.Add(heading);
        right.Children.Add(pageTop);
        right.Children.Add(new ScrollViewer { Content = body });
        DockPanel.SetDock(leftPane, Dock.Left);
        Content = DialogFooter.Page(new DockPanel { Children = { leftPane, right } }, DialogFooter.OkCancel(ok, () => Close(false), more: applyButton));
        Filter();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>The settings as the options now stand.</summary>
    public AppSettings Working { get; private set; }

    public string Query
    {
        get => search.Text ?? string.Empty;
        set => search.Text = value;
    }

    /// <summary>The pages listed, after the search.</summary>
    public IReadOnlyList<string> Pages => pages.ItemsSource as IReadOnlyList<string> ?? [];

    public string? Page
    {
        get => pages.SelectedItem as string;
        set => pages.SelectedItem = value;
    }

    /// <summary>The options the chosen page shows now, by their labels.</summary>
    public IReadOnlyList<string> Shown => [.. options.Where(o => o.Page == Page && o.Row.IsVisible).Select(o => o.Label)];

    /// <summary>Why the options as they stand cannot be put in force, naming the setting whose value Settings refuses;
    /// null when they can.</summary>
    public string? Refused => Declared.All.Select(s => s.Refused(applied, Working)).FirstOrDefault(r => r is not null);

    /// <summary>OK: the settings applied, and the dialog closed; nothing while a value is refused.</summary>
    public void Ok()
    {
        if (Refused is null)
        {
            Apply();
            Close(true);
        }
    }

    /// <summary>Apply: what the options changed put in force, the dialog kept open; nothing while a value is refused.</summary>
    public void Apply()
    {
        if (Refused is null)
        {
            apply(applied, Working);
            applied = Working;
            preview?.Update(applied, Working);
            ApplyTrust();
        }
    }

    /// <summary>Changes one setting, as its option does, OK and Apply greyed while a value it holds is refused.</summary>
    public void Change(Func<AppSettings, AppSettings> change)
    {
        Working = change(Working);
        ok.IsEnabled = applyButton.IsEnabled = Refused is null;
        preview?.Update(applied, Working);
        foreach ((_, Control editor, SettingWhen when) in dependents)
        {
            Follow(editor, when);
        }
    }

    private void Filter()
    {
        string[] words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Func<Option, bool> byShortcut = ShortcutFiltered();
        foreach (Option option in options)
        {
            option.Row.IsVisible = words.All(w => option.Words.Contains(w, StringComparison.OrdinalIgnoreCase)) && byShortcut(option);
        }
        string? was = Page;
        string[] listed = [.. Order.Where(p => options.Any(o => o.Page == p && o.Row.IsVisible)
            || (p == KeymapPage && (shortcutFilter is not null || FindsKeymapChoice(words))))];
        // A new list chooses no page for a moment, which would take the Keymap page's top, Find Shortcut with it, away.
        if (!listed.SequenceEqual(Pages))
        {
            pages.ItemsSource = listed;
        }
        nothing.IsVisible = listed.Length == 0;
        pages.SelectedItem = listed.Contains(was) ? was : listed.FirstOrDefault();
        ShowPage();
    }

    private void ShowPage()
    {
        body.Children.Clear();
        heading.Text = Page ?? string.Empty;
        pageTop.Child = Page == KeymapPage ? keymapHeader : null;
        foreach (Option option in options.Where(o => o.Page == Page))
        {
            body.Children.Add(option.Row);
        }
    }

    private void AddOptions()
    {
        AddThemes();
        Add(Declared.WindowFontFamily);
        Add(Declared.DisplayScale);
        Add(Declared.EditorFontFamily);
        Add(Declared.CaretWidth);
        Add(Declared.ShowNavigationBar);
        Add(Declared.MainMenuBar);

        Add(Declared.FollowSystemContrast);
        Add(Declared.StatePalette);
        Add(Declared.SoundCues);
        Add(Declared.SoundOnErrorLine);
        Add(Declared.SoundOnWarningLine);
        Add(Declared.SoundOnBreakpointLine);
        Add(Declared.SoundOnFoldedLine);
        Add(Declared.SoundOnChangedLine);
        Add(Declared.SoundOnBuildEnd);
        Add(Declared.SoundOnTestsEnd);
        Add(Declared.SoundOnDebuggerStop);
        Add(Declared.SoundOnTerminalFailure);
        Add(Declared.SoundOnSaveRefused);

        Add(Declared.ReopenProjectsOnStartup);
        Add(Declared.ConfirmBeforeClosing);

        AddTrust();

        AddKeymap();
    }

    /// <summary>A declared setting's own row, on its page, its editor made from the declaration.</summary>
    private void Add(Setting setting) => Add(setting, setting.Editor(Working, Change));

    /// <summary>A declared setting's own row, its editor made by the dialog round what only the dialog knows.</summary>
    private void Add(Setting setting, Control editor)
    {
        SettingRow row = setting.Row ?? throw new ArgumentException("A setting with no row of its own goes in a row the dialog builds", nameof(setting));
        Add(row.Page, row.Label, editor, row.Words, row.NoteShown);
        if (row.When is { } when)
        {
            // Greyed, saying why, while the setting it follows leaves it no effect.
            ToolTip.SetShowOnDisabled(editor, true);
            dependents.Add((row.Label, editor, when));
            Follow(editor, when);
        }
    }

    /// <summary>The rows another setting greys, each with what it takes effect under.</summary>
    private readonly List<(string Label, Control Editor, SettingWhen When)> dependents = [];

    private void Follow(Control editor, SettingWhen when)
    {
        bool holds = when.Holds(Working);
        editor.IsEnabled = holds;
        ToolTip.SetTip(editor, holds ? null : when.Why);
    }

    /// <summary>Why a row is greyed as the settings stand; null where it takes effect, or is no row another greys.</summary>
    public string? WhyGreyed(string label) =>
        dependents.FirstOrDefault(d => d.Label == label) is { Editor: { } editor, When: { } when } && !when.Holds(Working) && !editor.IsEnabled ? when.Why : null;

    /// <summary>A page that is one control of its own (the live templates' list and editor), the whole of its width, found
    /// by a search for any of its words.</summary>
    private void AddPage(string page, Control content, string keywords) => options.Add(new Option(page, page, content, $"{page} {keywords}"));

    /// <summary>An option's row: its label, what edits it, and a note under the editor when it needs saying what it
    /// does; a keymap row names its action.</summary>
    private Control Add(string page, string label, Control editor, string keywords, string? note = null, string? action = null)
    {
        // Beside an editor of several lines the label stands level with its first line.
        double firstLine = editor switch
        {
            TextBox { AcceptsReturn: true } => 5,
            StackPanel { Orientation: Orientation.Vertical, Children: [TextBox, ..] } => 5,
            StackPanel { Orientation: Orientation.Vertical, Children: [RadioButton or Border { Classes: [StyleClasses.Framed] }, ..] } => 6,
            _ => -1,
        };
        TextBlock name = new()
        {
            Text = label,
            Padding = new Thickness(0, 0, Gaps.Group, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetLabeledBy(editor, name);
        // The one field of an editor of several parts (a check box before it, its problem under it) is heard by the row's words.
        if (editor is not TextBox && editor.GetLogicalDescendants().OfType<TextBox>().ToList() is [{ } field] && AutomationProperties.GetName(field) is null or "")
        {
            AutomationProperties.SetLabeledBy(field, name);
        }
        TextBlock? said = note is null ? null : new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left, Classes = { StyleClasses.Muted } };
        OptionRow row = new(name, editor, said, firstLine, null);
        options.Add(new Option(page, label, row, $"{page} {label} {keywords}", action));
        return row;
    }

    private static CheckBox Check(bool value, Action<bool> changed)
    {
        CheckBox box = new() { IsChecked = value };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    private sealed record Option(string Page, string Label, Control Row, string Words, string? Action = null);
}
