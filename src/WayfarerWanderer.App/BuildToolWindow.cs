using Avalonia.Controls;
using Passenger.Editor;
using System.Collections.ObjectModel;

namespace WayfarerWanderer.App;

public sealed class BuildToolWindow : UserControl
{
    public ObservableCollection<string> Output { get; } = [];

    public BuildToolWindow()
    {
        ListBox lines = new() { ItemsSource = Output };
        // The build's output in the code font in force, as the editor shows code, following each change in Settings.
        CodeFonts.Follow(lines, FontFamilyProperty);
        Content = lines;
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
