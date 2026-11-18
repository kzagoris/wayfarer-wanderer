using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Core;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

public sealed class EditorWindow : Window
{
    public CodeEditor Editor { get; } = new();
    public BreakpointColumn Breakpoints { get; }
    public EditorActions Actions { get; }
    public BuildToolWindow BuildTool { get; } = new();
    public TestToolWindow TestsTool { get; } = new();
    public DebugToolWindow DebugTool { get; } = new();
    public ProjectSession? Session { get; private set; }
    public Border TrustBanner { get; }

    public EditorWindow(TextDocument? document)
    {
        Title = "Wayfarer Wanderer";
        Width = 860;
        Height = 540;
        var keys = DefaultKeymaps.ForPlatform().Bind(EditorActions.ToggleBreakpoint, "F9");
        Editor.Keymap = keys;
        if (document != null)
            Editor.Document = document;
        Editor.IsVisible = document != null;

        Breakpoints = new BreakpointColumn(ToggleClickedLine);
        Actions = new EditorActions(() => document != null, () => Breakpoints.Toggle(Editor.CaretPosition.Line));
        Editor.SetGutterColumns([Breakpoints], []);

        var action = Actions[EditorActions.ToggleBreakpoint];
        var shortcut = keys.ShortcutsFor(action.Id).Single().First;
        var toggle = new MenuItem
        {
            Header = action.Title,
            Tag = action.Id,
            IsEnabled = action.Available(),
            InputGesture = new KeyGesture(shortcut.Key, shortcut.Modifiers)
        };
        toggle.Click += (_, _) => action.TryExecute();
        AddProjectActions();
        MenuItem build = MenuAction(EditorActions.Build);
        MenuItem test = MenuAction(EditorActions.RunTests);
        MenuItem run = MenuAction(EditorActions.Run);
        MenuItem debug = MenuAction(EditorActions.Debug);
        MenuItem stop = MenuAction(EditorActions.Stop);
        MenuItem newProject = MenuAction(EditorActions.NewProject);
        MenuItem runMenu = new() { Header = "_Run", Items = { build, test, run, debug, toggle, stop } };
        runMenu.SubmenuOpened += (_, _) => RefreshActions(runMenu);
        Menu menu = new()
        {
            Items =
            {
                new MenuItem { Header = "_File", Items = { newProject } },
                runMenu
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
        TabControl tools = new()
        {
            Height = 170,
            IsVisible = false,
            Items =
            {
                new TabItem { Header = "Build", Content = BuildTool },
                new TabItem { Header = "Tests", Content = TestsTool },
                new TabItem { Header = "Debug", Content = DebugTool }
            }
        };
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(TrustBanner, Dock.Top);
        DockPanel.SetDock(tools, Dock.Bottom);
        Content = new DockPanel { Children = { menu, TrustBanner, tools, new Grid { Children = { Editor, palette } } } };
        KeyDown += (_, e) => e.Handled = Actions.TryExecute(keys.ActionFor(new Shortcut(Keystroke.From(e))));
        Opened += (_, _) => Editor.Focus();
        Closing += (_, _) => UnawaitedWork.Observe(Session?.DisposeAsync().AsTask() ?? Task.CompletedTask);
    }

    public async Task OpenWorkspaceAsync(string root, CancellationToken cancellationToken = default)
    {
        if (Session is not null)
        {
            await Session.DisposeAsync();
        }
        Session = new ProjectSession(root, BuildTool, TestsTool, DebugTool);
        TrustBanner.IsVisible = !Session.IsTrusted;
        if (Session.IsTrusted)
        {
            await Session.ImportAsync(cancellationToken);
        }
        if (Content is DockPanel panel && panel.Children.OfType<TabControl>().FirstOrDefault() is { } tools)
        {
            tools.IsVisible = true;
        }
        Title = $"Wayfarer Wanderer - {Path.GetFileName(Path.TrimEndingDirectorySeparator(root))}";
    }

    private void ToggleClickedLine(int line)
    {
        Editor.GoTo(new TextPosition(line, 0));
        Actions.TryExecute(EditorActions.ToggleBreakpoint);
    }

    private void AddProjectActions()
    {
        Actions.Add(new EditorAction(EditorActions.Build, "Build Current Module", () => Session?.IsTrusted == true, BuildCurrent));
        Actions.Add(new EditorAction(EditorActions.RunTests, "Run Module Tests", () => Session?.IsTrusted == true, TestCurrent));
        Actions.Add(new EditorAction(EditorActions.Run, "Run", () => Session?.IsTrusted == true, RunCurrent));
        Actions.Add(new EditorAction(EditorActions.Debug, "Debug", () => Session?.IsTrusted == true, DebugCurrent));
        Actions.Add(new EditorAction(EditorActions.Stop, "Stop", () => Session?.IsRunning == true, () =>
            UnawaitedWork.Observe(Session!.CancelAsync())));
        Actions.Add(new EditorAction(EditorActions.NewProject, "New Project…", () => true, () =>
            UnawaitedWork.Observe(new NewProjectAction().RunAsync(this))));
    }

    private MenuItem MenuAction(string id)
    {
        EditorAction action = Actions[id];
        MenuItem item = new() { Header = action.Title, IsEnabled = action.Available(), Tag = id };
        item.Click += (_, _) => action.TryExecute();
        return item;
    }

    private void RefreshActions(MenuItem menu)
    {
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is string id)
            {
                item.IsEnabled = Actions[id].Available();
            }
        }
    }

    private void BuildCurrent()
    {
        JvmModule? module = Session?.Snapshot?.Modules.FirstOrDefault();
        if (module is not null)
        {
            UnawaitedWork.Observe(Session!.BuildAsync(module.Id, "test"));
        }
    }

    private void TestCurrent()
    {
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
        WorkspaceSnapshot? snapshot = Session?.Snapshot;
        JvmModule? module = snapshot?.Modules.FirstOrDefault(candidate => candidate.MainClasses.Count > 0);
        if (module is not null)
        {
            JvmLaunchConfiguration configuration = new()
            {
                Name = "Current",
                Module = module.Id,
                MainClass = module.MainClasses[0].ClassName,
                WorkingDirectory = module.Root,
                JavaHome = module.RuntimeToolchain.Home ?? Environment.GetEnvironmentVariable("JAVA_HOME") ?? string.Empty
            };
            UnawaitedWork.Observe(Session!.RunAsync(configuration));
        }
    }

    private void DebugCurrent()
    {
        DebugTool.Frames.Add("Debug adapter is not configured for this workspace.");
        DebugTool.Variables.Add("Choose a supported JVM adapter before starting a debug session.");
    }

    private async Task TrustWorkspaceAsync()
    {
        if (Session is null)
        {
            return;
        }
        Session.Trust();
        await Session.ImportAsync();
        TrustBanner.IsVisible = false;
    }
}
