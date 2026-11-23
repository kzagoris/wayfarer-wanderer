// From Passenger 8a1df7e4 src/Passenger.App/MainWindow.Switcher.cs, MIT (notices/Passenger/LICENSE): over the files the
// window has open, which its one editor shows in turn, rather than tab groups; without an agent's input, which Wayfarer
// has none of, and without a screen reader's words.
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Passenger.Core;
using Passenger.Editor;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Editors;
using WayfarerWanderer.App.Shell.Explorer;
using WayfarerWanderer.App.Shell.Navigation;
using WayfarerWanderer.App.Shell.Workspace;
using A = WayfarerWanderer.App.Shell.AppActions;

namespace WayfarerWanderer.App;

/// <summary>The Switcher (Ctrl+Tab): the files open in the order they last came forward, however each came (the Project
/// window, Go to, Back, a search's result), then the tool windows. Pressed and let go at once, it goes back to the file
/// shown before, so a second press comes back; with its modifier held, its popup shows after a moment, Tab moving its
/// choice, and letting the modifier go goes to the one chosen. Asked for from Find Action, its popup waits for Enter.</summary>
public sealed partial class EditorWindow
{
    /// <summary>How long the modifier is held before the popup shows, so a quick press switches without it.</summary>
    private static readonly TimeSpan SwitcherShowsAfter = TimeSpan.FromMilliseconds(150);

    private readonly UsedOrder<OpenDocument> filesUsed = new();

    /// <summary>The Switcher while its modifier is held, until it is let go or the switch is left; null otherwise.</summary>
    private Switching? switching;

    /// <summary>The modifiers held with the key being pressed, while its press is handled; null outside a press (a menu's
    /// click), where the Switcher's popup waits for Enter.</summary>
    private KeyModifiers? pressedWith;

