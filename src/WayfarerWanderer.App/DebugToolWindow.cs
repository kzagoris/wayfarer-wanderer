using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Passenger.Dap;
using Passenger.Editor;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>A row of the Debug window's variables tree: a frame's variable, an object's field, an array's element, a
/// watch or an evaluated expression. Its children are asked for as it is first expanded.</summary>
public sealed class VariableNode : Observable
{
    private readonly Func<int, Task<IReadOnlyList<JvmDebugVariable>>>? load;
    private string value;
    private bool isExpanded;
    private bool loaded;

    public VariableNode(string name, string value, string type, int reference, Func<int, Task<IReadOnlyList<JvmDebugVariable>>>? load, bool isWatch = false)
    {
        Name = name;
        this.value = value;
        Type = type;
        Reference = reference;
        IsWatch = isWatch;
        this.load = load;
        if (reference > 0 && load is not null) Children.Add(new VariableNode("", "Collecting data…", "", 0, null));
    }

    public string Name { get; }

    public string Value
    {
        get => value;
        private set => Set(ref this.value, value);
    }

    public string Type { get; }

    public int Reference { get; }

    public bool IsWatch { get; }

    public ObservableCollection<VariableNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (Set(ref isExpanded, value) && value && !loaded) _ = LoadAsync();
        }
    }

    public override string ToString() => Name.Length == 0 ? Value : $"{Name} = {Value}";

    private async Task LoadAsync()
    {
        if (load is null || Reference == 0) return;
        loaded = true;
        try
        {
            IReadOnlyList<JvmDebugVariable> children = await load(Reference);
            Children.Clear();
            foreach (JvmDebugVariable child in children)
                Children.Add(new VariableNode(child.Name, child.Value, child.Type, child.Reference, load));
        }
        catch (Exception error) when (error is DapException or InvalidOperationException or IOException or OperationCanceledException)
        {
            Children.Clear();
            Children.Add(new VariableNode("", $"Cannot read: {error.Message}", "", 0, null));
        }
    }
}

