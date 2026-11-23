// From Passenger 8a1df7e4 src/Passenger.App/Navigation/GoToFilePopup.cs, MIT (notices/Passenger/LICENSE).
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Text;
using WayfarerWanderer.App.Shell.Explorer;

namespace WayfarerWanderer.App.Shell.Navigation;

/// <summary>Go to File: a query over every file in the folder, ranked as it is typed, the recent files when it is
/// empty. Up and Down move through the results, Enter opens one, Escape leaves. A line after the name ("Main.java:20",
/// "Main.java:20:4") goes there.</summary>
public sealed partial class GoToFilePopup : SearchPopup<FileHit>
{
    private readonly FileIndex index;
    private readonly IReadOnlyList<string> recent;

    public GoToFilePopup(FileIndex index, IReadOnlyList<string> recent, string? initialQuery = null)
        : base("Go to File", "File name, CamelHumps or a path with /", PopupFrame.Wide, initialQuery)
    {
        this.index = index;
        this.recent = recent;
        QueryChanged();
    }

    /// <summary>The place a line after the file's name asked for, counted from one as a stack trace writes it, from the
    /// start of its line when no column is given; null for the file alone.</summary>
    public TextPosition? At { get; private set; }

    /// <summary>A query's name and the line and column after it: "name:line[:column]".</summary>
    [GeneratedRegex(@"^(?<name>.*?):(?<line>\d+)(?::(?<column>\d+))?\s*$")]
    private static partial Regex Place();

    /// <summary>A query's file name and the place after it (<see cref="At"/>), which Search Everywhere's files take too.</summary>
    public static (string Name, TextPosition? At) PlaceIn(string query)
    {
        if (Place().Match(query) is { Success: true } place && place.Groups["name"].Value.Trim().Length > 0
            && int.TryParse(place.Groups["line"].Value, CultureInfo.InvariantCulture, out int line) && line > 0)
        {
            int column = int.TryParse(place.Groups["column"].Value, CultureInfo.InvariantCulture, out int given) && given > 0 ? given : 1;
            return (place.Groups["name"].Value, new TextPosition(line - 1, column - 1));
        }
        return (query, null);
    }

    protected override void QueryChanged()
    {
        // Called by the base's constructor's text before the index is given.
        if (index is null)
        {
            return;
        }
        (string query, At) = PlaceIn(Query);
        IEnumerable<FileHit> hits = query.Trim().Length == 0
            ? recent.Select(full => new FileHit(full, FileIndex.Shown(full, index.Root)))
            : index.Search(query).Select(p => new FileHit(Path.Combine(index.Root, p), p));
        Show([.. hits], query.Trim().Length == 0 ? "No recent files" : "Nothing matches");
    }

    /// <summary>A file's row: its icon, its name and its folder at the row's end, as every popup row's detail.</summary>
    protected override Control Row()
    {
        // The hidden tabs' row upstream (HiddenTabsPopup.TabRow), which Wayfarer has no popup for.
        RowIcon icon = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Gaps.Normal, 0), [DockPanel.DockProperty] = Dock.Left };
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock folder = ListRow.DetailText();
        DockPanel row = new() { Height = ListRow.Height, Children = { icon, folder, name } };
        return row.Showing<FileHit>(hit =>
        {
            icon.Data = hit.Icon;
            icon.Foreground = hit.IconBrush;
            name.Text = hit.Name;
            folder.Text = hit.Folder;
        });
    }
}

/// <summary>One result: the file's name, the folder it is in, and its icon.</summary>
public sealed record FileHit(string FullPath, string RelativePath)
{
    public string Name => Path.GetFileName(RelativePath);
    public string Folder => Path.GetDirectoryName(RelativePath) is { Length: > 0 } folder ? folder : string.Empty;
    public Geometry Icon => FileIcons.ForFile(FullPath);
    public IBrush IconBrush => FileIcons.BrushForFile(FullPath);
}
