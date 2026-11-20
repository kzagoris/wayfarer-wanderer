// From Passenger 2044e49b src/Passenger.App/ToolWindows/ListMenus.cs, MIT (notices/Passenger/LICENSE): the placing of a
// menu the keys open, which the Explorer's row menu needs, and a list's menu with its items, which the Welcome screen's
// recent folders have; the rest cut.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace WayfarerWanderer.App.Shell.ToolWindows;

/// <summary>Right-click menus on a tool window's list.</summary>
public static class ListMenus
{
    /// <summary>A list's right-click menu, made again as its choice changes and as it closes: the items for the row chosen,
    /// none where they are none.</summary>
    public static void Attach(SelectingItemsControl list, Func<IReadOnlyList<Control>> items)
    {
        void Make()
        {
            if (list.ContextMenu is { IsOpen: true })
            {
                return;
            }
            IReadOnlyList<Control> made = items();
            ContextMenu? menu = made.Count == 0 ? null : new ContextMenu { ItemsSource = made };
            if (menu is not null)
            {
                menu.Closed += (_, _) => Make();
            }
            list.ContextMenu = menu;
        }
        list.SelectionChanged += (_, _) => Make();
        Make();
    }

    /// <summary>A menu item, with the keys it shows (in KeyGesture's words) where it has them.</summary>
    public static MenuItem Item(string header, string? keys, Action run, bool enabled = true)
    {
        MenuItem item = new() { Header = header, IsEnabled = enabled, InputGesture = keys is null ? null : KeyGesture.Parse(keys) };
        item.Click += (_, _) => run();
        return item;
    }

    /// <summary>Puts text on the clipboard of the window a control is in.</summary>
    public static async Task CopyAsync(Visual from, string text)
    {
        if (TopLevel.GetTopLevel(from)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

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

    /// <summary>A submenu of items.</summary>
    public static MenuItem Sub(string header, params Control[] items) => new() { Header = header, ItemsSource = items };
}
