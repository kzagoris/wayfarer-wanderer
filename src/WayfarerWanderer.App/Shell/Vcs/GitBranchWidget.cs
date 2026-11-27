// From Passenger 494009d7 src/Passenger.App/MainWindow.axaml (GitBranchButton), MainWindow.GitStatus.cs and
// MainWindow.Vcs.cs (ShowBranchesAsync, Listed), MIT (notices/Passenger/LICENSE): the branch widget alone, without the
// sync and changed-files widgets beside it.
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;
using Passenger.Vcs;
using WayfarerWanderer.App.Shell.ToolWindows;

namespace WayfarerWanderer.App.Shell.Vcs;

/// <summary>The status bar's Git branch: in a working tree, the branch checked out (Detached HEAD where none is), a click
/// opening the branch popup (<see cref="PopupAsync"/>): what is done next, then the local branches, each with its own menu.
/// Hidden outside a working tree.</summary>
public sealed class GitBranchWidget : Button
{
    private readonly TextBlock words = new() { Classes = { StyleClasses.Status }, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    public GitBranchWidget()
    {
        Classes.Add(StyleClasses.StatusItem);
        IsVisible = false;
        MinWidth = 96;
        PathIcon branch = new() { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        branch.Bind(PathIcon.DataProperty, branch.GetResourceObservable(IconKeys.Branch));
        branch.Bind(PathIcon.ForegroundProperty, branch.GetResourceObservable(TokenKeys.TextMuted));
        DockPanel.SetDock(branch, Dock.Left);
        Content = new DockPanel { HorizontalSpacing = Gaps.Tight, Children = { branch, words } };
    }

    /// <summary>Styled as the Button it is.</summary>
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>The branch as the widget shows it; null while it is hidden.</summary>
    public string? Shown => IsVisible ? words.Text : null;

    /// <summary>Shows <paramref name="vcs"/>'s branch, or hides the widget outside a working tree.</summary>
    public void Show(VcsController vcs)
    {
        bool tree = vcs.Repository is not null;
        IsVisible = tree;
        string branch = BranchWords(vcs);
        words.Text = tree ? branch : null;
        ToolTip.SetTip(this, tree ? $"Git branch: {branch}\n\nClick to check out another branch or make a new one" : null);
        AutomationProperties.SetName(this, tree ? $"Git branch: {branch}" : null);
    }

    /// <summary>What a working tree's branch is called where the window shows it.</summary>
    public static string BranchWords(VcsController vcs) => vcs.Branch ?? "Detached HEAD";

    /// <summary>How many local branches the branch popup lists at most, whatever the repository holds.</summary>
    public const int BranchesListed = 10;

    /// <summary>The branch popup's items: Update Project, Fetch, New Branch…, Commit, Push, Stash and Unstash, then the
    /// local branches (<see cref="Listed"/>), each opening its own menu (Checkout, Merge into Current and the rest), and More
    /// Branches… for the rest.</summary>
    /// <param name="action">An action's item as the window's menus show it, with its keys.</param>
    /// <param name="branchMenu">A branch's own menu, as the Git window's branches panel has it.</param>
    public static async Task<IReadOnlyList<Control>> PopupAsync(VcsController vcs, Func<string, Control> action, Action newBranch, Action moreBranches,
        Func<BranchInfo, IReadOnlyList<Control>> branchMenu, GitBranchesState favorites)
    {
        List<Control> items =
        [
            action(AppActions.UpdateProject),
            action(AppActions.Fetch),
            ListMenus.Item("New Branch…", newBranch),
            action(AppActions.Commit),
            action(AppActions.Push),
            action(AppActions.Stash),
            action(AppActions.Unstash),
            new Separator(),
            ListMenus.Heading("Local"),
        ];
        IReadOnlyList<BranchInfo> local = await vcs.LocalBranchesAsync();
        foreach (BranchInfo branch in Listed(local, favorites))
        {
            items.Add(BranchItem(branch.Name, branch.IsCurrent, () => branchMenu(branch)));
        }
        if (local.Count > BranchesListed)
        {
            items.Add(ListMenus.Item("More Branches…", moreBranches));
        }
        return items;
    }

    /// <summary>The local branches the branch popup lists, of those read with the one committed to last first: the current
    /// one, then the favourites, then the rest, at most <see cref="BranchesListed"/>.</summary>
    public static List<BranchInfo> Listed(IReadOnlyList<BranchInfo> local, GitBranchesState favorites)
    {
        List<BranchInfo> listed = [];
        for (int rank = 0; rank < 3 && listed.Count < BranchesListed; rank++)
        {
            for (int i = 0; i < local.Count && listed.Count < BranchesListed; i++)
            {
                if ((local[i].IsCurrent ? 0 : favorites.Holds(local[i]) ? 1 : 2) == rank)
                {
                    listed.Add(local[i]);
                }
            }
        }
        return listed;
    }

    /// <summary>A branch whose own menu holds what can be done with it, made as that menu first opens (upstream's
    /// MenuChoice.Holding).</summary>
    private static MenuItem BranchItem(object header, bool current, Func<IReadOnlyList<Control>> items)
    {
        MenuItem item = new() { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = current, ItemsSource = (Control[])[new Separator()] };
        bool made = false;
        item.SubmenuOpened += (_, e) =>
        {
            if (e.Source == item && !made)
            {
                made = true;
                item.ItemsSource = items();
            }
        };
        return item;
    }
}
