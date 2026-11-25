using Avalonia;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Workspace;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>The debugger's values in the editor, as IntelliJ shows them while a program is paused: a card with the value
/// of the expression the mouse rests on (read without side effects), Quick Evaluate Expression's card at the caret or
/// the selection, Evaluate Expression putting either in the Debug window's box, Add to Watches, and inline values after
/// the lines of the stopped method.</summary>
public sealed partial class EditorWindow
{
    private ValueCard? valueCard;
    private TextDocument? annotated;
    private int hovers;

    /// <summary>The card a hover or Quick Evaluate shows: for tests.</summary>
    public ValueCard ValueCard => valueCard ??= NewValueCard();

    private ValueCard NewValueCard()
    {
        ValueCard card = new(Editor);
        card.View.SetValueRequested += (node, text) => UnawaitedWork.Observe(DebugTool.SetValueAsync(node, text));
        card.View.WatchRequested += expression =>
        {
            DebugTool.AddWatch(expression);
            ToolDock.Show(ToolWindowIds.Debug);
        };
        card.View.EvaluateRequested += EvaluateInDebugWindow;
        return card;
    }

    private void FollowDebugValues()
    {
        DebugTool.ValuesShown += ShowInlineValues;
        DebugTool.Cleared += ForgetDebugValues;
        // A hint's own card says what is under the pointer on a hint (EditorWindow.HintCard).
        Editor.Hovered += (_, e) =>
        {
            if (e.Hint is null) UnawaitedWork.Observe(HoverValueAsync(e.Offset));
        };
        Editor.HoverEnded += (_, _) =>
        {
            int hover = ++hovers;
            // A moment to reach the card from the word.
            DispatcherTimer.RunOnce(() =>
            {
                if (hover == hovers && valueCard is { IsOpen: true, IsSticky: false, IsPointerOver: false }) valueCard.Close();
            }, TimeSpan.FromMilliseconds(400));
        };
    }

    private void AddDebugValueActions()
    {
        Actions.Add(new EditorAction(AppActions.Evaluate, "Evaluate Expression…", () => Session?.IsPaused == true,
            () => EvaluateInDebugWindow(ExpressionAtCaret() ?? "")));
        Actions.Add(new EditorAction(AppActions.QuickEvaluate, "Quick Evaluate Expression",
            () => Session?.IsPaused == true && ExpressionAtCaret() is not null, () => UnawaitedWork.Observe(QuickEvaluateAsync())));
        Actions.Add(new EditorAction(AppActions.AddToWatches, "Add to Watches",
            () => Session?.IsDebugging == true && ExpressionAtCaret() is not null, () =>
            {
                if (ExpressionAtCaret() is not { } expression) return;
                DebugTool.AddWatch(expression);
                ToolDock.Show(ToolWindowIds.Debug);
            }));
    }

    private void EvaluateInDebugWindow(string expression)
    {
        ToolDock.Show(ToolWindowIds.Debug);
        DebugTool.EvaluateIn(expression);
    }

    /// <summary>The selection, else the field access chain at the caret.</summary>
    private string? ExpressionAtCaret()
    {
        if (!Editor.IsVisible || Editor.Document is not { } document) return null;
        Selection primary = Editor.Selections.Primary;
        TextSnapshot text = document.Snapshot;
        if (!primary.IsEmpty) return text.GetText(primary.Start, primary.Length).Trim() is { Length: > 0 } selected ? selected : null;
        return JavaDebugText.ExpressionAt(text, primary.Caret);
    }

    /// <summary>Whether the editor shows the source of the frame expressions are evaluated in.</summary>
    private JvmDebugFrame? FrameShown() =>
        DebugTool.SelectedFrame is { Path: { } path } frame && SourcePath is { } shown && PathNames.Same(path, shown) ? frame : null;

    private async Task HoverValueAsync(int offset)
    {
        hovers++;
        if (Session?.IsPaused != true || DebugTool.Session is not { } session || FrameShown() is not { } frame) return;
        if (valueCard is { IsOpen: true, IsSticky: true }) return;
        TextSnapshot text = Editor.Document.Snapshot;
        if (JavaDebugText.ExpressionAt(text, offset) is not { } expression) return;
        int hover = hovers;
        try
        {
            JvmDebugVariable value = await session.EvaluateAsync(expression, frame.Id, "hover");
            if (hover != hovers || Editor.Document.Snapshot != text || DebugTool.SelectedFrame != frame) return;
            ShowValue(session, expression, value, JavaDebugText.ExpressionStart(text, offset), sticky: false);
        }
        catch (Exception error) when (error is DapException or InvalidOperationException or IOException or OperationCanceledException)
        {
            // Nothing to show where the hover reads no value.
        }
    }

    /// <summary>Quick Evaluate Expression: the selection, else the expression at the caret, evaluated as the Evaluate box
    /// does (calls run), its value a card at the expression; one that does not evaluate shows why.</summary>
    private async Task QuickEvaluateAsync()
    {
        if (DebugTool.Session is not { } session || DebugTool.SelectedFrame is not { } frame || ExpressionAtCaret() is not { } expression) return;
        TextSnapshot text = Editor.Document.Snapshot;
        Selection primary = Editor.Selections.Primary;
        int start = primary.IsEmpty ? Math.Max(0, JavaDebugText.ExpressionStart(text, primary.Caret)) : primary.Start;
        JvmDebugVariable value;
        try
        {
            value = await session.EvaluateAsync(expression, frame.Id);
        }
        catch (Exception error) when (error is DapException or InvalidOperationException or IOException or OperationCanceledException)
        {
            value = new JvmDebugVariable(expression, $"Cannot evaluate: {error.Message}", "", 0);
        }
        if (Editor.Document.Snapshot != text || DebugTool.SelectedFrame != frame) return;
        ShowValue(session, expression, value, start, sticky: true);
    }

    private void ShowValue(JvmInteractiveDebugger session, string expression, JvmDebugVariable value, int start, bool sticky)
    {
        Point at = Editor.PointOf(Math.Max(0, start));
        VariableNode node = value.Value.StartsWith("Cannot ", StringComparison.Ordinal)
            ? new VariableNode(expression, value.Value, "", 0, null)
            : new VariableNode(expression, value.Value, value.Type, value.Reference, session.VariablesAsync, expression: expression);
        ValueCard.Show(node, new Point(at.X, at.Y + Editor.LineHeight + 2), sticky);
    }

    /// <summary>The frame's values after the lines of its method that name them, in the file shown for it.</summary>
    private void ShowInlineValues(JvmDebugFrame frame, IReadOnlyList<JvmDebugVariable> locals)
    {
        ForgetInlineValues();
        if (frame.Path is not { } path || frame.Line < 1 || !documents.TryGetValue(Path.GetFullPath(path), out OpenDocument? source)) return;
        if (Decorated(source.Document) is not { } decorations) return;
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (JvmDebugVariable local in locals)
            if (local.Name != "this") values[local.Name] = local.Value;
        decorations.SetLineAnnotations(JavaDebugText.InlineValues(source.Document.Snapshot, frame.Line - 1, values));
        annotated = source.Document;
    }

    private void ForgetInlineValues()
    {
        if (annotated is not null) Decorated(annotated)?.SetLineAnnotations([]);
        annotated = null;
    }

    /// <summary>The program runs on: the values read at the stop go.</summary>
    private void ForgetDebugValues()
    {
        hovers++;
        valueCard?.Close();
        ForgetInlineValues();
    }
}
