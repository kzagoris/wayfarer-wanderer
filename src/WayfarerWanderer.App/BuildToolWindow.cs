using Avalonia.Controls;
using Passenger.Editor;
using System.Collections.ObjectModel;

namespace WayfarerWanderer.App;

public sealed class BuildToolWindow : UserControl
{
    private readonly ListBox lines;

    public ObservableCollection<string> Output { get; } = [];

    public BuildToolWindow()
    {
        lines = new ListBox { ItemsSource = Output, [Avalonia.Automation.AutomationProperties.NameProperty] = "Build output" };
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
        foreach (string part in line.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            Output.Add(part);
        }
        if (Output.Count > 0)
        {
            lines.ScrollIntoView(Output.Count - 1);
        }
    }
}