    private void AddSwitcher()
    {
        // Its keys are taken on their way in, ahead of the focus's own Tab, which would move the keys to the next control.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Handled || FirstStart is not null || keys.ActionFor(new Shortcut(Keystroke.From(e))) is not { } action
                || action is not (A.Switcher or A.SwitcherBackward))
            {
                return;
            }
            pressedWith = e.KeyModifiers;
            try
            {
                e.Handled = Actions.TryExecute(action);
            }
            finally
            {
                pressedWith = null;
            }
        }, RoutingStrategies.Tunnel);
        // In no menu: macOS's menu bar would take its keys as a key equivalent, never telling how long they are held.
        AddSwitch(A.Switcher, "Switcher", 1);
        AddSwitch(A.SwitcherBackward, "Switcher Backward", -1);

        void AddSwitch(string id, string title, int step) =>
            Actions.Registry.Add(new IdeAction(id, title, "IDE", c => Switch(c, step)) { WhyNot = _ => NothingToSwitchTo() });
    }

    /// <summary>A file shown: the latest used now.</summary>
    private void SwitcherFollowsFile(OpenDocument shown) => filesUsed.Came(shown);

    private string? NothingToSwitchTo() =>
        documents.Count > 0 || SwitchableWindows().Any() ? null : Refusals.NothingToSwitchTo;

    private IEnumerable<Shell.Docking.ToolWindow> SwitchableWindows() => ToolDock.Windows.Where(w => w.Unserved is null && w.Action is not null);

    /// <summary>The Switcher's rows as they stand: the files open, the latest used first, those not used since the window
    /// opened in Recent Files' order, then the tool windows, by name.</summary>
    public IReadOnlyList<SwitcherRow> SwitcherRows()
    {
        string? root = Session?.Root;
        filesUsed.Keep(d => documents.TryGetValue(d.Path, out OpenDocument? open) && open == d);
        List<OpenDocument> open = [.. documents.Values.OrderBy(d => RankIn(recentFiles, d))];
        List<SwitcherRow> rows = [.. filesUsed.Of(open).Select(d => new SwitcherRow(Path.GetFileName(d.Path),
            Path.GetDirectoryName(FileIndex.Shown(d.Path, root)) ?? string.Empty, d.Path, null))];
        rows.AddRange(SwitchableWindows().OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(w => new SwitcherRow(w.Title, Actions.Registry.Shortcut(w.Action!) ?? string.Empty, null, w)));
        return rows;

        static int RankIn(IReadOnlyList<string> recent, OpenDocument document)
        {
            for (int i = 0; i < recent.Count; i++)
            {
                if (PathNames.Same(recent[i], document.Path))
                {
                    return i;
                }
            }
            return int.MaxValue;
        }
    }

    /// <summary>Ctrl+Tab, or Ctrl+Shift+Tab for <paramref name="step"/> back: the choice moved while the Switcher is open, else
    /// the Switcher begun, its choice the row after the file shown (the file shown where the editor has not the keys), or the
    /// last row going back.</summary>
    private void Switch(ActionContext context, int step)
    {
        if (switching is { } held)
        {
            held.Move(step);
            return;
        }
        if (SearchShown is SwitcherPopup open)
        {
            open.Move(step);
            return;
        }
        IReadOnlyList<SwitcherRow> rows = SwitcherRows();
        if (rows.Count == 0)
        {
            return;
        }
        int shown = rows[0].Path is { } first && Editor.IsVisible && SourcePath is { } path && PathNames.Same(first, path)
            && context.ToolWindow is null ? 0 : -1;
        int chosen = step > 0 ? (shown + 1) % rows.Count : rows.Count - 1;
        KeyModifiers modifiers = (pressedWith ?? KeyModifiers.None) & ~KeyModifiers.Shift;
        if (modifiers != KeyModifiers.None)
        {
            switching = new Switching(this, modifiers, rows, chosen);
        }
        else
        {
            ShowSwitcher(rows, chosen);
        }
    }

    private SwitcherPopup ShowSwitcher(IReadOnlyList<SwitcherRow> rows, int chosen)
    {
        SwitcherPopup popup = new(rows, chosen);
        ShowSearch(popup, popup, row =>
        {
            if (row is not null)
            {
                SwitchTo(row);
            }
        });
        return popup;
    }

    /// <summary>Goes to a row's file, where it is still open, or a tool window, as its action does.</summary>
    private void SwitchTo(SwitcherRow row)
    {
        if (row.Path is { } path)
        {
            if (documents.ContainsKey(path))
            {
                GoToFile(path, null);
                Editor.Focus();
            }
        }
        else if (row.Window?.Action is { } action)
        {
            RunFromMenu(action);
        }
    }

    /// <summary>The Switcher with its modifier held: the choice moves with each press, the popup shows once the modifier has
    /// been held a moment, and letting the modifier go goes to the one chosen; Escape, the popup closed, or the window left
    /// ends it without going anywhere.</summary>
    private sealed class Switching
    {
        private readonly EditorWindow owner;
        private readonly KeyModifiers held;
        private readonly IReadOnlyList<SwitcherRow> rows;
        private readonly Debounce showing;
        private readonly EventHandler<KeyEventArgs> released;
        private readonly EventHandler<KeyEventArgs> pressed;
        private int chosen;
        private SwitcherPopup? popup;

        public Switching(EditorWindow owner, KeyModifiers held, IReadOnlyList<SwitcherRow> rows, int chosen)
        {
            this.owner = owner;
            this.held = held;
            this.rows = rows;
            this.chosen = chosen;
            released = (_, e) => OnReleased(e);
            pressed = (_, e) => OnPressed(e);
            owner.AddHandler(KeyUpEvent, released, RoutingStrategies.Tunnel, handledEventsToo: true);
            owner.AddHandler(KeyDownEvent, pressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            owner.Deactivated += OnLeft;
            showing = new Debounce(SwitcherShowsAfter, () =>
            {
                Show();
                return Task.CompletedTask;
            });
            showing.Restart();
        }

        /// <summary>The row the Switcher goes to as the modifier is let go, while its popup is not shown.</summary>
        public int Chosen => chosen;

        public void Move(int step)
        {
            if (popup is { } shown)
            {
                shown.Move(step);
            }
            else
            {
                chosen = ((chosen + step) % rows.Count + rows.Count) % rows.Count;
            }
        }

        private void Show()
        {
            if (owner.switching != this)
            {
                return;
            }
            popup = owner.ShowSwitcher(rows, chosen);
            popup.DetachedFromVisualTree += (_, _) => End();
            // The layer keeps a popup closed until the next shows: hidden, it is left as well.
            owner.searchLayer.PropertyChanged += OnLayerShown;
        }

        private void OnLayerShown(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == IsVisibleProperty && !owner.searchLayer.IsVisible)
            {
                End();
            }
        }

        /// <summary>One of the held modifiers let go: the row chosen gone to, through the popup where it shows.</summary>
        private void OnReleased(KeyEventArgs e)
        {
            if (Keystroke.Alone(e.Key, KeyModifiers.None) is not { } modifier || !held.HasFlag(modifier.Modifiers))
            {
                return;
            }
            End();
            if (popup is { } shown)
            {
                shown.Choose();
            }
            else
            {
                owner.SwitchTo(rows[chosen]);
            }
        }

        /// <summary>Escape before the popup shows, which takes its own once it does.</summary>
        private void OnPressed(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && popup is null)
            {
                End();
                e.Handled = true;
            }
        }

        private void OnLeft(object? sender, EventArgs e)
        {
            End();
            if (popup is { } shown && ReferenceEquals(owner.SearchShown, shown))
            {
                shown.GiveUp();
            }
        }

        private void End()
        {
            if (owner.switching != this)
            {
                return;
            }
            owner.switching = null;
            showing.Stop();
            owner.RemoveHandler(KeyUpEvent, released);
            owner.RemoveHandler(KeyDownEvent, pressed);
            owner.Deactivated -= OnLeft;
            owner.searchLayer.PropertyChanged -= OnLayerShown;
        }
    }

    /// <summary>The row the held Switcher would go to now, for tests; null while none is held.</summary>
    public int? SwitchingTo => switching?.Chosen;
}
