// From Passenger 2044e49b src/Passenger.App/ToolWindows/ListMenus.cs, MIT (notices/Passenger/LICENSE): only the placing of a
// menu the keys open, which the Explorer's row menu needs; the list menus Wayfarer has none of yet are cut.
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace WayfarerWanderer.App.Shell.ToolWindows;

/// <summary>Right-click menus on a tool window's list.</summary>
public static class ListMenus
{
    /// <summary>Places a menu the menu key (Shift+F10) opens under what has the keys (a list's chosen row), where one a
    /// right-click opens stands at the pointer. Gives what puts it back where it stood, run as it closes too.</summary>
    public static Action PlaceForKeys(ContextMenu menu, Control at)
    {
        (PlacementMode Placement, Control? Target) was = (menu.Placement, menu.PlacementTarget);
        bool back = false;
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.PlacementTarget = at;
        menu.Closed += Closed;
        return Back;

        void Closed(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Back();

        void Back()
        {
            if (!back)
            {
                back = true;
                menu.Closed -= Closed;
                (menu.Placement, menu.PlacementTarget) = was;
            }
        }
    }
}
