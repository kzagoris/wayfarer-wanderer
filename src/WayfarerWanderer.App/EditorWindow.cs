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
        AddWelcome();
        AddMainMenuActions();
        TrustBanner = TrustWorkspaceBanner();
        AddNavigation();
        AddSwitcher();
        AddFindInFiles();
        AddToolWindows(new Grid { Children = { Editor, Welcome } });
        AddExplorerActions();
        DockPanel.SetDock(TrustBanner, Dock.Top);
        DockPanel.SetDock(asking, Dock.Top);
        Control bar = Toolbar();
        DockPanel.SetDock(bar, Dock.Top);
        frame = new DockPanel { Children = { bar, TrustBanner, MissingJdkBanner(), asking, StatusLine(), ToolDock } };
        Content = new Panel { Children = { frame, searchLayer } };
        // An editing action's keys (the clipboard's, Undo) are the editor's or a text box's where one has them; elsewhere
        // they run nothing, as a menu item for them would run them in the editor.
        KeyDown += (_, e) => e.Handled = FirstStart is null && keys.ActionFor(new Shortcut(Keystroke.From(e))) is { } pressed
            && Actions.Registry[pressed] is { InEditor: false } && Actions.TryExecute(pressed);
        Opened += (_, _) => Editor.Focus();
        Closing += CloseSession;
        Closed += (_, _) => ForgetAllLanguages();
    }

    /// <summary>Find Action: every action by name, the ones that cannot run where the keys were greyed, run by Enter.</summary>
    public void OpenFindAction() => ShowFindAction();

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
        ForgetPlaces();
        if (SourcePath != null)
        {
            SourcePath = null;
            Editor.Document = new TextDocument();
            Editor.Highlighter = null;
            ShowBlameOf(null);
        }
        DebugTool.Reset();
        RunTool.Console.Reset();
        DebugTool.Console.Reset();
        lastLaunchDebugs = null;
        Session = new ProjectSession(root, BuildTool, TestsTool, DebugTool, RunTool);
        Remember(root);
        ShowWelcomeWhenEmpty();
        Explorer.Load(root, Session.Snapshot);
        // Git reads the folder while it is imported; the folder is open once both are done.
        Task git = OpenVcsAsync(root);
        FollowDebugSession(Session);
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
                if (request == workspaceRequest && !closing) StartLanguageServers();
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
        UpdateToolbar();
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
        AddRunAndDebugTools();
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
        // The theme's layout as the dock's default, laid over what the person has not arranged, now and as it changes.
        EventHandler followTheme = (_, _) => ToolDock.UseDefault(Themes.Layout);
        followTheme(null, EventArgs.Empty);
        Themes.LayoutChanged += followTheme;
        Closed += (_, _) => Themes.LayoutChanged -= followTheme;
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
        AddRunAndDebugActions();
        Actions.Add(new EditorAction(EditorActions.NewProject, "New Project…", () => true, () =>
            UnawaitedWork.Observe(new NewProjectAction().RunAsync(this))));
        Actions.Add(new EditorAction(EditorActions.ProjectStructure, "Project Structure…", () => Session is not null, () =>
            UnawaitedWork.Observe(ShowProjectStructureAsync())));
        AddLaunchJdkActions();
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
        // The build's output goes to Build; the program's to the Run window's console, shown as it starts.
        ToolDock.ShowBehind(ToolWindowIds.Build);
        if (CurrentLaunch() is not null)
        {
            lastLaunchDebugs = false;
            UnawaitedWork.Observe(RunCurrentAsync());
        }
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
            StartLanguageServers();
        }
        catch (Exception failure) when (ToolingJvmMissing(failure))
        {
            ShowToolingJvmMissing(true);
        }
        Explorer.Update(Session.Snapshot);
        ShowMissingJdks();
    }
}
