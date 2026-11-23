using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Passenger.Editor;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Docking;
using WayfarerWanderer.App.Shell.Settings;
using WayfarerWanderer.App.Shell.Terminal;

namespace WayfarerWanderer.App;

/// <summary>The window's terminal, as Passenger's MainWindow.Terminal has it: its tool window at the bottom, the action
/// that opens and leaves it, New Terminal Session, and the keys it takes ahead of the window's.</summary>
public sealed partial class EditorWindow
{
    private TerminalPanel? terminal;

    /// <summary>The Terminal tool window's view, built as it is first shown or asked for.</summary>
    public TerminalPanel TerminalPanel => terminal ?? (TerminalPanel)ToolDock[ToolWindowIds.Terminal].Content;

    /// <summary>The folder a new shell starts in: the open folder, else the home folder.</summary>
    private string TerminalFolder => Session?.Root ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private void AddTerminal()
    {
        ToolDock.Add(new ToolWindow(ToolWindowIds.Terminal, "Terminal", IconKeys.Terminal, MakeTerminal, ToolAnchor.BottomLeft)
        {
            Action = AppActions.ShowTerminal,
            ActionTitle = "Terminal Tool Window",
            Tools = () => terminal?.HeaderTools,
            Activate = ToggleTerminal,
            TakesKeysAsShown = true,
        });
        Actions.Registry.Add(new IdeAction(AppActions.NewTerminalSession, "New Terminal Session", "Terminal", _ => IdeErrors.Observe(NewTerminalSessionAsync()))
        {
            FromTerminal = true,
        });
        // Ahead of the terminal, which takes every key it is given: the window's actions that run from it.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (TerminalTakes(FocusManager?.GetFocusedElement(), e) is { } action)
            {
                e.Handled = Actions.TryExecute(action);
            }
        }, RoutingStrategies.Tunnel);
        Closing += (_, _) =>
        {
            if (terminal is { } open)
            {
                IdeErrors.Observe(open.CloseAllAsync());
            }
        };
    }

    /// <summary>A shell of its own in the open folder, its tab made before the window shows, so showing it starts no other.</summary>
    private Task<Passenger.Terminal.TerminalSession> NewTerminalSessionAsync()
    {
        Task<Passenger.Terminal.TerminalSession> starting = TerminalPanel.NewSessionAsync();
        ToolDock.Show(ToolWindowIds.Terminal);
        return starting;
    }

    /// <summary>A new tab of the Terminal window, its shell started in a folder (Open In, Terminal).</summary>
    public Task<Passenger.Terminal.TerminalSession> OpenTerminalAt(string at)
    {
        Task<Passenger.Terminal.TerminalSession> starting = TerminalPanel.NewSessionAsync(at);
        ToolDock.Show(ToolWindowIds.Terminal);
        return starting;
    }

    /// <summary>The Terminal window's view, as it is first shown or asked for, with the settings in force then.</summary>
    private TerminalPanel MakeTerminal()
    {
        TerminalPanel panel = terminal = new TerminalPanel(() => TerminalFolder);
        panel.ActionRequested += (_, action) => Actions.TryExecute(action);
        panel.CommandFailed += (_, _) => Cue(SoundCue.TerminalCommandFailed);
        panel.Emptied += (_, _) =>
        {
            ToolDock.Hide(ToolWindowIds.Terminal);
            if (Editor.IsVisible)
            {
                Editor.Focus();
            }
        };
        FollowTerminal(panel, SettingsKept.Effective, null);
        return panel;
    }

    /// <summary>The terminal's look and ways as the settings in force give them: the code font unless it has a font of its
    /// own, and a shell no longer set given back to the system's.</summary>
    private static void FollowTerminal(TerminalPanel panel, AppSettings next, AppSettings? before)
    {
        FollowTerminalView(panel.View, next, before);
        if (next.TerminalShell is { } shell && CommandLine.Split(shell) is { Count: > 0 } parts)
        {
            panel.Shell = (parts[0], [.. parts.Skip(1)]);
        }
        else if (before?.TerminalShell is not null)
        {
            panel.Shell = null;
        }
    }

    /// <summary>A terminal view's look as the settings in force give it: the Terminal window's, and the Run and Debug
    /// consoles'.</summary>
    private static void FollowTerminalView(Passenger.Terminal.TerminalView view, AppSettings next, AppSettings? before)
    {
        view.FontSize = next.TerminalFontSize;
        if (before is null || before.TerminalFontFamily != next.TerminalFontFamily)
        {
            if (CodeFonts.Named(next.TerminalFontFamily) is { } own)
            {
                view.FontFamily = own;
            }
            else
            {
                view.ClearValue(Passenger.Terminal.TerminalView.FontFamilyProperty);
            }
        }
        view.CursorBlinking = next.TerminalCursorBlinking;
        view.CursorStyle = next.TerminalCursorStyle;
        view.CopyOnSelection = next.TerminalCopyOnSelection;
    }

    /// <summary>The terminal follows a change of the settings, once it is built.</summary>
    private void TerminalSettingsChanged(SettingsChange change)
    {
        FollowTerminalView(RunTool.Console.View, change.After, change.Before);
        FollowTerminalView(DebugTool.Console.View, change.After, change.Before);
        if (terminal is { } panel)
        {
            FollowTerminal(panel, change.After, change.Before);
        }
    }

    /// <summary>The terminal's shortcut: to the terminal from anywhere, and back to the editor from the terminal.</summary>
    private void ToggleTerminal()
    {
        if (terminal?.View.IsFocused == true)
        {
            ToolDock.Hide(ToolWindowIds.Terminal);
            if (Editor.IsVisible)
            {
                Editor.Focus();
            }
            return;
        }
        ToolDock.Bring(ToolWindowIds.Terminal);
        TerminalPanel.FocusView();
    }

    /// <summary>The action a key runs ahead of the focused terminal, or null where the terminal takes it: one that runs from
    /// the terminal (<see cref="IdeAction.FromTerminal"/>) or shows a tool window, but never a Ctrl+letter (the shell's), nor
    /// any of Cmd's keys on the Mac but Cmd+K (its Clear Buffer), nor the second key of a chord.</summary>
    private string? TerminalTakes(object? focused, KeyEventArgs e)
    {
        if (focused is not Passenger.Terminal.TerminalView)
        {
            return null;
        }
        if (OperatingSystem.IsMacOS() && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            return e.KeyModifiers == KeyModifiers.Meta && e.Key == Key.K ? null : keys.ActionFor(new Shortcut(Keystroke.From(e)));
        }
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is >= Key.A and <= Key.Z)
        {
            return null;
        }
        return keys.ActionFor(new Shortcut(Keystroke.From(e))) is { } action
            && (Actions.Registry[action] is { FromTerminal: true } || ToolDock.Windows.Any(w => w.Action == action))
            ? action
            : null;
    }
}
