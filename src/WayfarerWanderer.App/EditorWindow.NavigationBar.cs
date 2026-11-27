// From Passenger 494009d7 src/Passenger.App/MainWindow.NavigationBar.cs, MIT (notices/Passenger/LICENSE): one bar over
// Wayfarer's one editor, fed the outline the status bar's path asks the Java and Kotlin servers for; without a screen
// reader's words.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Editors;
using WayfarerWanderer.App.Shell.Navigation;
using WayfarerWanderer.App.Shell.Settings;
using A = WayfarerWanderer.App.Shell.AppActions;

namespace WayfarerWanderer.App;

/// <summary>The navigation bar over the editor (<see cref="Shell.Editors.NavigationBar"/>): the list a part opens, under it,
/// narrowed as typed as File Structure is, the row chosen gone to with the keys given to the editor; a folder's part shown
/// in the Project window; and Jump to Navigation Bar, which gives the bar the keys.</summary>
public sealed partial class EditorWindow
{
    /// <summary>The bar over the editor.</summary>
    public NavigationBar NavigationBar { get; private set; } = null!;

    /// <summary>The editor with its tab strip and navigation bar docked over it, which the dock's centre holds.</summary>
    private Control EditorWithBar()
    {
        AddEditorTabs();
        NavigationBar = new NavigationBar(Editor) { IsOn = SettingsKept.Value.ShowNavigationBar };
        NavigationBar.ListAsked += (_, list) => ShowNavigationList(list);
        Actions.Registry.Add(new IdeAction(A.JumpToNavigationBar, "Jump to Navigation Bar", NavigateGroup, _ => JumpToNavigationBar())
        {
            WhyNot = _ => SourcePath is null || !Editor.IsVisible ? "No file is open"
                : !NavigationBar.Bar.IsVisible && !NavigationBar.HasOutline
                    ? $"{Path.GetFileName(SourcePath)} has no outline yet: only Java and Kotlin files have one, once their server is running"
                    : null,
            Words = ["Breadcrumbs", "Navigation Bar", "Focus Breadcrumbs"],
        });
        DockPanel.SetDock(NavigationBar.Bar, Dock.Top);
        return new EditorDock(EditorTabs) { Children = { EditorTabs.TabStrip, NavigationBar.Bar, Editor } };
    }

    /// <summary>The bar shown or hidden as Settings now says, the status bar's path naming the outline where it no longer does.</summary>
    private void FollowNavigationBar(AppSettings settings)
    {
        if (NavigationBar.IsOn == settings.ShowNavigationBar)
        {
            return;
        }
        NavigationBar.IsOn = settings.ShowNavigationBar;
        ShowBreadcrumbs();
    }

    /// <summary>The keys to the bar; with the bar hidden, the list its last part opens.</summary>
    private void JumpToNavigationBar()
    {
        if (!NavigationBar.JumpTo())
        {
            StatusText = SourcePath is { } path ? $"{Path.GetFileName(path)} has no outline yet" : "No file is open";
        }
    }

    /// <summary>A navigation bar's list under the part that opened it: narrowed as typed as File Structure is, the row
    /// chosen gone to with the keys given to the editor.</summary>
    private void ShowNavigationList(NavigationList list)
    {
        SymbolPopup popup = new(list.Title, "Filter by name, CamelHumps too", (query, _) =>
        {
            string filter = query.Trim();
            return Task.FromResult(filter.Length == 0 ? list.Rows : FilterOutline(list.Rows, filter));
        }, initial: _ => list.Chosen, width: PopupFrame.Medium);
        Point? under = list.Under.TranslatePoint(new Point(0, ReferenceEquals(list.Under, Editor) ? 0 : list.Under.Bounds.Height), this);
        ShowSearch(popup, popup, chosen =>
        {
            if (chosen is not null)
            {
                NavigateTo(chosen.Path, chosen.Position);
            }
        }, under);
    }

    /// <summary>The list a navigation bar's part opened, while it is open: for tests.</summary>
    public SymbolPopup? NavigationListShown => SearchShown as SymbolPopup;
}
