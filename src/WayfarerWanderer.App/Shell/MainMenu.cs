// From Passenger 2044e49b src/Passenger.App/MainMenu.cs, MIT (notices/Passenger/LICENSE).
namespace WayfarerWanderer.App.Shell;

/// <summary>One entry of a menu: an action (titled as it is registered), a submenu, a separator, or a submenu the window
/// lists as it opens (View's Tool Windows, File's Recent Projects).</summary>
public sealed record MenuEntry(string? Action, string? Title = null, IReadOnlyList<MenuEntry>? Items = null)
{
    public bool IsSeparator => Action is null && Items is null && Title is null;

    /// <summary>A submenu of what the window has as it opens, named by its title.</summary>
    public bool IsListed => Action is null && Items is null && Title is not null;
}

/// <summary>The main menu: File, Edit, View, Navigate, Code, Refactor, Build, Run, Tests, Tools, Git, Window and Help,
/// each made of the actions that name a place in it, in their order, a separator between each run of them; a menu no
/// action names a place in is left out. On the toolbar as a bar of its titles or behind its button (Settings' Show main
/// menu as a menu bar), the titles the toolbar has no room for behind the button after them.</summary>
public static class MainMenu
{
    public const string ToolWindows = "Tool Windows";
    public const string RecentProjects = "Recent Projects";

    /// <summary>The menus, in their order.</summary>
    public static IReadOnlyList<string> Titles { get; } = ["File", "Edit", "View", "Navigate", "Code", "Refactor", "Build", "Run", "Tests", "Tools", "Git", "Window", "Help"];

    /// <summary>The submenus the window lists as each opens.</summary>
    private static readonly string[] Listed = [ToolWindows, RecentProjects];

    /// <summary>The submenus, each where its parent lists it.</summary>
    private static readonly MenuPlace[] Submenus =
    [
        new("View/" + ToolWindows, 0, 0),
        new("View/Active Editor", 2, 0),
        new("File/" + RecentProjects, 0, 4),
        new("File/Line Separators", 1, 2),
    ];

    private static readonly MenuEntry Separator = new(null);

    /// <summary>The menus the actions' places make, those with nothing in them left out.</summary>
    public static IReadOnlyList<MenuEntry> Build(IEnumerable<(string Id, MenuPlace Place)> placed)
    {
        List<(string Id, MenuPlace Place)> all = [.. placed];
        return [.. Titles.Select(title => Menu(title, all)).Where(menu => menu.Items is { Count: > 0 })];
    }

    private static MenuEntry Menu(string path, List<(string Id, MenuPlace Place)> placed)
    {
        IEnumerable<(int Group, int Order, MenuEntry Entry)> actions = placed.Where(p => p.Place.Path == path)
            .Select(p => (p.Place.Group, p.Place.Order, new MenuEntry(p.Id)));
        IEnumerable<(int Group, int Order, MenuEntry Entry)> submenus = Submenus.Where(s => s.Path[..Math.Max(0, s.Path.LastIndexOf('/'))] == path)
            .Select(s => (s.Group, s.Order, Listed.Contains(Name(s.Path)) ? new MenuEntry(null, Name(s.Path)) : Menu(s.Path, placed)));
        List<MenuEntry> items = [];
        int? group = null;
        foreach ((int Group, int Order, MenuEntry Entry) item in actions.Concat(submenus).OrderBy(i => i.Group).ThenBy(i => i.Order))
        {
            if (group is { } before && before != item.Group)
            {
                items.Add(Separator);
            }
            items.Add(item.Entry);
            group = item.Group;
        }
        return new MenuEntry(null, Name(path), items);
    }

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
}
