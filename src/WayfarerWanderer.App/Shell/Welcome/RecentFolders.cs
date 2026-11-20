// From Passenger 2044e49b src/Passenger.App/Welcome/RecentSolutions.cs, MIT (notices/Passenger/LICENSE): its recent
// solutions, kept as the folders Wayfarer opens.
using System.Text.Json.Nodes;
using Passenger.Core;
using WayfarerWanderer.App.Shell.State;

namespace WayfarerWanderer.App.Shell.Welcome;

/// <summary>The folders opened lately, by canonical path: what the Welcome screen and File, Recent Folders list, and those
/// still open in a window as Wayfarer last quit, which a start reopens. Kept by path rather than as a list, so two windows'
/// openings and removals merge entry by entry.</summary>
public sealed record RecentFoldersState : IStateDocument<RecentFoldersState>
{
    /// <summary>How many are kept, the oldest dropped past it.</summary>
    public const int Limit = 50;

    public Dictionary<string, RecentFolder> Folders { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    /// <summary>The newest first, each with its path.</summary>
    public IReadOnlyList<(string Path, RecentFolder Entry)> Newest() =>
        [.. Folders.OrderByDescending(s => s.Value.Opened).ThenBy(s => s.Key, StringComparer.Ordinal).Select(s => (s.Key, s.Value))];

    /// <summary>Those a window had open as Wayfarer last quit, or has open now, oldest first, so the newest opens last.</summary>
    public IReadOnlyList<(string Path, RecentFolder Entry)> StillOpen() => [.. Newest().Where(s => s.Entry.Open).Reverse()];

    /// <summary>With a path opened now, newest of all even on a clock that has not moved since the last, and open.</summary>
    public RecentFoldersState Opened(string path, string name, DateTimeOffset now)
    {
        DateTimeOffset latest = Folders.Values.Select(s => s.Opened).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        Dictionary<string, RecentFolder> next = new(Folders)
        {
            [path] = new RecentFolder { Name = name, Opened = now > latest ? now : latest.AddTicks(1), Open = true },
        };
        return this with { Folders = Kept(next) };
    }

    /// <summary>With a path no window has open any longer (its window closed, or another folder opened in it), so a start
    /// leaves it closed; kept among the recent ones.</summary>
    public RecentFoldersState Closed(string path)
    {
        if (!Folders.TryGetValue(path, out RecentFolder? entry) || !entry.Open)
        {
            return this;
        }
        return this with { Folders = new(Folders) { [path] = entry with { Open = false } } };
    }

    public RecentFoldersState Without(string path)
    {
        Dictionary<string, RecentFolder> next = new(Folders);
        next.Remove(path);
        return this with { Folders = next };
    }

    /// <summary>An entry without a path is left out, a name missing is the path's own, and only the newest are kept.</summary>
    public RecentFoldersState Clamped() => this with
    {
        Folders = Kept((Folders ?? []).Where(s => !string.IsNullOrEmpty(s.Key) && s.Value is not null)
            .ToDictionary(s => s.Key, s => s.Value with { Name = string.IsNullOrEmpty(s.Value.Name) ? Path.GetFileName(s.Key.TrimEnd('/', '\\')) : s.Value.Name })),
    };

    private static Dictionary<string, RecentFolder> Kept(Dictionary<string, RecentFolder> all) =>
        all.Count <= Limit ? all : all.OrderByDescending(s => s.Value.Opened).Take(Limit).ToDictionary(s => s.Key, s => s.Value);
}

/// <summary>One recent folder: its name as the window showed it, when it was last opened, and whether a window still had it
/// open as Wayfarer last quit.</summary>
public sealed record RecentFolder
{
    public string Name { get; init; } = string.Empty;

    public DateTimeOffset Opened { get; init; }

    public bool Open { get; init; }
}

/// <summary>A recent folder as a list shows it: where it is, its name, its path with the home folder as ~, and whether it is
/// gone from disk.</summary>
public sealed record RecentRow(string Path, string Name, string Shown, bool Missing)
{
    public static RecentRow Of(string path, RecentFolder entry) =>
        new(path, entry.Name, Home(path), !Directory.Exists(path));

    /// <summary>A path under the home folder, shown from ~.</summary>
    public static string Home(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && PathNames.IsUnder(path, home) ? "~" + path[home.TrimEnd('/', '\\').Length..] : path;
    }

    /// <summary>What a folder opens as, by the build it holds at its top: Maven, Gradle, an IntelliJ module, or a folder.</summary>
    public static string Kind(string path)
    {
        if (File.Exists(System.IO.Path.Combine(path, "pom.xml")))
        {
            return "Maven";
        }
        if (((string[])["build.gradle.kts", "build.gradle", "settings.gradle.kts", "settings.gradle"]).Any(f => File.Exists(System.IO.Path.Combine(path, f))))
        {
            return "Gradle";
        }
        try
        {
            return Directory.Exists(path) && Directory.EnumerateFiles(path, "*.iml").Any() ? "IntelliJ" : "Folder";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "Folder";
        }
    }
}
