// From Passenger 8a1df7e4 src/Passenger.App/CheckRows.cs, MIT (notices/Passenger/LICENSE): CheckChoice and the project's
// icon before a row's name (ProjectLabel) cut, Wayfarer offering no check rows of .NET projects or backing services.
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace WayfarerWanderer.App.Shell;

/// <summary>A choice a list of check rows offers (<see cref="CheckRows.Of"/>): its name, what it is, whether it is
/// checked, and whether it can be.</summary>
public interface ICheckChoice
{
    string Name { get; }

    string Detail { get; }

    bool CanChoose { get; }

    bool IsChosen { get; set; }
}

/// <summary>A row a person checks or clears, as tall as every list's row (<see cref="ListRow.Height"/>): its box at its
/// start, centred in it, then what it names, and what it is in the rest of the row's width, each cut short with an
/// ellipsis where it has no more, what it is first.</summary>
public static class CheckRows
{
    /// <summary>A list of check rows over its choices, one row a choice: Space checks or clears the chosen row, Enter does
    /// what <paramref name="enter"/> does where given (the dialog's default button), a click on a box checks its own.</summary>
    public static ListBox List(IEnumerable<ICheckChoice> choices, Action? enter = null)
    {
        ListBox list = new() { Classes = { StyleClasses.Tree }, ItemsSource = choices };
        ReusedRows.Use(list, Of);
        ListKeys.Act(list, new ListActs
        {
            Enter = enter,
            Keys = e =>
            {
                if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || list.SelectedItem is not ICheckChoice { CanChoose: true } choice)
                {
                    return false;
                }
                choice.IsChosen = !choice.IsChosen;
                return true;
            },
        });
        return list;
    }

    /// <summary>A row's box: centred in the row, as wide as its glyph.</summary>
    public static CheckBox Box() => new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };

    /// <summary>A list's row for whichever choice the list hands its container: its box, which follows the choice while
    /// shown and checks the one it shows, never taking the keys from the list; its name; and what it is, muted. One that
    /// cannot be chosen is greyed, its box with it.</summary>
    public static Control Of()
    {
        CheckBox check = Box();
        check.Focusable = false;
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock detail = new() { Classes = { StyleClasses.Muted, StyleClasses.Caption }, Margin = new Thickness(Gaps.Wide, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(check, Dock.Left);
        DockPanel.SetDock(name, Dock.Left);
        DockPanel row = new() { Height = ListRow.Height, Margin = new Thickness(Gaps.Small, 0), Children = { check, name, detail } };
        AutomationProperties.SetLabeledBy(row, name);
        bool setting = false;
        check.IsCheckedChanged += (_, _) =>
        {
            if (!setting && row.DataContext is ICheckChoice choice)
            {
                choice.IsChosen = check.IsChecked == true;
            }
        };
        return row.Showing<ICheckChoice>(choice =>
        {
            setting = true;
            check.IsChecked = choice.IsChosen;
            check.IsEnabled = choice.CanChoose;
            setting = false;
            name.Text = choice.Name;
            detail.Text = choice.Detail;
            ListRow.Dim(row, !choice.CanChoose);
        });
    }
}
