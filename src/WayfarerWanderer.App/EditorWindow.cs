using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Passenger.Editor;
using Passenger.Text;

namespace WayfarerWanderer.App;

public sealed class EditorWindow : Window
{
    public CodeEditor Editor { get; } = new();
    public BreakpointColumn Breakpoints { get; }
    public EditorActions Actions { get; }

    public EditorWindow(TextDocument? document)
    {
        Title = "Wayfarer Wanderer";
        Width = 860;
        Height = 540;
        var keys = DefaultKeymaps.ForPlatform().Bind(EditorActions.ToggleBreakpoint, "F9");
        Editor.Keymap = keys;
        if (document != null)
            Editor.Document = document;
        Editor.IsVisible = document != null;

        Breakpoints = new BreakpointColumn(ToggleClickedLine);
        Actions = new EditorActions(() => document != null, () => Breakpoints.Toggle(Editor.CaretPosition.Line));
        Editor.SetGutterColumns([Breakpoints], []);

        var action = Actions[EditorActions.ToggleBreakpoint];
        var shortcut = keys.ShortcutsFor(action.Id).Single().First;
        var toggle = new MenuItem
        {
            Header = action.Title,
            IsEnabled = action.Available(),
            InputGesture = new KeyGesture(shortcut.Key, shortcut.Modifiers)
        };
        toggle.Click += (_, _) => action.TryExecute();
        var menu = new Menu { Items = { new MenuItem { Header = "_Run", Items = { toggle } } } };
        DockPanel.SetDock(menu, Dock.Top);
        var query = new TextBox { PlaceholderText = "Find Action" };
        var matches = new ListBox { DisplayMemberBinding = new Avalonia.Data.Binding(nameof(EditorAction.Title)) };
        var palette = new StackPanel
        {
            Width = 340,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsVisible = false,
            Children = { query, matches }
        };
        void Refresh()
        {
            matches.ItemsSource = Actions.Find(query.Text ?? "").Where(a => a.Available()).ToArray();
            matches.SelectedIndex = matches.ItemCount > 0 ? 0 : -1;
        }
        void ClosePalette()
        {
            palette.IsVisible = false;
            Editor.Focus();
        }
        void RunSelected()
        {
            if (matches.SelectedItem is EditorAction selected && selected.TryExecute())
                ClosePalette();
        }
        query.TextChanged += (_, _) => Refresh();
        matches.DoubleTapped += (_, _) => RunSelected();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (e.Key == Key.A && e.KeyModifiers == (modifier | KeyModifiers.Shift))
            {
                palette.IsVisible = true;
                query.Text = "";
                Refresh();
                query.Focus();
            }
            else if (palette.IsVisible && e.Key == Key.Enter)
                RunSelected();
            else if (palette.IsVisible && e.Key == Key.Escape)
                ClosePalette();
            else if (palette.IsVisible && e.Key is Key.Down or Key.Up && matches.ItemCount > 0)
                matches.SelectedIndex = Math.Clamp(matches.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, matches.ItemCount - 1);
            else
                return;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Content = new DockPanel { Children = { menu, new Grid { Children = { Editor, palette } } } };
        KeyDown += (_, e) => e.Handled = Actions.TryExecute(keys.ActionFor(new Shortcut(Keystroke.From(e))));
        Opened += (_, _) => Editor.Focus();
    }

    private void ToggleClickedLine(int line)
    {
        Editor.GoTo(new TextPosition(line, 0));
        Actions.TryExecute(EditorActions.ToggleBreakpoint);
    }
}
