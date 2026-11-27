// From Passenger 494009d7 src/Passenger.App/Welcome/RecentShortcuts.cs, MIT (notices/Passenger/LICENSE): its system lists
// of recent solutions, kept as Wayfarer's recent folders, each entry a start of Wayfarer of its own.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Passenger.Core;
using Passenger.Editor;
using WayfarerWanderer.App.Shell.State;

namespace WayfarerWanderer.App.Shell.Welcome;

/// <summary>The system's lists of Wayfarer outside its windows, kept in step with the recent folders
/// (<c>recentFolders.json</c>) as the window that last changed them has them: the Windows taskbar's jump list
/// (<see cref="WindowsJumpList"/>), whose entries start Wayfarer on the folder, the macOS Dock's menu, whose items open one
/// as File, Recent Folders does, and on Linux freedesktop's recently used list (<see cref="LinuxRecentlyUsed"/>), which KDE
/// shows as the Recent Files of Wayfarer's icon. Only the desktop app (one keeping state) sets them; a UI-thread object.</summary>
public static class RecentShortcuts
{
    /// <summary>How many recent ones the Dock's menu lists; the jump list lists as many as the taskbar says it shows.</summary>
    public const int DockShows = 10;

    /// <summary>The jump list's heading over the recent ones, as the File menu names them.</summary>
    public const string Category = "Recent Folders";

    /// <summary>How long the recent list rests before the system's lists follow it, so a start's reopening sets them once.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    private static Debounce? following;

    /// <summary>The window whose recent list the system's lists follow next.</summary>
    private static EditorWindow? from;

    /// <summary>The task starting a window with nothing open.</summary>
    public static RecentShortcut NewWindow { get; } = new("New Window", "A Wayfarer window with nothing open", [LaunchArguments.NewWindowFlag]);

    /// <summary>The window's recent list changed, or it opened on it: the system's lists follow once it rests, in the
    /// desktop app, or where a test's recently used list stands in. A portable copy writes no list the system keeps
    /// (<see cref="ThisCopy.Data"/>): only the Dock's menu, held by its process alone, follows.</summary>
    public static void Follow(EditorWindow window, StateDocument<RecentFoldersState> recent)
    {
        UiThread.Check();
        bool registers = ThisCopy.Data is null;
        bool recentlyUsed = LinuxRecentlyUsed.StandIn is not null || OperatingSystem.IsLinux() && registers;
        bool shell = OperatingSystem.IsWindows() && registers || OperatingSystem.IsMacOS();
        if (!recentlyUsed && !shell)
        {
            return;
        }
        if (recentlyUsed)
        {
            LinuxRecentlyUsed.Begin(recent);
        }
        from = window;
        following ??= new Debounce(Settle, () =>
        {
            if (from is { } asked && IsOpen(asked) && asked.RecentKept is { } kept)
            {
                Set(asked, kept);
            }
            return Task.CompletedTask;
        });
        following.Restart();
    }

    private static bool IsOpen(EditorWindow window) =>
        Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.Windows.Contains(window);

    private static void Set(EditorWindow window, StateDocument<RecentFoldersState> recent)
    {
        if (LinuxRecentlyUsed.StandIn is not null || OperatingSystem.IsLinux())
        {
            LinuxRecentlyUsed.Follow(recent);
        }
        else if (OperatingSystem.IsWindows())
        {
            if (WindowsJumpList.Set(Shortcuts(recent.Value), [NewWindow], window.ForgetRecent) is { } refused)
            {
                IdeErrors.Write(refused);
            }
        }
        else if (OperatingSystem.IsMacOS() && Application.Current is { } app)
        {
            NativeMenus.Fill(app, NativeDock.MenuProperty, DockItems([.. Shortcuts(recent.Value).Take(DockShows)]));
        }
    }

    /// <summary>The recent folders still on disk, newest first, each a start of Wayfarer naming its path; read as taken.</summary>
    public static IEnumerable<(string Path, RecentShortcut Shortcut)> Shortcuts(RecentFoldersState recent) =>
        RecentShortcut.Recent(recent.Newest().Select(r => (r.Path, r.Entry.Name)), Directory.Exists);

    /// <summary>The Dock's menu's items, in the one menu the application keeps for it (<see cref="NativeMenus"/>): the
    /// recent ones titled apart, each opened from the window in front as File, Recent Folders opens it, then New Window,
    /// which starts another Wayfarer.</summary>
    private static List<NativeMenuItemBase> DockItems(IReadOnlyList<(string Path, RecentShortcut Shortcut)> recent)
    {
        List<NativeMenuItemBase> items = [];
        foreach (((string path, _), RecentShortcut shortcut) in recent.Zip(RecentShortcut.ToldApart(recent, RecentRow.Home)))
        {
            NativeMenuItem item = new(shortcut.Title) { ToolTip = shortcut.Tip };
            item.Click += (_, _) => Front()?.OpenRecent(path);
            items.Add(item);
        }
        if (items.Count > 0)
        {
            items.Add(new NativeMenuItemSeparator());
        }
        NativeMenuItem newWindow = new(NewWindow.Title);
        newWindow.Click += (_, _) => StartAnother(NewWindow.Arguments);
        items.Add(newWindow);
        return items;
    }

    /// <summary>The window in front, else the last opened.</summary>
    private static EditorWindow? Front() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.OfType<EditorWindow>().FirstOrDefault(w => w.IsActive) ?? desktop.Windows.OfType<EditorWindow>().LastOrDefault()
            : null;

    /// <summary>Another Wayfarer started with <paramref name="arguments"/>, as the jump list's entries start one.</summary>
    private static void StartAnother(IReadOnlyList<string> arguments)
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return;
        }
        try
        {
            using System.Diagnostics.Process? started = System.Diagnostics.Process.Start(Passenger.Core.ChildProcesses.Command(exe, arguments));
        }
        catch (Exception error) when (Passenger.Core.ChildFailures.Of(error))
        {
            IdeErrors.Write($"New Window: Wayfarer did not start: {error.Message}");
        }
    }
}
