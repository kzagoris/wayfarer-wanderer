using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Core;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Docking;
using WayfarerWanderer.App.Shell.Explorer;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

public sealed partial class EditorWindow : Window
{
    private int workspaceRequest;
    /// <summary>The keymap in force (Settings' Keymap page), which the editor and the window's actions follow.</summary>
    private Keymap keys = AppActions.Keymap();
    public CodeEditor Editor { get; } = new();
    public BreakpointColumn Breakpoints { get; }
    public EditorActions Actions { get; }
    public BuildToolWindow BuildTool { get; } = new();
    public TestToolWindow TestsTool { get; } = new();
    public DebugToolWindow DebugTool { get; } = new();
    /// <summary>The open folder's files, its modules and source roots marked: the Project tool window, at the left.</summary>
    public ExplorerView Explorer { get; } = new();
    /// <summary>The tool windows' dock, around the editor: Build, Tests and Debug start at the bottom.</summary>
    public DockHost ToolDock { get; } = new();
    public ProjectSession? Session { get; private set; }
    public Border TrustBanner { get; }

    public EditorWindow(TextDocument? document)
    {
        Title = "Wayfarer Wanderer";
        Width = 860;
        Height = 540;
        Editor.Keymap = keys;
        if (document != null)
            Editor.Document = document;
        Editor.IsVisible = document != null;

        Breakpoints = new BreakpointColumn(ToggleClickedLine);
        Actions = new EditorActions(() => Editor.IsVisible, ToggleCurrentBreakpoint,
            () => new ActionContext(Editor, FocusManager?.GetFocusedElement()), () => keys);
        AddGutter();

        AddProjectActions();
        AddSemanticActions();
        AddVcs();
        MenuItem build = MenuAction(EditorActions.Build);
        MenuItem test = MenuAction(EditorActions.RunTests);
        MenuItem run = MenuAction(EditorActions.Run);
        MenuItem debug = MenuAction(EditorActions.Debug);
        MenuItem toggle = MenuAction(EditorActions.ToggleBreakpoint);
        MenuItem stop = MenuAction(EditorActions.Stop);
        MenuItem newProject = MenuAction(EditorActions.NewProject);
        MenuItem runMenu = new()
        {
            Header = "Run",
            Items = { build, test, run, debug, toggle, MenuAction(EditorActions.Continue),
                MenuAction(EditorActions.StepInto), MenuAction(EditorActions.StepOver), MenuAction(EditorActions.StepOut), stop,
                new Separator(), MenuAction(EditorActions.ChooseRunJdk) }
        };
        AddWelcome();
        MenuItem fileMenu = new() { Header = "File", Items = { newProject, MenuAction(EditorActions.OpenWorkspace), MenuAction(AppActions.CloneRepository), RecentMenu(),
            MenuAction(EditorActions.OpenSource), MenuAction(EditorActions.SaveSource), new Separator(), MenuAction(EditorActions.ProjectStructure),
            MenuAction(AppActions.ShowSettings) } };
        fileMenu.SubmenuOpened += (_, _) => RefreshActions(fileMenu);
        runMenu.SubmenuOpened += (_, _) => RefreshActions(runMenu);
        Menu menu = new()
        {
            Items =
            {
                fileMenu,
                SemanticMenu(),
                runMenu,
                GitMenu(),
            }
        };
        DockPanel.SetDock(menu, Dock.Top);
        var query = new TextBox { PlaceholderText = "Find Action" };
        var matches = new ListBox { DisplayMemberBinding = new Avalonia.Data.Binding(nameof(EditorAction.Title)) };
        var palette = new StackPanel
        {
            Width = 340,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsVisible = false,
            Children = { query, matches }
        };
        Button trust = new() { Content = "Trust Workspace" };
        trust.Click += (_, _) => UnawaitedWork.Observe(TrustWorkspaceAsync());
        TrustBanner = new Border
        {
            IsVisible = false,
            Padding = new(8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Workspace code will not run until the folder is trusted." },
                    trust
                }
            }
        };
        void Refresh()
        {
            matches.ItemsSource = Actions.Find(query.Text ?? "").Where(a => a.Available()).ToArray();
            matches.SelectedIndex = matches.ItemCount > 0 ? 0 : -1;
        }
        void ClosePalette()
        {
            palette.IsVisible = false;
            Editor.Focus();
        }
        void RunSelected()
        {
            if (matches.SelectedItem is EditorAction selected && selected.TryExecute())
                ClosePalette();
        }
        query.TextChanged += (_, _) => Refresh();
        matches.DoubleTapped += (_, _) => RunSelected();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            // The first start's page takes every key while it shows.
            if (FirstStart is not null)
                return;
            var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (e.Key == Key.A && e.KeyModifiers == (modifier | KeyModifiers.Shift))
            {
                palette.IsVisible = true;
                query.Text = "";
                Refresh();
                query.Focus();
            }
            else if (palette.IsVisible && e.Key == Key.Enter)
                RunSelected();
            else if (palette.IsVisible && e.Key == Key.Escape)
                ClosePalette();
            else if (palette.IsVisible && e.Key is Key.Down or Key.Up && matches.ItemCount > 0)
                matches.SelectedIndex = Math.Clamp(matches.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, matches.ItemCount - 1);
            else
                return;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddToolWindows(new Grid { Children = { Editor, Welcome, palette } });
        AddExplorerActions();
        DockPanel.SetDock(TrustBanner, Dock.Top);
        DockPanel.SetDock(asking, Dock.Top);
        frame = new DockPanel { Children = { menu, TrustBanner, MissingJdkBanner(), asking, StatusLine(), ToolDock } };
        Content = new Panel { Children = { frame } };
        KeyDown += (_, e) => e.Handled = FirstStart is null && Actions.TryExecute(keys.ActionFor(new Shortcut(Keystroke.From(e))));
        Opened += (_, _) => Editor.Focus();
        Closing += CloseSession;
        Closed += (_, _) => ForgetAllLanguages();
    }

    public async Task OpenWorkspaceAsync(string root, CancellationToken cancellationToken = default)
    {
        int request = ++workspaceRequest;
        ResetSemanticSession();
        if (Session is not null)
        {
            await Session.DisposeAsync();
        }
        if (request != workspaceRequest || closing) return;
        Breakpoints.Reset();
        if (SourcePath != null)
        {
            SourcePath = null;
            Editor.Document = new TextDocument();
            Editor.Highlighter = null;
            ShowBlameOf(null);
        }
        DebugTool.Reset();
        Session = new ProjectSession(root, BuildTool, TestsTool, DebugTool);
        Remember(root);
        ShowWelcomeWhenEmpty();
        Explorer.Load(root, Session.Snapshot);
        // Git reads the folder while it is imported; the folder is open once both are done.
        Task git = OpenVcsAsync(root);
        Session.PausedAt += OpenFrame;
        ProjectSession opened = Session;
        // A build file changed and the folder was imported again: the tree shows the new model, or the kept one stale.
        opened.SnapshotChanged += () =>
        {
            if (Session != opened) return;
            Explorer.Update(opened.Snapshot);
            ShowMissingJdks();
            // The running language servers learn the new model; a failed import leaves them on the last good one.
            if (!opened.IsStale && opened.Snapshot is { } snapshot) ReloadLanguageModels(snapshot);
        };
        TrustBanner.IsVisible = !Session.IsTrusted;
        ShowMissingJdks();
        if (Session.IsTrusted)
        {
            try
            {
                await Session.OpenAsync(cancellationToken);
                ShowToolingJvmMissing(false);
            }
            catch (OperationCanceledException) when (request != workspaceRequest || closing)
            {
                IdeErrors.Observe(git);
                return;
            }
            catch (Exception failure) when (ToolingJvmMissing(failure))
            {
                // No JVM to read the project with: the banner offers one, and the folder stays open without a model.
                ShowToolingJvmMissing(true);
            }
        }
        await git;
        if (request != workspaceRequest || closing) return;
        Explorer.Update(Session.Snapshot);
        ShowMissingJdks();
        // A first start shows Build as the folder opens, unless a bottom window already is; after that the layout kept says
        // what is open.
        if (EditorApplication.State?.Layout.IsKept != true
            && !ToolDock.Windows.Any(w => w.IsShown && w.Anchor is ToolAnchor.BottomLeft or ToolAnchor.BottomRight))
        {
            ToolDock.ShowBehind(ToolWindowIds.Build);
        }
        Title = $"Wayfarer Wanderer - {Path.GetFileName(Path.TrimEndingDirectorySeparator(root))}";
    }

    /// <summary>Puts the editor in the dock's centre, Project at its left, and Build, Tests and Debug at its bottom, each
    /// shown by its own action; the person's window opens on the layout kept last and keeps each change.</summary>
    private void AddToolWindows(Control center)
    {
        ToolDock.Center = center;
        ToolDock.Actions = Actions.Registry;
        Explorer.FileOpened += (_, path) => OpenFromExplorer(path);
        ToolDock.Add(new ToolWindow(ToolWindowIds.Explorer, "Project", IconKeys.Explorer, Explorer, ToolAnchor.LeftTop)
        {
            Action = AppActions.FocusExplorer,
            // The keys go to the rows, not the filter, as Passenger's Explorer takes them.
            Activate = () =>
            {
                ToolDock.Activate(ToolWindowIds.Explorer);
                Explorer.FocusRows();
            },
            OpenAtFirst = true,
        });
        ToolDock.Add(new ToolWindow(ToolWindowIds.Build, "Build", IconKeys.Build, BuildTool, ToolAnchor.BottomLeft) { Action = AppActions.ShowBuild, ActionTitle = "Build Tool Window" });
        ToolDock.Add(new ToolWindow(ToolWindowIds.Tests, "Tests", IconKeys.Tests, TestsTool, ToolAnchor.BottomLeft) { Action = AppActions.ShowTests, ActionTitle = "Tests Tool Window" });
        ToolDock.Add(new ToolWindow(ToolWindowIds.Debug, "Debug", IconKeys.Debug, DebugTool, ToolAnchor.BottomLeft) { Action = AppActions.ShowDebug, ActionTitle = "Debug Tool Window" });
        AddTerminal();
        AddVcsToolWindows();
        if (EditorApplication.State is { } state)
        {
            if (state.Layout.IsKept)
            {
                ToolDock.Apply(state.Layout.Value);
            }
            ToolDock.LayoutChanged += (_, _) => state.Layout.Set(ToolDock.Capture());
        }
    }

    /// <summary>A file chosen in the Explorer, opened in the editor: a binary file has no text to show, so it stays as it was.</summary>
    private void OpenFromExplorer(string path)
    {
        try
        {
            OpenSource(path);
        }
        catch (InvalidDataException)
        {
            // A binary file: the editor keeps what it showed.
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Report(error);
        }
    }

    private void ToggleClickedLine(int line)
    {
        Editor.GoTo(new TextPosition(line, 0));
        Actions.TryExecute(EditorActions.ToggleBreakpoint);
    }

    private void AddProjectActions()
    {
        Actions.Add(new EditorAction(EditorActions.Build, "Build Current Module", () => Session is { IsTrusted: true, IsRunning: false }, BuildCurrent));
        Actions.Add(new EditorAction(EditorActions.RunTests, "Run Module Tests", () => Session is { IsTrusted: true, IsRunning: false }, TestCurrent));
        Actions.Add(new EditorAction(EditorActions.Run, "Run", () => Session is { IsTrusted: true, IsRunning: false }, RunCurrent));
        Actions.Add(new EditorAction(EditorActions.Debug, "Debug", () => Session is { IsTrusted: true, IsRunning: false }, DebugCurrent));
        Actions.Add(new EditorAction(EditorActions.Stop, "Stop", () => Session?.IsRunning == true, () =>
            UnawaitedWork.Observe(Session!.CancelAsync())));
        AddDebugAndFileActions();
        Actions.Add(new EditorAction(EditorActions.NewProject, "New Project…", () => true, () =>
            UnawaitedWork.Observe(new NewProjectAction().RunAsync(this))));
        Actions.Add(new EditorAction(EditorActions.ProjectStructure, "Project Structure…", () => Session is not null, () =>
            UnawaitedWork.Observe(ShowProjectStructureAsync())));
        AddLaunchJdkActions();
    }

    private MenuItem MenuAction(string id)
    {
        EditorAction action = Actions[id];
        MenuItem item = new() { Header = action.Title, IsEnabled = action.Available(), Tag = id, InputGesture = MenuGesture(id) };
        item.Click += (_, _) => action.TryExecute();
        return item;
    }

    /// <summary>The keys a menu item shows for its action: its first single-keystroke shortcut in the keymap in force, or
    /// none for an action with only chords or no keys.</summary>
    private KeyGesture? MenuGesture(string id) =>
        keys.ShortcutsFor(id).FirstOrDefault(shortcut => shortcut.Second is null) is { First: var stroke }
            ? new KeyGesture(stroke.Key, stroke.Modifiers)
            : null;

    private void RefreshActions(MenuItem menu)
    {
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is string id)
            {
                item.IsEnabled = Actions[id].Available();
                // The keymap may have changed in Settings since the menu last opened.
                item.InputGesture = MenuGesture(id);
            }
            else if (item.Tag is Func<bool> can)
            {
                item.IsEnabled = can();
            }
        }
    }

    private void BuildCurrent()
    {
        ToolDock.ShowBehind(ToolWindowIds.Build);
        JvmModule? module = Session?.Snapshot?.Modules.FirstOrDefault();
        if (module is not null)
        {
            UnawaitedWork.Observe(Session!.BuildAsync(module.Id, "test"));
        }
    }

    private void TestCurrent()
    {
        ToolDock.ShowBehind(ToolWindowIds.Tests);
        JvmModule? module = Session?.Snapshot?.Modules.FirstOrDefault(candidate =>
            candidate.SourceRoots.Any(root => root.Kind is JvmSourceKind.TestJava or JvmSourceKind.TestKotlin));
        if (module is not null)
        {
            UnawaitedWork.Observe(Session!.TestAsync(new TestSelector(
                Session.Snapshot!.BuildSystem, module.Id, string.Empty)));
        }
    }

    private void RunCurrent()
    {
        // The program's output goes to Build, as a build's does.
        ToolDock.ShowBehind(ToolWindowIds.Build);
        if (CurrentLaunch() is not null)
            UnawaitedWork.Observe(RunCurrentAsync());
    }

    private async Task TrustWorkspaceAsync()
    {
        if (Session is null)
        {
            return;
        }
        Session.Trust();
        KeepTrust(Session.Root);
        TrustBanner.IsVisible = false;
        try
        {
            await Session.OpenAsync();
            ShowToolingJvmMissing(false);
        }
        catch (Exception failure) when (ToolingJvmMissing(failure))
        {
            ShowToolingJvmMissing(true);
        }
        Explorer.Update(Session.Snapshot);
        ShowMissingJdks();
    }
}