/// <summary>The Debug tool window, as IntelliJ's: the session's toolbar (Rerun, Stop, Resume, Pause, the steps), then two
/// tabs. Threads &amp; Variables lists the threads, the chosen one's frames and the chosen frame's variables, which open
/// into fields and elements, under a box that evaluates an expression or adds it as a watch. Console is where the program
/// runs: its output, and its input typed there.</summary>
public sealed class DebugToolWindow : UserControl
{
    private readonly TextBlock status = new() { Margin = new(8, 4), TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.75 };
    private readonly ObservableCollection<JvmDebugThread> threads = [];
    private readonly ObservableCollection<JvmDebugFrame> frames = [];
    private readonly ObservableCollection<VariableNode> variables = [];
    private readonly List<string> watches = [];
    private readonly ComboBox threadList = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(4, 4, 4, 2) };
    private readonly ListBox frameList = new();
    private readonly TreeView variableTree = new();
    private readonly TextBox evaluate = new()
    {
        PlaceholderText = "Evaluate expression (Enter) or add a watch (Ctrl+Shift+Enter)",
        Margin = new(4, 4, 4, 2),
    };
    private readonly StackPanel tools = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton variablesTab = Tab("Threads & Variables");
    private readonly ToggleButton consoleTab = Tab("Console");
    private readonly ContentControl body = new();
    private readonly Control threadsAndVariables;
    private JvmInteractiveDebugger? debugger;
    private bool showing;
    private int version;

    public DebugToolWindow()
    {
        threadList.ItemsSource = threads;
        threadList.ItemTemplate = new FuncDataTemplate<JvmDebugThread>((thread, _) => new TextBlock { Text = thread?.Display, TextTrimming = TextTrimming.CharacterEllipsis });
        threadList.SelectionChanged += (_, _) =>
        {
            if (!showing && threadList.SelectedItem is JvmDebugThread thread) _ = ShowThreadAsync(thread);
        };
        frameList.ItemsSource = frames;
        frameList.ItemTemplate = new FuncDataTemplate<JvmDebugFrame>((frame, _) => new TextBlock
        {
            Text = frame?.Display,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = frame?.Path is null ? 0.6 : 1,
        });
        frameList.SelectionChanged += (_, _) =>
        {
            if (!showing && frameList.SelectedItem is JvmDebugFrame frame) _ = ShowFrameAsync(frame, open: true);
        };
        CodeFonts.Follow(variableTree, FontFamilyProperty);
        variableTree.ItemsSource = variables;
        variableTree.ItemTemplate = new FuncTreeDataTemplate<VariableNode>((node, _) => Row(node), node => node.Children);
        variableTree.Styles.Add(new Style(x => x.OfType<TreeViewItem>())
        {
            Setters = { new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(VariableNode.IsExpanded)) { Mode = BindingMode.TwoWay }) },
        });
        variableTree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && variableTree.SelectedItem is VariableNode { IsWatch: true } watch)
            {
                watches.Remove(watch.Name);
                variables.Remove(watch);
                e.Handled = true;
            }
        };
        evaluate.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            string expression = evaluate.Text?.Trim() ?? "";
            if (expression.Length == 0) return;
            if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)) AddWatch(expression);
            else _ = EvaluateAsync(expression);
        };

        DockPanel.SetDock(threadList, Dock.Top);
        DockPanel.SetDock(status, Dock.Top);
        DockPanel left = new() { Children = { threadList, status, frameList } };
        DockPanel.SetDock(evaluate, Dock.Top);
        DockPanel right = new() { Children = { evaluate, variableTree } };
        Grid.SetColumn(right, 2);
        threadsAndVariables = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1*,1,2*"),
            Children = { left, new HairlineDivider(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 }, right },
        };

        variablesTab.Click += (_, _) => ShowTab(console: false);
        consoleTab.Click += (_, _) => ShowTab(console: true);
        Console.Started += (_, _) => ShowTab(console: true);
        StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(12, 0, 0, 0), Children = { variablesTab, consoleTab } };
        DockPanel header = new() { Margin = new(4, 2), Children = { tools, tabs } };
        DockPanel.SetDock(tools, Dock.Left);
        DockPanel.SetDock(header, Dock.Top);
        Content = new DockPanel { Children = { header, body } };
        ShowTab(console: false);
    }

    /// <summary>Where the program runs while it is debugged.</summary>
    public ProgramConsole Console { get; } = new();

    public string? Status { get => status.Text; set => status.Text = value; }

    /// <summary>Whether the Console tab is the one shown.</summary>
    public bool ShowsConsole => consoleTab.IsChecked == true;

    /// <summary>The chosen thread's frames as listed, top first.</summary>
    public IReadOnlyList<string> Frames => [.. frames.Select(frame => frame.Display)];

    /// <summary>The variables tree's top rows, watches first, as <c>name = value</c>.</summary>
    public IReadOnlyList<string> Variables => [.. variables.Select(node => node.ToString())];

    public IReadOnlyList<VariableNode> VariableNodes => variables;

    public IReadOnlyList<JvmDebugThread> Threads => threads;

    public IReadOnlyList<string> Watches => watches;

    public TextBox EvaluateBox => evaluate;

    /// <summary>A frame chosen in the list: the window opens its source at its line.</summary>
    public event Action<JvmDebugFrame>? FrameChosen;

    /// <summary>The session's buttons, at the toolbar's start.</summary>
    public void SetTools(IEnumerable<Control> buttons)
    {
        tools.Children.Clear();
        foreach (Control button in buttons) tools.Children.Add(button);
    }

    public void ShowTab(bool console)
    {
        variablesTab.IsChecked = !console;
        consoleTab.IsChecked = console;
        body.Content = console ? Console : threadsAndVariables;
    }

    /// <summary>The session that is debugged now; its threads and frames are asked for as they are chosen.</summary>
    public void Attach(JvmInteractiveDebugger session)
    {
        debugger = session;
        Clear();
    }

    public void Detach(JvmInteractiveDebugger session)
    {
        if (debugger != session) return;
        debugger = null;
        Clear();
    }

    public void Reset()
    {
        Status = "";
        Clear();
    }

    /// <summary>The target runs again: what was shown of it is gone, its frames and objects with it.</summary>
    public void ShowRunning()
    {
        version++;
        Status = "Running";
        Clear();
    }

    private void Clear()
    {
        version++;
        showing = true;
        try
        {
            threads.Clear();
            frames.Clear();
            variables.Clear();
        }
        finally { showing = false; }
    }

    /// <summary>A stop: its thread chosen, its frames listed and the top frame's variables shown, with the watches.</summary>
    public void ShowPause(JvmDebugPause pause)
    {
        int shown = ++version;
        showing = true;
        try
        {
            ShowTab(console: false);
            Status = pause.Reason switch
            {
                "breakpoint" => "Paused at a breakpoint",
                "step" => "Paused after a step",
                "pause" => "Paused",
                _ => "Paused",
            };
            threads.Clear();
            foreach (JvmDebugThread thread in pause.Threads) threads.Add(thread);
            threadList.SelectedItem = threads.FirstOrDefault(thread => thread.Id == pause.ThreadId);
            frames.Clear();
            foreach (JvmDebugFrame frame in pause.Frames) frames.Add(frame);
            frameList.SelectedIndex = frames.Count > 0 ? 0 : -1;
        }
        finally { showing = false; }
        _ = ShowVariablesAsync(shown, pause.Frames.FirstOrDefault(), pause.Variables);
    }

    private async Task ShowThreadAsync(JvmDebugThread thread)
    {
        if (debugger is not { } session) return;
        int shown = ++version;
        session.CurrentThread = thread.Id;
        try
        {
            IReadOnlyList<JvmDebugFrame> stack = await session.FramesAsync(thread.Id);
            if (shown != version) return;
            showing = true;
            try
            {
                frames.Clear();
                foreach (JvmDebugFrame frame in stack) frames.Add(frame);
                frameList.SelectedIndex = frames.Count > 0 ? 0 : -1;
            }
            finally { showing = false; }
            if (stack.FirstOrDefault() is { } top) await ShowFrameAsync(top, open: true);
            else
            {
                variables.Clear();
                Status = $"{thread.Name} is not suspended";
            }
        }
        catch (Exception error) when (error is DapException or InvalidOperationException or IOException or OperationCanceledException)
        {
            Status = error.Message;
        }
    }

    private async Task ShowFrameAsync(JvmDebugFrame frame, bool open)
    {
        if (debugger is not { } session) return;
        int shown = ++version;
        if (open) FrameChosen?.Invoke(frame);
        try
        {
            IReadOnlyList<JvmDebugVariable> locals = await session.VariablesAsync(frame.Id);
            if (shown == version) await ShowVariablesAsync(shown, frame, locals);
        }
        catch (Exception error) when (error is DapException or InvalidOperationException or IOException or OperationCanceledException)
        {
            Status = error.Message;
        }
    }

    private async Task ShowVariablesAsync(int shown, JvmDebugFrame? frame, IReadOnlyList<JvmDebugVariable> locals)
    {
        Func<int, Task<IReadOnlyList<JvmDebugVariable>>>? load = debugger is { } session ? session.VariablesAsync : null;
        List<VariableNode> rows = [];
        foreach (string watch in watches.ToArray())
            rows.Add(await WatchAsync(watch, frame));
        if (shown != version) return;
        variables.Clear();
        foreach (VariableNode row in rows) variables.Add(row);
        foreach (JvmDebugVariable local in locals)
            variables.Add(new VariableNode(local.Name, local.Value, local.Type, local.Reference, load));
    }

    private async Task<VariableNode> WatchAsync(string expression, JvmDebugFrame? frame)
    {
        if (debugger is not { } session || frame is null)
            return new VariableNode(expression, "Cannot evaluate: the program is running", "", 0, null, isWatch: true);
        try
        {
            JvmDebugVariable result = await session.EvaluateAsync(expression, frame.Id);
            return new VariableNode(expression, result.Value, result.Type, result.Reference, session.VariablesAsync, isWatch: true);
        }
        catch (Exception error) when (error is DapException or InvalidOperationException or IOException or OperationCanceledException)
        {
            return new VariableNode(expression, $"Cannot evaluate: {error.Message}", "", 0, null, isWatch: true);
        }
    }

    /// <summary>Adds a watch, evaluated now in the chosen frame and again at every stop.</summary>
    public void AddWatch(string expression)
    {
        if (watches.Contains(expression)) return;
        watches.Add(expression);
        evaluate.Text = "";
        _ = AddWatchAsync(expression);
    }

    private async Task AddWatchAsync(string expression)
    {
        int shown = version;
        VariableNode row = await WatchAsync(expression, frameList.SelectedItem as JvmDebugFrame);
        if (shown != version || !watches.Contains(expression)) return;
        variables.Insert(variables.Count(node => node.IsWatch), row);
    }

    /// <summary>Evaluates an expression in the chosen frame: its value shown as the tree's first row, which opens as any.</summary>
    public async Task EvaluateAsync(string expression)
    {
        if (debugger is not { } session || frameList.SelectedItem is not JvmDebugFrame frame)
        {
            Status = "Expressions are evaluated while the program is paused.";
            return;
        }
        int shown = version;
        VariableNode row;
        try
        {
            JvmDebugVariable result = await session.EvaluateAsync(expression, frame.Id);
            row = new VariableNode(expression, result.Value, result.Type, result.Reference, session.VariablesAsync);
            // An assignment or a call may have changed the frame's variables and the watches.
            if (shown != version) return;
            shown = ++version;
            IReadOnlyList<JvmDebugVariable> locals = await session.VariablesAsync(frame.Id);
            if (shown != version) return;
            evaluated = null;
            await ShowVariablesAsync(shown, frame, locals);
        }
        catch (Exception error) when (error is DapException or InvalidOperationException or IOException or OperationCanceledException)
        {
            row = new VariableNode(expression, $"Cannot evaluate: {error.Message}", "", 0, null);
        }
        if (shown != version) return;
        if (evaluated is not null) variables.Remove(evaluated);
        evaluated = row;
        variables.Insert(variables.Count(node => node.IsWatch), row);
        variableTree.SelectedItem = row;
    }

    private VariableNode? evaluated;

    private static Control Row(VariableNode node)
    {
        TextBlock name = new() { Text = node.Name, VerticalAlignment = VerticalAlignment.Center };
        name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable(node.IsWatch ? TokenKeys.Editor_Info : TokenKeys.Editor_Text));
        TextBlock value = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        value.Bind(TextBlock.TextProperty, new Binding(nameof(VariableNode.Value)) { Source = node });
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (node.Name.Length > 0)
        {
            PathIcon icon = new() { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
            icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(node.IsWatch ? IconKeys.Evaluate : IconKeys.Variable));
            row.Children.Add(icon);
            row.Children.Add(name);
            row.Children.Add(new TextBlock { Text = "=", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        }
        else value.Opacity = 0.6;
        row.Children.Add(value);
        if (node.Type.Length > 0) ToolTip.SetTip(row, node.Type);
        return row;
    }

    private static ToggleButton Tab(string title) => new()
    {
        Content = title,
        Padding = new Thickness(10, 3),
        Focusable = false,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
