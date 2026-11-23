using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Shape = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WayfarerWanderer.App.Shell;

namespace WayfarerWanderer.App;

/// <summary>The toolbar, which is the window's title bar too, as Passenger's and IntelliJ IDEA's new UI have it: at its
/// left the main menu (<see cref="MainMenuPlace"/>), the project's widget (its recent projects, New, Open, Clone and
/// Close Project) and Git's (the branch, and what Git does next); at its right the run configuration's widget, Build,
/// Run, Debug and Stop, Find Action and Settings, then the window's own buttons. On Windows and macOS the window extends
/// into its frame and the bar's empty space moves it (Windows told which part is which by roles, so its snap, double
/// click and window menu work on it); on Linux the system's title bar stays above it. The bar's names and buttons follow
/// what the window can do as IDEA's toolbars do, looked at again a few times a second.</summary>
public sealed partial class EditorWindow
{
    /// <summary>The toolbar's height, which the title bar takes where the two are one.</summary>
    private const double ToolbarHeight = 40;

    /// <summary>Room at the bar's left for macOS's traffic lights.</summary>
    private const double MacWindowButtons = 72;

    private readonly Border toolbar = new() { Height = ToolbarHeight, Classes = { StyleClasses.TitleBar } };
    private readonly ToolbarRow toolbarContent = new() { Margin = new Thickness(8, 0) };
    private readonly StackPanel windowButtons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), IsVisible = false };
    private readonly Button maximizeButton = new() { Classes = { StyleClasses.WindowButton } };
    private readonly Shape maximizeGlyph = new() { Data = Geometry.Parse(RestoredGlyph) };
    private readonly TextBlock projectName = new() { Classes = { StyleClasses.ProjectName }, Text = NoProject, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis };
    // The project's initials, as Shell.axaml's Border.initials.small draws them on the title bar's own colours.
    private readonly TextBlock projectBadgeText = new() { Classes = { StyleClasses.BadgeText } };
    private readonly Border projectBadge = new() { Classes = { StyleClasses.Initials, StyleClasses.Small }, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly TextBlock vcsName = new() { Text = NoVcs, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock runConfigName = new() { Text = NoConfiguration, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly List<(Button Button, string Action)> toolbarActions = [];
    private readonly DispatcherTimer toolbarFollows = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private const string NoProject = "No project";
    private const string NoVcs = "Version control";
    private const string NoConfiguration = "No configuration";
    private const string RestoredGlyph = "M0.5,0.5 H9.5 V9.5 H0.5 Z";
    private const string MaximizedGlyph = "M0.5,2.5 H7.5 V9.5 H0.5 Z M2.5,2.5 V0.5 H9.5 V7.5 H7.5";

    /// <summary>The project's widget on the toolbar.</summary>
    public Button ProjectWidget { get; } = new() { Classes = { StyleClasses.Widget }, Margin = new Thickness(0, 0, 2, 0), MinWidth = 80 };

    /// <summary>Git's widget on the toolbar.</summary>
    public Button VcsWidget { get; } = new() { Classes = { StyleClasses.Widget }, MinWidth = 76 };

    /// <summary>The run configuration's widget on the toolbar.</summary>
    public Button RunConfigWidget { get; } = new() { Classes = { StyleClasses.Widget }, MinWidth = 52 };

    /// <summary>Whether the toolbar is the window's title bar.</summary>
    public bool TitleBarMerged { get; private set; }

    /// <summary>The toolbar made: its groups, its widgets' menus, and the title bar it becomes where the system allows.</summary>
    private Control Toolbar()
    {
        AddMainMenu();
        menuButton.Focusable = true;

        projectBadge.Child = projectBadgeText;
        ProjectWidget.Content = new DockPanel { Children = { Docked(projectBadge, Dock.Left), Docked(Chevron(), Dock.Right), projectName } };
        ToolTip.SetTip(ProjectWidget, "Open, make or close a project");
        AutomationProperties.SetLabeledBy(ProjectWidget, projectName);
        ProjectWidget.Click += (_, _) => ShowWidgetMenu(ProjectWidget, ProjectMenu());

        PathIcon branch = IconOf(IconKeys.Branch);
        branch.Margin = new Thickness(0, 0, 6, 0);
        VcsWidget.Content = new DockPanel { Children = { Docked(branch, Dock.Left), Docked(Chevron(), Dock.Right), vcsName } };
        ToolTip.SetTip(VcsWidget, NoVcs);
        VcsWidget.Click += (_, _) => ShowWidgetMenu(VcsWidget, VcsMenu());

        RunConfigWidget.Content = new DockPanel { Children = { Docked(Chevron(), Dock.Right), runConfigName } };
        ToolTip.SetTip(RunConfigWidget, "Run configuration");
        ShrinkingRow.SetGoesWhole(RunConfigWidget, true);
        RunConfigWidget.Click += (_, _) => ShowWidgetMenu(RunConfigWidget, [EditorActions.Run, EditorActions.Debug, null, EditorActions.ChooseRunJdk]);

        ShrinkingRow start = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Children = { menuPlace, ProjectWidget, VcsWidget } };
        ShrinkingRow end = new()
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                RunConfigWidget,
                ActionButton(EditorActions.Build, IconKeys.Build, "Build current module"),
                ActionButton(EditorActions.Run, IconKeys.Run, "Run", StyleClasses.ToolbarRun),
                ActionButton(EditorActions.Debug, IconKeys.Debug, "Debug"),
                ActionButton(EditorActions.Stop, IconKeys.Stop, "Stop", StyleClasses.ToolbarStop),
                new Border { Classes = { StyleClasses.ToolbarRule } },
                ActionButton(AppActions.FindAction, IconKeys.Search, "Find action"),
                ActionButton(AppActions.ShowSettings, IconKeys.Settings, "Settings"),
            },
        };
        foreach (Control group in new Control[] { start, end })
        {
            WindowDecorationProperties.SetElementRole(group, WindowDecorationsElementRole.User);
        }
        AddWindowButtons();
        toolbarContent.Children.Add(Docked(windowButtons, Dock.Right));
        toolbarContent.Children.Add(Docked(end, Dock.Right));
        toolbarContent.Children.Add(start);
        toolbar.Child = toolbarContent;
        toolbar.PointerPressed += OnToolbarPressed;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                LayOutTitleBar();
            }
        };
        MergeTitleBar(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
        toolbarFollows.Tick += (_, _) => UpdateToolbar();
        Opened += (_, _) => toolbarFollows.Start();
        Closed += (_, _) => toolbarFollows.Stop();
        UpdateToolbar();
        return toolbar;
    }

    private static T Docked<T>(T control, Dock dock)
        where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static PathIcon IconOf(string key)
    {
        PathIcon icon = new();
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(key));
        return icon;
    }

    private static PathIcon Chevron()
    {
        PathIcon chevron = IconOf(IconKeys.ChevronDown);
        chevron.Classes.Add(StyleClasses.Chevron);
        chevron.Margin = new Thickness(6, 0, 0, 0);
        return chevron;
    }

    /// <summary>A toolbar button that runs an action as its keys do, showing its work while it runs and greyed while it cannot.</summary>
    private Button ActionButton(string action, string icon, string tip, string? iconClass = null)
    {
        Button button = ToolButtons.Of(icon, tip);
        if (iconClass is not null && button.Content is PathIcon glyph)
        {
            glyph.Classes.Add(iconClass);
        }
        ActionButtons.Bind(button, action, Actions.Registry, id => RunFromMenu(id));
        toolbarActions.Add((button, action));
        return button;
    }

    /// <summary>The project's widget's menu: making and opening projects, the recent ones but the one open, and Close Project.</summary>
    private IReadOnlyList<object?> ProjectMenu()
    {
        List<object?> items = [EditorActions.NewProject, EditorActions.OpenWorkspace, AppActions.CloneRepository];
        List<Shell.Welcome.RecentRow> recent = [.. RecentProjectRows().Take(10)];
        if (recent.Count > 0)
        {
            items.Add(null);
            items.AddRange(recent.Select(row => (object?)new ListedItem($"{row.Name} – {row.Shown}", () => OpenRecent(row.Path))));
        }
        if (Session is not null)
        {
            items.Add(null);
            items.Add(AppActions.CloseSolution);
        }
        return items;
    }

    /// <summary>Git's widget's menu: what is done next in a repository, or making or cloning one outside any.</summary>
    private IReadOnlyList<object?> VcsMenu() => Vcs.Repository is null
        ? [AppActions.CreateRepository, AppActions.CloneRepository]
        : [AppActions.Commit, AppActions.Push, AppActions.UpdateProject, AppActions.Fetch, null, AppActions.Branches, null, AppActions.ShowDiff, AppActions.Annotate];

    /// <summary>A widget's menu under it: each action as the main menu shows it, a listed item, or a separator (null).</summary>
    private void ShowWidgetMenu(Button widget, IReadOnlyList<object?> items)
    {
        ActionContext context = barKeysContext ?? Actions.Registry.Context();
        barKeysContext = null;
        MenuFlyout flyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (object? item in items)
        {
            switch (item)
            {
                case null:
                    flyout.Items.Add(new Separator());
                    break;
                case string action when Actions.Registry[action] is not null:
                    flyout.Items.Add(FlyoutEntry(new MenuEntry(action), context));
                    break;
                case ListedItem listed:
                    MenuItem each = new() { Header = listed.Title };
                    each.Click += (_, _) => listed.Run();
                    flyout.Items.Add(each);
                    break;
            }
        }
        flyout.ShowAt(widget);
    }

    /// <summary>The bar's names and buttons as the window stands: the project and its badge, the branch, the configuration
    /// Run and Debug take, and each action button greyed while its action cannot run.</summary>
    public void UpdateToolbar()
    {
        string? root = Session?.Root;
        string name = root is null ? NoProject : System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(root));
        if (projectName.Text != name)
        {
            projectName.Text = name;
            projectBadgeText.Text = Badge(name);
            projectBadge.IsVisible = root is not null;
            ToolTip.SetTip(ProjectWidget, root ?? "Open, make or close a project");
        }
        string vcs = Vcs.Repository is null ? NoVcs : Vcs.Branch ?? "Detached HEAD";
        if (vcsName.Text != vcs)
        {
            vcsName.Text = vcs;
            ToolTip.SetTip(VcsWidget, Vcs.Repository is null ? NoVcs : $"Git branch: {vcs}");
        }
        string configuration = CurrentLaunch() is { } launch ? launch.MainClass[(launch.MainClass.LastIndexOf('.') + 1)..] : NoConfiguration;
        if (runConfigName.Text != configuration)
        {
            runConfigName.Text = configuration;
        }
        ActionContext context = Actions.Registry.Context();
        foreach ((Button button, string action) in toolbarActions)
        {
            button.IsEnabled = Actions.Registry.WhyNot(action, context) is null || ActionButtons.IsWorking(button);
        }
    }

    /// <summary>A project's badge: the first letters of its name's first two words, or its first two letters.</summary>
    public static string Badge(string name)
    {
        string[] words = [.. name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)];
        string letters = words.Length >= 2 ? $"{words[0][0]}{words[1][0]}" : name.Length >= 2 ? name[..2] : name;
        return letters.ToUpperInvariant();
    }

    // The title bar.

    private void AddWindowButtons()
    {
        Button minimize = WindowButton("Minimize", WindowDecorationsElementRole.MinimizeButton, new Shape { Data = Geometry.Parse("M0,5.5 H10") });
        minimize.Click += (_, _) => WindowState = WindowState.Minimized;
        maximizeButton.Content = maximizeGlyph;
        WindowDecorationProperties.SetElementRole(maximizeButton, WindowDecorationsElementRole.MaximizeButton);
        maximizeButton.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        Button close = WindowButton("Close", WindowDecorationsElementRole.CloseButton, new Shape { Data = Geometry.Parse("M0,0 L10,10 M10,0 L0,10") });
        close.Classes.Add(StyleClasses.Close);
        close.Click += (_, _) => Close();
        windowButtons.Children.AddRange([minimize, maximizeButton, close]);
    }

    private static Button WindowButton(string name, WindowDecorationsElementRole role, Shape glyph)
    {
        Button button = new() { Classes = { StyleClasses.WindowButton }, Content = glyph };
        ToolTip.SetTip(button, name);
        AutomationProperties.SetName(button, name);
        WindowDecorationProperties.SetElementRole(button, role);
        return button;
    }

    /// <summary>The toolbar becomes the title bar, or the system's stays above it.</summary>
    private void MergeTitleBar(bool merged)
    {
        TitleBarMerged = merged;
        if (merged && Application.Current?.TryGetResource("TitleBarDecorations", null, out object? theme) == true && theme is ControlTheme decorations)
        {
            WindowDecorationsTheme = decorations;
        }
        ExtendClientAreaToDecorationsHint = merged;
        ExtendClientAreaTitleBarHeightHint = merged ? ToolbarHeight * SettingsKept.Value.DisplayScale : -1;
        WindowDecorationProperties.SetElementRole(toolbar, merged && OperatingSystem.IsWindows() ? WindowDecorationsElementRole.TitleBar : WindowDecorationsElementRole.None);
        LayOutTitleBar();
    }

    /// <summary>The title bar's height follows the display scale Settings gives the window.</summary>
    private void FollowTitleBar(Shell.Settings.AppSettings settings) =>
        ExtendClientAreaTitleBarHeightHint = TitleBarMerged ? ToolbarHeight * settings.DisplayScale : -1;

    /// <summary>Whether macOS's traffic lights stand on the bar: but in full screen, where macOS hides them.</summary>
    private bool ShowsTrafficLights => TitleBarMerged && OperatingSystem.IsMacOS() && WindowState != WindowState.FullScreen;

    /// <summary>The bar as the window stands: the traffic lights' room, the window's buttons but in full screen, the restore
    /// glyph while maximized.</summary>
    private void LayOutTitleBar()
    {
        bool fullScreen = WindowState == WindowState.FullScreen;
        windowButtons.IsVisible = TitleBarMerged && !OperatingSystem.IsMacOS() && !fullScreen;
        toolbarContent.Margin = new Thickness(ShowsTrafficLights ? MacWindowButtons : 8, 0, windowButtons.IsVisible ? 0 : 8, 0);
        bool maximized = WindowState == WindowState.Maximized;
        maximizeGlyph.Data = Geometry.Parse(maximized ? MaximizedGlyph : RestoredGlyph);
        string name = maximized ? "Restore" : "Maximize";
        ToolTip.SetTip(maximizeButton, name);
        AutomationProperties.SetName(maximizeButton, name);
    }

    /// <summary>A press on the bar's empty space moves the window and a double click maximizes or restores it, as the
    /// system's title bar does. Where Windows has the bar's roles the press never comes here: Windows takes it as its own
    /// caption's.</summary>
    private void OnToolbarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!TitleBarMerged || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        BeginMoveDrag(e);
    }
}
