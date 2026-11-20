// From Passenger 2044e49b src/Passenger.App/Welcome/LinuxRecentlyUsed.cs, MIT (notices/Passenger/LICENSE): its recent
// solutions followed, kept as Wayfarer's recent folders, and its install found by Wayfarer's desktop entry.
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using Passenger.Core;
using WayfarerWanderer.App.Shell.State;

namespace WayfarerWanderer.App.Shell.Welcome;

/// <summary>freedesktop's recently used list (<see cref="RecentlyUsed"/>) kept in step with the recent folders: each
/// opened since the list last followed them is a use of Wayfarer's, and each let go of loses Wayfarer's use, so KDE lists
/// them under Wayfarer's icon and file managers as Recent. Only a copy install.sh installed writes it, naming its desktop
/// entry, whose Exec KDE knows the icon by; adding follows the desktop's own switch and size, read at each write. The
/// writes go in turn, off the window's thread.</summary>
public static class LinuxRecentlyUsed
{
    /// <summary>The desktop entry install.sh writes (without <c>.desktop</c>), as tools/package-release.py names it.</summary>
    public const string DesktopEntry = "io.github.kzagoris.WayfarerWanderer";

    /// <summary>A list Wayfarer follows, the program it names, and how it is held (null: as the desktop holds it).</summary>
    public sealed record Place(string File, RecentlyUsed.Program Program, RecentlyUsed.Keeping? Keeping);

    /// <summary>The list a test follows in place of the person's, as an installed Wayfarer a person sees follows it.</summary>
    public static Place? StandIn { get; set; }

    /// <summary>Each recent list as the recently used list last followed it.</summary>
    private static readonly ConditionalWeakTable<StateDocument<RecentFoldersState>, RecentFoldersState> Followed = new();

    /// <summary>One write at a time, in the order asked.</summary>
    private static readonly SemaphoreSlim Turn = new(1, 1);

    /// <summary>Wayfarer as install.sh installed it, found once; null where it isn't.</summary>
    private static Task<Place?>? installed;

    /// <summary>Following <paramref name="recent"/> starts from it as it stands, where it has not begun: what a start
    /// reopens comes after.</summary>
    public static void Begin(StateDocument<RecentFoldersState> recent)
    {
        UiThread.Check();
        Followed.TryAdd(recent, recent.Value);
    }

    /// <summary>What changed in <paramref name="recent"/> since it was last followed goes into the list; the writing, which
    /// a test waits for.</summary>
    public static Task Follow(StateDocument<RecentFoldersState> recent)
    {
        UiThread.Check();
        RecentFoldersState now = recent.Value;
        RecentFoldersState before = Followed.TryGetValue(recent, out RecentFoldersState? was) ? was : now;
        Followed.AddOrUpdate(recent, now);
        (IReadOnlyList<(string Path, DateTimeOffset When)> opened, IReadOnlyList<string> gone) = RecentlyUsed.Between(Stamps(before), Stamps(now));
        if (opened.Count == 0 && gone.Count == 0)
        {
            return Task.CompletedTask;
        }
        Task<Place?> place = StandIn is { } standIn ? Task.FromResult<Place?>(standIn) : installed ??= BackgroundWork.Run(Installed);
        Task writing = BackgroundWork.Run(() => WriteAsync(place, opened, gone));
        IdeErrors.Observe(writing);
        return writing;
    }

    private static Dictionary<string, DateTimeOffset> Stamps(RecentFoldersState state) =>
        state.Folders.ToDictionary(s => s.Key, s => s.Value.Opened, StringComparer.Ordinal);

    private static async Task WriteAsync(Task<Place?> finding, IReadOnlyList<(string Path, DateTimeOffset When)> opened, IReadOnlyList<string> gone)
    {
        if (await finding is not { } place)
        {
            return;
        }
        RecentlyUsed.Keeping keeping = place.Keeping ?? await DesktopKeepingAsync();
        await Turn.WaitAsync();
        try
        {
            Write(place, keeping, opened, gone);
        }
        finally
        {
            Turn.Release();
        }
    }

