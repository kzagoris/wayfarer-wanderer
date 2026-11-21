using Avalonia;
using Passenger;
using Passenger.Editor;

namespace WayfarerWanderer.App;

public sealed class BreakpointColumn(Action<int> clickLine) : IGutterColumn
{
    private readonly Dictionary<string, HashSet<int>> sources = new(Passenger.Core.PathNames.Comparer);
    private HashSet<int> lines = [];
    private string? selectedPath;
    private string? executionPath;
    private int executionLine = -1;

    internal void Select(string path)
    {
        if (!sources.TryGetValue(path, out var selected))
            sources[path] = selected = [];
        lines = selected;
        selectedPath = path;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The line the debugger stopped on (0-based), marked by an arrow while its source shows; null takes it away.</summary>
    internal void MarkExecution(string? path, int line)
    {
        executionPath = path;
        executionLine = path is null ? -1 : line;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The line marked as the one stopped on in the source shown, or -1.</summary>
    public int ExecutionLine => executionPath is not null && selectedPath is not null &&
        Passenger.Core.PathNames.Same(executionPath, selectedPath) ? executionLine : -1;

    internal int[] DebugLines() => [.. lines.Order().Select(line => line + 1)];

    internal IReadOnlyDictionary<string, int[]> Snapshot() => sources
        .Where(pair => pair.Value.Count > 0)
        .ToDictionary(pair => pair.Key, pair => pair.Value.Order().Select(line => line + 1).ToArray(),
            Passenger.Core.PathNames.Comparer);

    internal void Reset()
    {
        sources.Clear();
        lines = [];
        selectedPath = executionPath = null;
        executionLine = -1;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;
    public bool IsMarked(int line) => lines.Contains(line);

    internal void Toggle(int line)
    {
        if (!lines.Remove(line))
            lines.Add(line);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public double Width(GutterFont font) => font.LineHeight;

    public void Draw(GutterFrame frame)
    {
        int execution = ExecutionLine;
        foreach (var line in frame.Lines)
        {
            var center = new Point(frame.Left + frame.Width / 2, line.Top + frame.LineHeight / 2);
            if (IsMarked(line.Line))
            {
                double radius = Math.Min(6, frame.LineHeight / 3);
                frame.Context.DrawEllipse(frame.Brush(TokenKeys.Editor_Breakpoint), null, center, radius, radius);
            }
            if (line.Line == execution)
            {
                // The stopped-on line's arrow, as IntelliJ draws it over the breakpoint.
                double half = Math.Min(5, frame.LineHeight / 3);
                var arrow = new Avalonia.Media.StreamGeometry();
                using (var context = arrow.Open())
                {
                    context.BeginFigure(new Point(center.X - half, center.Y - half * 0.7), true);
                    context.LineTo(new Point(center.X + 0.2 * half, center.Y - half * 0.7));
                    context.LineTo(new Point(center.X + half, center.Y));
                    context.LineTo(new Point(center.X + 0.2 * half, center.Y + half * 0.7));
                    context.LineTo(new Point(center.X - half, center.Y + half * 0.7));
                    context.EndFigure(true);
                }
                frame.Context.DrawGeometry(frame.Brush(TokenKeys.Editor_Caret), null, arrow);
            }
        }
    }

    /// <summary>The breakpoints' lines washed red, and the line the debugger stopped on blue over them, as IntelliJ shows them.</summary>
    public void Tint(LineTints tints)
    {
        foreach (int line in lines)
        {
            if (line >= tints.First && line <= tints.Last) tints.Line(line, tints.Brush(TokenKeys.Editor_BreakpointLine));
        }
        int execution = ExecutionLine;
        if (execution >= tints.First && execution <= tints.Last) tints.Line(execution, tints.Brush(TokenKeys.Editor_ExecutionLine));
    }

    public GutterAnswer Press(GutterPress press)
    {
        if (!press.OnStrip || press.Click.Context)
            return GutterAnswer.Passed;

        clickLine(press.Click.Line);
        return GutterAnswer.Taken;
    }
}
