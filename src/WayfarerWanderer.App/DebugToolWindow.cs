using System.Collections.ObjectModel;
using Avalonia.Controls;

namespace WayfarerWanderer.App;

public sealed class DebugToolWindow : UserControl
{
    private readonly TextBlock status = new() { Margin = new(8, 4) };
    public ObservableCollection<string> Frames { get; } = [];
    public ObservableCollection<string> Variables { get; } = [];
    public string? Status { get => status.Text; set => status.Text = value; }

    public DebugToolWindow()
    {
        DockPanel.SetDock(status, Dock.Top);
        Content = new DockPanel
        {
            Children =
            {
                status,
                new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("1* 1*"),
                    Children = { Column("Call stack", Frames, 0), Column("Locals", Variables, 1) }
                }
            }
        };
    }

    public void Reset()
    {
        Status = "";
        Frames.Clear();
        Variables.Clear();
    }

    private static Control Column(string title, ObservableCollection<string> items, int column)
    {
        TextBlock header = new() { Text = title, Margin = new(8, 2) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel panel = new() { Children = { header, new ListBox { ItemsSource = items } } };
        Grid.SetColumn(panel, column);
        return panel;
    }
}
