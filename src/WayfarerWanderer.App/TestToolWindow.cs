using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Layout;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

public sealed class TestToolWindow : UserControl
{
    public ObservableCollection<JunitCase> Cases { get; } = [];

    public TestToolWindow()
    {
        Content = new DockPanel
        {
            Children =
            {
                Header("Tests"),
                new ListBox
                {
                    ItemsSource = Cases,
                    DisplayMemberBinding = new Avalonia.Data.Binding(nameof(JunitCase.DisplayName))
                }
            }
        };
    }

    public void Reset()
    {
        Cases.Clear();
    }

    public void Show(TestRunResult result)
    {
        Cases.Clear();
        foreach (JunitCase test in result.Cases)
        {
            Cases.Add(test);
        }
    }

    private static Control Header(string text)
    {
        TextBlock result = new() { Text = text, Margin = new(8, 6) };
        DockPanel.SetDock(result, Dock.Top);
        return result;
    }
}
