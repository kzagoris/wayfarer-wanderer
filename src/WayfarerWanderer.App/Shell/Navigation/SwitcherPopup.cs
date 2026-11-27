// From Passenger 494009d7 src/Passenger.App/Navigation/SwitcherPopup.cs, MIT (notices/Passenger/LICENSE): a file's row
// names its path rather than its tab (Wayfarer's one editor shows the files open in turn), and nothing is told a screen
// reader (Wayfarer has no announcer yet).
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Text;
using WayfarerWanderer.App.Shell.Docking;
using WayfarerWanderer.App.Shell.Explorer;

namespace WayfarerWanderer.App.Shell.Navigation;

/// <summary>A row of the Switcher: a file open by its icon, its name and its folder, or a tool window by its icon, its name
/// and its keys.</summary>
public sealed record SwitcherRow(string Name, string After, string? Path, ToolWindow? Window)
{
    public Geometry? Icon => Path is { } file ? FileIcons.ForFile(file) : null;

    public string? IconTint => Path is { } file ? FileIcons.TintForFile(file) : null;

    /// <summary>The theme's key of a tool window's icon.</summary>
    public string? IconKey => Window?.Icon;
}

/// <summary>The Switcher's popup: the files open, the one used last first, then the tool windows, with one chosen. Tab and
/// Down move the choice on, Shift+Tab and Up back, Tab round from either end, a modifier held or not; a name typed narrows
/// the rows; Enter or a double click goes to the one chosen, Escape leaves. Its keys are its own while it has them
/// (<see cref="IOwnKeys"/>), so the keymap runs none of them.</summary>
public sealed class SwitcherPopup : SearchPopup<SwitcherRow>, IOwnKeys
{
    private readonly IReadOnlyList<SwitcherRow> all;
    private readonly int first;

    /// <param name="chosen">The row chosen as it opens.</param>
    public SwitcherPopup(IReadOnlyList<SwitcherRow> rows, int chosen)
        : base("Switcher", "A file's or tool window's name", PopupFrame.Medium)
    {
        all = rows;
        first = chosen;
        AutomationProperties.SetName(this, Title);
        QueryChanged();
    }

    protected override void QueryChanged()
    {
        // Called by the base's constructor's text before the rows are given.
        if (all is null)
        {
            return;
        }
        string typed = Query.Trim();
        Show(typed.Length == 0 ? all : [.. all.Where(row => FuzzyMatch.Score(typed, row.Name) is not null)], "Nothing open has that name",
            typed.Length == 0 ? first : 0);
    }

    /// <summary>A row: its icon, its name, and its folder or keys at the row's end, as every popup row's detail.</summary>
    protected override Control Row() => Editors.HiddenTabsPopup.TabRow<SwitcherRow>((shown, icon, name, after) =>
    {
        icon.Data = shown.Icon;
        icon.DataKey = shown.IconKey;
        icon.ForegroundKey = shown.IconTint;
        name.Text = shown.Name;
        after.Text = shown.After;
    });

    /// <summary>The choice moved a row on, or back for a negative step, round from either end.</summary>
    public void Move(int step)
    {
        int count = Rows.Count;
        if (count > 0)
        {
            int at = Chosen();
            ChooseAt(at < 0 ? (step > 0 ? 0 : count - 1) : ((at + step) % count + count) % count);
        }
    }

    /// <summary>Tab and Shift+Tab, and Up and Down with a modifier held, which every search popup's own keys pass over.</summary>
    protected override bool TakesKey(KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            Move(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            return true;
        }
        if (e.Key is Key.Up or Key.Down && Rows.Count > 0)
        {
            ChooseAt(Math.Clamp(Chosen() + (e.Key == Key.Up ? -1 : 1), 0, Rows.Count - 1));
            return true;
        }
        return false;
    }

    /// <summary>The keys that move, choose or leave, whatever modifier is held with them.</summary>
    bool IOwnKeys.TakesKey(KeyEventArgs e) => e.Key is Key.Tab or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Enter or Key.Escape;

    private int Chosen()
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            if (ReferenceEquals(Rows[i], Selected))
            {
                return i;
            }
        }
        return -1;
    }
}
