using Avalonia.Controls;
using System.Collections.ObjectModel;

namespace WayfarerWanderer.App;

public sealed class BuildToolWindow : UserControl
{
    public ObservableCollection<string> Output { get; } = [];

    public BuildToolWindow()
    {
        Content = new ListBox
        {
            ItemsSource = Output,
            FontFamily = "monospace"
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
}
