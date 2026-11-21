using Avalonia.Controls;
using Avalonia.Layout;

namespace WayfarerWanderer.App;

/// <summary>The Run tool window, as IntelliJ's: Rerun and Stop down its left side, and the console the program runs in,
/// where its output shows and its input is typed.</summary>
public sealed class RunToolWindow : DockPanel
{
    private readonly StackPanel tools = new() { Orientation = Orientation.Vertical, Spacing = 2, Margin = new(2, 4) };

    public RunToolWindow()
    {
        SetDock(tools, Dock.Left);
        Children.Add(tools);
        Children.Add(Console);
    }

    public ProgramConsole Console { get; } = new();

    /// <summary>The run's buttons, down the window's left side.</summary>
    public void SetTools(IEnumerable<Control> buttons)
    {
        tools.Children.Clear();
        foreach (Control button in buttons) tools.Children.Add(button);
    }
}
