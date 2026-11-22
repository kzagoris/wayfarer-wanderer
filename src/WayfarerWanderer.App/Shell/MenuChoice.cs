// From Passenger 2044e49b src/Passenger.App/MenuChoice.cs, MIT (notices/Passenger/LICENSE): Holding cut.
using Avalonia.Controls;

namespace WayfarerWanderer.App.Shell;

/// <summary>A menu's item for one value among several (an encoding, a line separator, an indentation): the current value
/// checked and still enabled, never greyed, so it reads as the one in force; choosing it again changes nothing.
/// Shell.axaml draws the check as a ✓.</summary>
public static class MenuChoice
{
    public static MenuItem Of(object header, bool current, Action choose)
    {
        MenuItem item = new() { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = current };
        item.Click += (_, _) =>
        {
            if (!current)
            {
                choose();
            }
        };
        return item;
    }
}
