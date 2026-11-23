using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.Dap;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>The Debug tool window, as IntelliJ's: the session's toolbar (Rerun, Stop, Resume, Pause, the steps), then two
/// tabs. Threads &amp; Variables lists the threads, the chosen one's frames and the chosen frame's variables, which open
/// into fields and elements and take Set Value, under the box that evaluates Java in the frame: an expression or a code
/// fragment of statements (Shift+Enter starts a new line), with completion (after a dot, or Ctrl+Space) and the
/// expressions evaluated before (Up and Down); Ctrl+Shift+Enter adds it as a watch. Console is where the program runs: its
/// output, and its input typed there.</summary>
public sealed class DebugToolWindow : UserControl
{
    /// <summary>The expressions evaluated, oldest first, for every session of the run.</summary>
    private static readonly List<string> History = [];
    private const int HistoryKept = 50;

    private readonly TextBlock status = new() { Margin = new(8, 4), TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.75 };
    private readonly ObservableCollection<JvmDebugThread> threads = [];
    private readonly ObservableCollection<JvmDebugFrame> frames = [];
    private readonly ObservableCollection<VariableNode> variables = [];
    private readonly List<string> watches = [];
    private readonly ComboBox threadList = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(4, 4, 4, 2), [Avalonia.Automation.AutomationProperties.NameProperty] = "Threads" };
    private readonly ListBox frameList = new() { [Avalonia.Automation.AutomationProperties.NameProperty] = "Frames" };
    private readonly VariablesView variableView;
    private readonly TextBox evaluate = new()
    {
        PlaceholderText = "Evaluate expression (Enter) or add a watch (Ctrl+Shift+Enter)",
        [Avalonia.Automation.AutomationProperties.NameProperty] = "Evaluate expression",
        Margin = new(4, 4, 4, 2),
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        MaxHeight = 160,
    };
    private readonly CompletionPopup completion;
    private readonly StackPanel tools = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton variablesTab = Tab("Threads & Variables");
    private readonly ToggleButton consoleTab = Tab("Console");
    private readonly ContentControl body = new();
    private readonly Control threadsAndVariables;
    private IReadOnlyList<JvmDebugCompletion> completions = [];
    private JvmInteractiveDebugger? debugger;
    private VariableNode? evaluated;
    private bool showing;
    private int version;
    private int historyAt = -1;
    private int asked;

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
        variableView = new VariablesView(variables);
        variableView.SetValueRequested += (node, text) => _ = SetValueAsync(node, text);
        variableView.WatchRequested += AddWatch;
        variableView.EvaluateRequested += EvaluateIn;
        variableView.RemoveRequested += RemoveWatch;

        completion = new CompletionPopup(evaluate);
        completion.Chosen += Complete;
        evaluate.AddHandler(KeyDownEvent, EvaluateKeyDown, RoutingStrategies.Tunnel);
        evaluate.TextChanged += (_, _) =>
        {
            if (completion.IsOpen) _ = CompleteAsync(explicitly: false);
        };
        evaluate.TextInput += (_, e) =>
        {
            if (e.Text == ".") _ = CompleteAsync(explicitly: false);
        };
        evaluate.LostFocus += (_, _) => completion.Close();

        DockPanel.SetDock(threadList, Dock.Top);
        DockPanel.SetDock(status, Dock.Top);
        DockPanel left = new() { Children = { threadList, status, frameList } };
        DockPanel.SetDock(evaluate, Dock.Top);
        DockPanel right = new() { Children = { evaluate, variableView } };
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

    public VariablesView VariablesView => variableView;

    public IReadOnlyList<JvmDebugThread> Threads => threads;

    public IReadOnlyList<string> Watches => watches;

    public TextBox EvaluateBox => evaluate;

    public CompletionPopup EvaluateCompletion => completion;

    /// <summary>The expressions evaluated before, oldest first.</summary>
    public static IReadOnlyList<string> EvaluatedBefore => History;

    /// <summary>The session debugged now, while there is one.</summary>
    public JvmInteractiveDebugger? Session => debugger;

    /// <summary>The frame chosen in the list: where expressions are evaluated.</summary>
    public JvmDebugFrame? SelectedFrame => frameList.SelectedItem as JvmDebugFrame;

    /// <summary>A frame chosen in the list: the window opens its source at its line.</summary>
    public event Action<JvmDebugFrame>? FrameChosen;

    /// <summary>A frame's variables were shown: the editor writes them after the lines that name them.</summary>
    public event Action<JvmDebugFrame, IReadOnlyList<JvmDebugVariable>>? ValuesShown;

    /// <summary>What was shown of a stop is gone: the program runs on, or the session ended.</summary>
    public event Action? Cleared;

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
        Cleared?.Invoke();
        version++;
        showing = true;
        completion.Close();
        try
        {
            threads.Clear();
            frames.Clear();
            variables.Clear();
            evaluated = null;
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
        evaluated = null;
        foreach (VariableNode row in rows) variables.Add(row);
        foreach (JvmDebugVariable local in locals)
            variables.Add(new VariableNode(local.Name, local.Value, local.Type, local.Reference, load, owner: frame?.Id ?? 0));
        if (frame is not null) ValuesShown?.Invoke(frame, locals);
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
        expression = expression.Trim();
        if (expression.Length == 0 || watches.Contains(expression)) return;
        watches.Add(expression);
        if (evaluate.Text?.Trim() == expression) evaluate.Text = "";
        _ = AddWatchAsync(expression);
    }

    private void RemoveWatch(VariableNode watch)
    {
        watches.Remove(watch.Name);
        variables.Remove(watch);
    }

    private async Task AddWatchAsync(string expression)
    {
        int shown = version;
        VariableNode row = await WatchAsync(expression, SelectedFrame);
        if (shown != version || !watches.Contains(expression)) return;
        variables.Insert(variables.Count(node => node.IsWatch), row);
    }

    /// <summary>Puts Java in the evaluate box, ready to be evaluated: Evaluate Expression from the editor or a row.</summary>
    public void EvaluateIn(string text)
    {
        ShowTab(console: false);
        evaluate.Text = text;
        evaluate.CaretIndex = text.Length;
        evaluate.Focus();
        evaluate.SelectAll();
    }

    /// <summary>Evaluates an expression or a code fragment in the chosen frame: its value shown as the tree's first row,
    /// which opens as any; the frame's variables and the watches are read again, as what was evaluated may have changed
    /// them.</summary>
    public async Task EvaluateAsync(string expression)
    {
        Remember(expression);
        if (debugger is not { } session || SelectedFrame is not { } frame)
        {
            Status = "Expressions are evaluated while the program is paused.";
            return;
        }
        int shown = version;
        VariableNode row;
        string title = expression.ReplaceLineEndings(" ");
        try
        {
            JvmDebugVariable result = await session.EvaluateAsync(expression, frame.Id);
            row = new VariableNode(title, result.Value, result.Type, result.Reference, session.VariablesAsync, expression: expression);
            if (!await RefreshAsync(shown, frame)) return;
            shown = version;
        }
        catch (Exception error) when (error is DapException or InvalidOperationException or IOException or OperationCanceledException)
        {
            row = new VariableNode(title, $"Cannot evaluate: {error.Message}", "", 0, null);
        }
        if (shown != version) return;
        if (evaluated is not null) variables.Remove(evaluated);
        evaluated = row;
        variables.Insert(variables.Count(node => node.IsWatch), row);
        variableView.Selected = row;
    }

    /// <summary>Set Value: a frame's local, a field or an element through its owner, a watch or an expression by assigning
    /// to it. The frame's variables and the watches are read again, for they may have changed with it.</summary>
    public async Task SetValueAsync(VariableNode node, string text)
    {
        if (debugger is not { } session || SelectedFrame is not { } frame)
        {
            Status = "Values are set while the program is paused.";
            return;
        }
        int shown = version;
        try
        {
            JvmDebugVariable result = node.Owner > 0
                ? await session.SetVariableAsync(node.Owner, node.Name, text)
                : await session.EvaluateAsync($"{node.Expression} = {text}", frame.Id);
            if (shown != version) return;
            node.Show(result.Value, result.Type, result.Reference);
            Status = $"{node.Name} = {result.Value}";
            await RefreshWatchesAsync(frame);
        }
        catch (Exception error) when (error is DapException or InvalidOperationException or IOException or OperationCanceledException)
        {
            Status = $"Cannot set {node.Name}: {error.Message}";
        }
    }

    /// <summary>Reads the frame's variables and the watches again; false when the program moved on meanwhile.</summary>
    private async Task<bool> RefreshAsync(int shown, JvmDebugFrame frame)
    {
        if (debugger is not { } session || shown != version) return false;
        int now = ++version;
        IReadOnlyList<JvmDebugVariable> locals = await session.VariablesAsync(frame.Id);
        if (now != version) return false;
        await ShowVariablesAsync(now, frame, locals);
        return now == version;
    }

    /// <summary>The watches and the frame's own variables shown again in place, keeping what was open open.</summary>
    private async Task RefreshWatchesAsync(JvmDebugFrame frame)
    {
        if (debugger is not { } session) return;
        int shown = version;
        foreach (VariableNode watch in variables.Where(node => node.IsWatch).ToArray())
        {
            VariableNode again = await WatchAsync(watch.Name, frame);
            if (shown != version) return;
            watch.Show(again.Value, again.Type, again.Reference);
        }
        try
        {
            IReadOnlyList<JvmDebugVariable> locals = await session.VariablesAsync(frame.Id);
            if (shown != version) return;
            foreach (JvmDebugVariable local in locals)
                if (variables.FirstOrDefault(node => !node.IsWatch && node.Owner == frame.Id && node.Name == local.Name) is { } row && row.Value != local.Value)
                    row.Show(local.Value, local.Type, local.Reference);
            ValuesShown?.Invoke(frame, locals);
        }
        catch (Exception error) when (error is DapException or InvalidOperationException or IOException or OperationCanceledException)
        {
            Status = error.Message;
        }
    }

    // ------------------------------------------------------------------ the evaluate box

    private void EvaluateKeyDown(object? sender, KeyEventArgs e)
    {
        if (completion.TakeKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }
        string text = evaluate.Text ?? "";
        switch (e.Key)
        {
            case Key.Space when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                _ = CompleteAsync(explicitly: true);
                return;
            case Key.Enter when e.KeyModifiers == KeyModifiers.Shift:
                // A code fragment: a new line, as the box takes Enter itself.
                return;
            case Key.Enter:
                e.Handled = true;
                string expression = text.Trim();
                if (expression.Length == 0) return;
                if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)) AddWatch(expression);
                else _ = EvaluateAsync(expression);
                return;
            case Key.Up when e.KeyModifiers == KeyModifiers.None && !text[..Math.Min(evaluate.CaretIndex, text.Length)].Contains('\n'):
                e.Handled = Recall(-1);
                return;
            case Key.Down when e.KeyModifiers == KeyModifiers.None && !text[Math.Min(evaluate.CaretIndex, text.Length)..].Contains('\n'):
                e.Handled = Recall(1);
                return;
            case Key.Escape when completion.IsOpen:
                completion.Close();
                e.Handled = true;
                return;
        }
    }

    private static void Remember(string expression)
    {
        History.Remove(expression);
        History.Add(expression);
        if (History.Count > HistoryKept) History.RemoveAt(0);
    }

    /// <summary>Up and Down: the expression evaluated before, or after, the one shown; past the newest, an empty box.</summary>
    private bool Recall(int by)
    {
        if (History.Count == 0) return false;
        int at = historyAt < 0 || historyAt >= History.Count || History[historyAt] != evaluate.Text ? History.Count : historyAt;
        at += by;
        if (at < 0) return true;
        if (at >= History.Count)
        {
            historyAt = -1;
            evaluate.Text = "";
            return true;
        }
        historyAt = at;
        evaluate.Text = History[at];
        evaluate.CaretIndex = History[at].Length;
        return true;
    }

    /// <summary>Asks for completions at the caret: the members after a dot, else the names in scope. One asked for
    /// without a key (a dot typed, the text changed while the list is open) closes the list when nothing matches.</summary>
    private async Task CompleteAsync(bool explicitly)
    {
        if (debugger is not { } session || SelectedFrame is not { } frame)
        {
            completion.Close();
            return;
        }
        int ask = ++asked;
        string text = evaluate.Text ?? "";
        int caret = Math.Min(evaluate.CaretIndex, text.Length);
        try
        {
            IReadOnlyList<JvmDebugCompletion> found = await session.CompletionsAsync(text, caret, frame.Id);
            if (ask != asked || evaluate.Text != text) return;
            if (found.Count == 0 && !(explicitly && completion.IsOpen))
            {
                completion.Close();
                return;
            }
            completions = found;
            List<CompletionChoice> choices = [];
            for (int i = 0; i < found.Count; i++)
            {
                JvmDebugCompletion item = found[i];
                choices.Add(new CompletionChoice(default(JsonElement), item.Label, item.Detail, item.Label, i.ToString("D4"), Kind(item.Kind), i));
            }
            (Point top, double height) = CaretPlace(caret);
            completion.Show(choices, top, height);
        }
        catch (Exception error) when (error is DapException or InvalidOperationException or IOException or OperationCanceledException)
        {
            completion.Close();
        }
    }

    private static int Kind(string kind) => kind switch
    {
        "method" or "function" => 2,
        "constructor" => 4,
        "field" or "property" => 5,
        "variable" => 6,
        "class" => 7,
        "interface" => 8,
        "keyword" => 14,
        _ => 1,
    };

    private void Complete(CompletionChoice choice)
    {
        completion.Close();
        if (choice.Index < 0 || choice.Index >= completions.Count) return;
        JvmDebugCompletion item = completions[choice.Index];
        string text = evaluate.Text ?? "";
        int start = Math.Clamp(item.Start, 0, text.Length);
        int end = Math.Clamp(item.Start + item.Length, start, text.Length);
        // The rest of the word under the caret goes too, as IntelliJ's Tab replaces it.
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '$')) end++;
        evaluate.Text = text[..start] + item.Text + text[end..];
        evaluate.CaretIndex = start + item.Text.Length;
        evaluate.Focus();
    }

    /// <summary>The caret's top in the box, and a line's height: where the completion list goes.</summary>
    private (Point Top, double Height) CaretPlace(int caret)
    {
        if (evaluate.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault() is { } presenter)
        {
            Rect box = presenter.TextLayout.HitTestTextPosition(caret);
            if (presenter.TranslatePoint(box.TopLeft, evaluate) is { } top) return (top, Math.Max(box.Height, 16));
        }
        return (new Point(6, 4), Math.Max(evaluate.Bounds.Height - 4, 16));
    }

    private static ToggleButton Tab(string title) => new()
    {
        Content = title,
        Padding = new Thickness(10, 3),
        Focusable = false,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
