using Avalonia;
using Passenger;
using Passenger.Editor;

namespace WayfarerWanderer.App;

public sealed class BreakpointColumn(Action<int> clickLine) : IGutterColumn
{
    private readonly HashSet<int> lines = [];
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
        foreach (var line in frame.Lines)
        {
            if (!IsMarked(line.Line))
                continue;

            var center = new Point(frame.Left + frame.Width / 2, line.Top + frame.LineHeight / 2);
            double radius = Math.Min(6, frame.LineHeight / 3);
            frame.Context.DrawEllipse(frame.Brush(TokenKeys.Editor_Breakpoint), null, center, radius, radius);
        }
    }

    public GutterAnswer Press(GutterPress press)
    {
        if (!press.OnStrip || press.Click.Context)
            return GutterAnswer.Passed;

        clickLine(press.Click.Line);
        return GutterAnswer.Taken;
    }
}
