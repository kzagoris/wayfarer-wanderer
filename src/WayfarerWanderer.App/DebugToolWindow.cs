using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Layout;

namespace WayfarerWanderer.App;

public sealed class DebugToolWindow : UserControl
{
    public ObservableCollection<string> Frames { get; } = [];
    public ObservableCollection<string> Variables { get; } = [];

    public DebugToolWindow()
    {
        Content = new DockPanel
        {
            Children =
            {
                Header("Debug"),
                new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("1* 1*"),
                    Children =
                    {
                        Column(new ListBox { ItemsSource = Frames }, 0),
                        Column(new ListBox { ItemsSource = Variables }, 1)
                    }
                }
            }
        };
    }

    public void Reset()
    {
        Frames.Clear();
        Variables.Clear();
    }

    private static Control Column(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static Control Header(string text)
    {
        TextBlock result = new() { Text = text, Margin = new(8, 6) };
        DockPanel.SetDock(result, Dock.Top);
        return result;
    }
}
