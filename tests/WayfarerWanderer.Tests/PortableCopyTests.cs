using Passenger.Core;
using WayfarerWanderer.App.Updates;

namespace WayfarerWanderer.Tests;

/// <summary>A portable copy (Passenger #2153) found from Wayfarer's own layouts, what it says of a newer release, and the
/// Windows update's reason read from Velopack's log (#2300).</summary>
public sealed class PortableCopyTests
{
    [Fact]
    public void TheMacAppKeepsItsDataBesideTheBundleThoughItsAppLivesInResources()
    {
        string folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string programs = Directory.CreateDirectory(Path.Combine(folder, "WayfarerWanderer.app", "Contents", "Resources", "app")).FullName;
            Assert.Equal(folder, ThisCopy.FolderOf(programs));
            Assert.Null(ThisCopy.Of(programs, []).Data);

            Directory.CreateDirectory(Path.Combine(folder, ThisCopy.DataName));
            Assert.Equal(PathNames.Canonical(Path.Combine(folder, ThisCopy.DataName)), ThisCopy.Of(programs, []).Data);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void TheWindowsAndLinuxFolderKeepsItsDataBesideTheExecutableAndADataFileIsRefused()
    {
        string folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string programs = Directory.CreateDirectory(Path.Combine(folder, "WayfarerWanderer")).FullName;
            Assert.Equal(programs, ThisCopy.FolderOf(programs));

            File.WriteAllText(Path.Combine(programs, ThisCopy.DataName), "not a folder");
            FormatException refused = Assert.Throws<FormatException>(() => ThisCopy.Of(programs, []));
            Assert.Contains("is no folder", refused.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Passenger", refused.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void APortableCopyNamesItsPortableBuildAndNeverDownloads()
    {
        string? asset = Releases.ThisSystemsAsset(portable: true);
        if (OperatingSystem.IsWindows())
        {
            Assert.Matches("^WayfarerWanderer-windows-(x64|arm64)-portable\\.zip$", asset);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Matches("^WayfarerWanderer-macos-(x64|arm64)\\.zip$", asset);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal(Releases.ThisSystemsAsset(portable: false), asset);
        }

        UpdatedElsewhere said = Updater.Portable("/somewhere/data", "WayfarerWanderer-windows-x64-portable.zip");
        Assert.False(said.Download);
        Assert.Contains("/somewhere/data", said.Why, StringComparison.Ordinal);
        Assert.Contains("WayfarerWanderer-windows-x64-portable.zip", said.Why, StringComparison.Ordinal);

        UpdatedElsewhere packaged = Updater.Packaged(new PackageManagerMark("Scoop", "scoop update wayfarer"));
        Assert.Equal("This copy of Wayfarer is a package Scoop installed: scoop update wayfarer updates it.", packaged.Why);
    }

    [Fact]
    public void VelopacksLogGivesTheLastApplysOwnError()
    {
        string[] lines =
        [
            "[update:100] [10:00:00] [INFO] Running: C:\\Users\\a\\AppData\\Local\\WayfarerWanderer\\Update.exe apply --package C:\\p\\WayfarerWanderer-0.1.0-full.nupkg",
            "[update:100] [10:00:00] [INFO] Process started with ID: 200",
            "[update:200] [10:00:01] [ERROR] Apply error: an older apply's error",
            "[other:300] [10:00:02] [ERROR] Another program's error",
            "[update:100] [10:01:00] [INFO] Running: C:\\Users\\a\\AppData\\Local\\WayfarerWanderer\\Update.exe apply --package C:\\p\\WayfarerWanderer-0.2.0-full.nupkg",
            "[update:100] [10:01:00] [INFO] Process started with ID: 201",
            "[other:300] [10:01:01] [ERROR] Another program's error",
            "[update:201] [10:01:11] [ERROR] Error applying package: Unable to start the update, because one or more running processes prevented it.",
        ];

        Assert.Equal("Unable to start the update, because one or more running processes prevented it.",
            VelopackLog.WhyNotApplied(lines, "WayfarerWanderer", "0.2.0"));
        Assert.Equal("an older apply's error", VelopackLog.WhyNotApplied(lines, "WayfarerWanderer", "0.1.0"));
        Assert.Null(VelopackLog.WhyNotApplied(lines, "WayfarerWanderer", "0.3.0"));
    }
}
