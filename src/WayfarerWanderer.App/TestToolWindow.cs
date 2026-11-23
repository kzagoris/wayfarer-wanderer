using System.Collections.ObjectModel;
using Avalonia.Controls;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

public sealed class TestToolWindow : UserControl
{
    public ObservableCollection<JunitCase> Cases { get; } = [];

    public TestToolWindow()
    {
        Content = new ListBox
        {
            ItemsSource = Cases,
            [Avalonia.Automation.AutomationProperties.NameProperty] = "Test results",
            DisplayMemberBinding = new Avalonia.Data.Binding(nameof(JunitCase.DisplayName))
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
}
