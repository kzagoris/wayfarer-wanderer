// From Passenger 494009d7 src/Passenger.App/ToolWindows/ListMenus.cs, ToolButtons.cs, Refusals.cs, DialogFooter.cs and
// BrowseRow.cs, MIT (notices/Passenger/LICENSE): only the members the Git views call.
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.Editor;
using WayfarerWanderer.App.Shell.Explorer;
using WayfarerWanderer.App.Shell.ToolWindows;

namespace WayfarerWanderer.App.Shell;

/// <summary>The v0.4.2 members of shared pieces that the Git views call while those pieces' own ports are on other
/// branches. Each is an extension member, so the piece's own member, once it has one, is the one called and these go
/// unused: delete each when its piece has it.</summary>
internal static class GitBridges
{
    extension(ListMenus)
    {
        /// <summary>An item, greyed while <paramref name="whyNot"/> says why it cannot run, which its tip says; with
        /// <paramref name="icon"/> (a key of <see cref="IconKeys"/>), that icon before its words.</summary>
        public static MenuItem Item(string header, Action run, string? whyNot = null, string? icon = null)
        {
            MenuItem item = new() { Header = header };
            if (icon is not null)
            {
                PathIcon glyph = new();
                glyph.Bind(PathIcon.DataProperty, glyph.GetResourceObservable(icon));
                item.Icon = glyph;
            }
            ListMenus.Grey(item, whyNot);
            item.Click += (_, _) => run();
            return item;
        }

        /// <summary>An item that runs an action, with the keys the keymap in force gives that action.</summary>
        public static MenuItem Keyed(string header, string action, Action run, string? whyNot = null) =>
            KeysOf(ListMenus.Item(header, run, whyNot), action);

        /// <summary>What Enter and a double click do on a list's row, in the list's words for it.</summary>
        public static MenuItem Open(string header, Action run, string? whyNot = null) => Act(header, ListKeys.OpenKey, run, whyNot);

        /// <summary>What F4 does on a list's row: Go to Source in every list.</summary>
        public static MenuItem Source(Action run, string? whyNot = null) => Act("Go to Source", ListKeys.SourceKey, run, whyNot);

        /// <summary>An item turning something on or off, ticked while it is on; with <paramref name="action"/>, the keys the
        /// keymap gives the action it runs.</summary>
        public static MenuItem Toggle(string header, bool on, Action<bool> turn, string? whyNot = null, string? icon = null, string? action = null)
        {
            MenuItem item = ListMenus.Item(header, () => turn(!on), whyNot, icon);
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = on;
            return action is null ? item : KeysOf(item, action);
        }

        /// <summary>The heading over a group of a menu's items: muted words the keys and the pointer pass over, heard as a
        /// heading.</summary>
        public static MenuItem Heading(string words)
        {
            MenuItem item = new() { Header = words, IsEnabled = false, Classes = { StyleClasses.Caption } };
            AutomationProperties.SetHeadingLevel(item, 1);
            return item;
        }

        /// <summary>Greys an item while it cannot run, its tip saying why; null enables it again.</summary>
        public static void Grey(MenuItem item, string? whyNot)
        {
            item.IsEnabled = whyNot is null;
            ToolTip.SetTip(item, whyNot);
        }
    }

    private static MenuItem Act(string header, Key key, Action run, string? whyNot)
    {
        MenuItem item = ListMenus.Item(header, run, whyNot);
        item.InputGesture = new KeyGesture(key);
        return item;
    }

    private static MenuItem KeysOf(MenuItem item, string action)
    {
        if (AppActions.Keymap().ShortcutsFor(action).FirstOrDefault(s => s.Second is null) is { } keys)
        {
            item.InputGesture = new KeyGesture(keys.First.Key, keys.First.Modifiers);
        }
        return item;
    }

    extension(ToolButtons)
    {
        /// <summary>A tool button showing <paramref name="mark"/>, named and tipped <paramref name="tip"/>.</summary>
        public static Button Of(Control mark, string tip, Action? run = null)
        {
            Button button = new() { Classes = { StyleClasses.Tool }, Content = mark, Focusable = false };
            ToolButtons.Tip(button, tip);
            if (run is not null)
            {
                button.Click += (_, _) => run();
            }
            return button;
        }

        /// <summary>A small button of a glyph beside words (a chip's ×).</summary>
        public static Button Small(string glyph, string tip, Action? run = null)
        {
            Button button = new() { Classes = { StyleClasses.Small }, Content = RowIcon.Glyph(glyph) };
            ToolButtons.Tip(button, tip);
            if (run is not null)
            {
                button.Click += (_, _) => run();
            }
            return button;
        }

        /// <summary>Names and tips a button, its tip naming <paramref name="keys"/> too where it has them.</summary>
        public static T Tip<T>(T button, string tip, string? keys)
            where T : Control
        {
            ToolTip.SetTip(button, ActionTips.Named(tip, keys));
            AutomationProperties.SetName(button, tip);
            return button;
        }
    }

    extension(Refusals)
    {
        public static string FileDeleted => "The file is deleted";

        public static string Tracked => "Git tracks this file: only an unversioned file is added to .gitignore";

        public static string DeletedByCommit => "The commit deleted this file, so it holds none to compare";

        public static string Detached => "HEAD is detached: no branch is checked out";
    }

    extension(DialogFooter)
    {
        /// <summary>Marks a dialog's page to stand in from the window's edges as its footer does; until DialogFooter has it,
        /// the page keeps its own margin.</summary>
        public static AttachedProperty<bool> InsetProperty => Inset;
    }

    private static readonly AttachedProperty<bool> Inset = AvaloniaProperty.RegisterAttached<Control, bool>("Inset", typeof(GitBridges));

    extension(BrowseRow)
    {
        /// <summary>A field with a Browse… button after it, which asks <paramref name="pick"/>.</summary>
        public static BrowseRow Of(TextBox field, Func<Task> pick)
        {
            Button browse = new() { Content = "Browse…", Margin = new Thickness(Gaps.Normal, 0, 0, 0) };
            browse.Click += (_, _) => IdeErrors.Observe(pick());
            return BrowseRow.Of(field, browse);
        }
    }

    extension(BrowseRow row)
    {
        /// <summary>The Browse… button.</summary>
        public Button Browse => row.Children.OfType<Button>().Single();
    }
}
