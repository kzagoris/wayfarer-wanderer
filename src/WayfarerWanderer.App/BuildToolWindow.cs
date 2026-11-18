using Avalonia.Controls;
using Avalonia.Layout;
using System.Collections.ObjectModel;

namespace WayfarerWanderer.App;

public sealed class BuildToolWindow : UserControl
{
    public ObservableCollection<string> Output { get; } = [];

    public BuildToolWindow()
    {
        Content = new DockPanel
        {
            Children =
            {
                Header("Build"),
                new ListBox
                {
                    ItemsSource = Output,
                    FontFamily = "monospace"
                }
            }
        };
    }

    public void Reset()
    {
        Output.Clear();
    }

    public void Append(string line)
    {
        Output.Add(line);
    }

    private static Control Header(string text)
    {
        TextBlock result = new() { Text = text, Margin = new(8, 6) };
        DockPanel.SetDock(result, Dock.Top);
        return result;
    }
}