    private static void Write(Place place, RecentlyUsed.Keeping keeping, IReadOnlyList<(string Path, DateTimeOffset When)> opened, IReadOnlyList<string> gone)
    {
        XDocument? was;
        try
        {
            using FileStream stream = new(place.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            was = RecentlyUsed.Read(stream);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            was = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
        {
            IdeErrors.Write($"Recently used: {place.File} could not be read, so it is left as it is: {error.Message}");
            return;
        }
        XDocument? list = was;
        if (keeping.Adds)
        {
            foreach ((string path, DateTimeOffset when) in opened)
            {
                list = RecentlyUsed.Used(list, path, RecentlyUsed.Folder, place.Program, when);
            }
        }
        foreach (string path in gone)
        {
            list = list is null ? null : RecentlyUsed.Forgotten(list, path, place.Program.Name);
        }
        if (list is null || ReferenceEquals(list, was))
        {
            return;
        }
        list = RecentlyUsed.Kept(list, keeping, DateTimeOffset.UtcNow);
        try
        {
            string target = AtomicFile.Target(place.File);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            AtomicFile.Write(target, RecentlyUsed.Bytes(list), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Recently used: {place.File} not written: {error.Message}");
        }
    }

    /// <summary>Wayfarer as install.sh installed it: a desktop entry in the data home or the system's whose Exec runs this
    /// process's program, with the list in the data home; null for a build or an archive unpacked.</summary>
    private static Place? Installed()
    {
        if (!OperatingSystem.IsLinux() || RecentlyUsed.FileIn(Environment.GetEnvironmentVariable) is not { } file
            || Environment.ProcessPath is not { } process)
        {
            return null;
        }
        string program = PathNames.Canonical(process);
        foreach (string folder in (string[])[Path.GetDirectoryName(file)!, "/usr/local/share", "/usr/share"])
        {
            string entry = Path.Combine(folder, "applications", DesktopEntry + ".desktop");
            try
            {
                if (File.Exists(entry) && File.ReadLines(entry).FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal)) is { } line
                    && line["Exec=".Length..] is var exec && RecentlyUsed.CommandOf(exec) is var command && Path.IsPathRooted(command)
                    && PathNames.Same(PathNames.Canonical(command), program))
                {
                    return new Place(file, new RecentlyUsed.Program(DesktopEntry, exec), null);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                IdeErrors.Write($"Recently used: {entry} could not be read: {error.Message}");
            }
        }
        IdeErrors.Write($"Recently used: no desktop entry runs {program}, so this Wayfarer, not one install.sh installed, lists nothing as recently used");
        return null;
    }

    /// <summary>How the desktop holds the list: KDE's settings in a KDE session, else GNOME's where gsettings has them,
    /// else GTK's settings files.</summary>
    private static async Task<RecentlyUsed.Keeping> DesktopKeepingAsync()
    {
        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } set && Path.IsPathRooted(set) ? set
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        if ((Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty).Split(':').Contains("KDE", StringComparer.OrdinalIgnoreCase))
        {
            return RecentlyUsed.Keeping.OfKde([Text(Path.Combine(config, "kdeglobals")), Text("/etc/xdg/kdeglobals")]);
        }
        if (Programs.OnPath("gsettings") is { } gsettings)
        {
            try
            {
                ChildResult listed = await ChildProcesses.RunAsync(ChildProcesses.Command(gsettings, ["list-recursively", "org.gnome.desktop.privacy"]),
                    limit: Waits.Of(TimeSpan.FromSeconds(10)));
                if (listed.Succeeded && RecentlyUsed.Keeping.OfGnome(listed.Output) is { } gnome)
                {
                    return gnome;
                }
            }
            catch (Exception error) when (ChildFailures.Of(error))
            {
                IdeErrors.Write($"Recently used: GNOME's privacy settings could not be read, so GTK's are taken: {error.Message}");
            }
        }
        string[] folders = [Path.Combine(config, "gtk-4.0"), Path.Combine(config, "gtk-3.0"), "/etc/xdg/gtk-4.0", "/etc/xdg/gtk-3.0", "/etc/gtk-3.0"];
        return RecentlyUsed.Keeping.OfGtk(folders.Select(folder => Text(Path.Combine(folder, "settings.ini"))));
    }

    /// <summary>A settings file's text, or null where there is none to read.</summary>
    private static string? Text(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
