using Avalonia.Controls;
using Avalonia.Input;
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
        Content = new DockPanel { Children = { menu, Editor } };
        KeyDown += (_, e) => e.Handled = Actions.TryExecute(keys.ActionFor(new Shortcut(Keystroke.From(e))));
        Opened += (_, _) => Editor.Focus();
    }

    private void ToggleClickedLine(int line)
    {
        Editor.GoTo(new TextPosition(line, 0));
        Actions.TryExecute(EditorActions.ToggleBreakpoint);
    }
}
