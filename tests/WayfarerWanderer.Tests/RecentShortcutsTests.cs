using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Passenger.Core;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell.State;
using WayfarerWanderer.App.Shell.Welcome;

namespace WayfarerWanderer.Tests;

/// <summary>The recent folders outside the window: the command line each entry starts Wayfarer with, the entries as the
/// recent folders give them, freedesktop's recently used list following them, and the taskbar's jump list kept.</summary>
public class RecentShortcutsTests
{
    private static readonly XNamespace Bookmark = "http://www.freedesktop.org/standards/desktop-bookmarks";

    [Fact]
    public void CommandLineTakesOneFolderOrANewWindow()
    {
        string folder = Path.Combine(Path.GetTempPath(), "some project");
        Assert.Equal(LaunchArguments.None, LaunchArguments.Parse([]));
        Assert.Equal(new LaunchArguments(Path.GetFullPath(folder), false), LaunchArguments.Parse([folder]));
        Assert.Equal(new LaunchArguments(null, true), LaunchArguments.Parse(["--new-window"]));
        Assert.Equal(LaunchArguments.None, LaunchArguments.Parse(["--veloapp-firstrun"]));
        Assert.Throws<FormatException>(() => LaunchArguments.Parse(["--nonsense"]));
        Assert.Throws<FormatException>(() => LaunchArguments.Parse([folder, folder + "2"]));
        Assert.Throws<FormatException>(() => LaunchArguments.Parse([folder, "--new-window"]));
    }

    [Fact]
    public void ShortcutsAreTheRecentFoldersOnDiskNewestFirstEachStartingWayfarerOnIt()
    {
        string root = Directory.CreateTempSubdirectory("wayfarer-shortcuts-").FullName;
        try
        {
            string older = Directory.CreateDirectory(Path.Combine(root, "a", "app")).FullName;
            string newer = Directory.CreateDirectory(Path.Combine(root, "b", "app")).FullName;
            string gone = Path.Combine(root, "gone");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            RecentFoldersState recent = new RecentFoldersState()
                .Opened(older, "app", now).Opened(gone, "gone", now.AddSeconds(1)).Opened(newer, "app", now.AddSeconds(2));

            (string Path, RecentShortcut Shortcut)[] shortcuts = [.. RecentShortcuts.Shortcuts(recent)];

            Assert.Equal([newer, older], shortcuts.Select(s => s.Path));
            Assert.Equal([newer], shortcuts[0].Shortcut.Arguments);
            Assert.Equal(newer, shortcuts[0].Shortcut.Tip);
            // Two folders named alike read apart by the folder each is in.
            Assert.Equal(["app – b", "app – a"], RecentShortcut.ToldApart(shortcuts, p => p).Select(s => s.Title));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task RecentlyUsedFollowsFoldersOpenedAndForgotten()
    {
        string root = Directory.CreateTempSubdirectory("wayfarer-recently-used-").FullName;
        string list = Path.Combine(root, "data", "recently-used.xbel");
        StateStore state = StateStore.Open(Path.Combine(root, "state"));
        LinuxRecentlyUsed.StandIn = new LinuxRecentlyUsed.Place(list, new RecentlyUsed.Program(LinuxRecentlyUsed.DesktopEntry, "/opt/wayfarer/WayfarerWanderer.App %f"),
            RecentlyUsed.Keeping.Gtk);
        try
        {
            // The list is freedesktop's, of Linux paths, whatever system the test runs on; nothing looks on disk for it.
            const string project = "/home/someone/my project";
            LinuxRecentlyUsed.Begin(state.Recent);
            state.Recent.Set(state.Recent.Value.Opened(project, "my project", DateTimeOffset.UtcNow));
            await LinuxRecentlyUsed.Follow(state.Recent);

            XElement bookmark = Assert.Single(XDocument.Load(list).Root!.Elements("bookmark"));
            Assert.Equal("file:///home/someone/my%20project", (string?)bookmark.Attribute("href"));
            XElement application = Assert.Single(bookmark.Descendants(Bookmark + "application"));
            Assert.Equal(LinuxRecentlyUsed.DesktopEntry, (string?)application.Attribute("name"));
            Assert.Equal("1", (string?)application.Attribute("count"));

            state.Recent.Set(state.Recent.Value.Without(project));
            await LinuxRecentlyUsed.Follow(state.Recent);

            Assert.Empty(XDocument.Load(list).Root!.Elements("bookmark"));
        }
        finally
        {
            LinuxRecentlyUsed.StandIn = null;
            state.Release();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void JumpListIsKeptOnWindowsTaskbar()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The taskbar's jump list is Windows' own");
            return;
        }
        KeptAndDeleted();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void KeptAndDeleted()
    {
        string root = Directory.CreateTempSubdirectory("wayfarer-jump-list-").FullName;
        // A list of its own, so the person's Wayfarer's is left as it is.
        string appId = "WayfarerWanderer.Tests." + Guid.NewGuid().ToString("N");
        string? refused = "not run";
        string? deleted = "not run";
        List<string> forgotten = [];
        Thread apartment = new(() =>
        {
            string project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            RecentFoldersState recent = new RecentFoldersState().Opened(project, "project", DateTimeOffset.UtcNow);
            refused = WindowsJumpList.Set(RecentShortcuts.Shortcuts(recent), [RecentShortcuts.NewWindow], forgotten.AddRange, appId);
            deleted = WindowsJumpList.Delete(appId);
        });
        apartment.SetApartmentState(ApartmentState.STA);
        apartment.Start();
        apartment.Join();
        Directory.Delete(root, true);

        Assert.Null(refused);
        Assert.Null(deleted);
        Assert.Empty(forgotten);
    }
}
