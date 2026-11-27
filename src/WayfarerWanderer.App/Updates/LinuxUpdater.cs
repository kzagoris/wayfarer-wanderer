// From Passenger 494009d7 src/Passenger.App/Updates/LinuxUpdater.cs, MIT (notices/Passenger/LICENSE): only the per-user
// archive install, Wayfarer's layout.
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Passenger.Core;

namespace WayfarerWanderer.App.Updates;
using WayfarerWanderer.App.Shell;

/// <summary>Stages the verified Linux archive beside the default per-user install and swaps directories at exit.</summary>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxUpdater : InPlaceUpdater
{
    private static readonly TimeSpan AskLimit = TimeSpan.FromSeconds(10);
    private readonly string Folder;
    private readonly string Staging;
    private readonly string running = OwnVersion.Shown;
    public LinuxUpdater(string install, string staging) : base(Launch)
    {
        Folder = install;
        Staging = staging;
    }
    public override string Installed => Folder;
    internal static LinuxUpdater? OfThisInstall(string staging, out string why)
    {
        string install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/wayfarer-wanderer");
        why = "Only the per-user archive installation updates itself.";
        if (Environment.ProcessPath is not { } executable || !PathNames.SameFile(executable, Path.Combine(install, Program.ProgramName)))
            return null;
        install = PathNames.Canonical(install);
        if (Libc.Access(Path.GetDirectoryName(install)!, Libc.MayWrite) != 0)
        {
            why = $"The installation at {install} cannot be replaced by this user.";
            return null;
        }
        why = string.Empty;
        return new LinuxUpdater(install, staging);
    }
    private Task<UpdatedElsewhere?>? packageOwner;

    public override Task<UpdatedElsewhere?> ElsewhereAsync(CancellationToken cancel)
    {
        if (packageOwner is null || packageOwner.IsCanceled || packageOwner.IsFaulted)
            packageOwner = FindPackageOwnerAsync(cancel);
        return packageOwner;
    }

    private async Task<UpdatedElsewhere?> FindPackageOwnerAsync(CancellationToken cancel)
    {
        string executable = Path.Combine(Folder, Program.ProgramName);
        if (await MarkOfAsync(executable, cancel).ConfigureAwait(false) is { } mark)
        {
            return Packaged(mark);
        }
        return await PackageOfAsync(executable, cancel).ConfigureAwait(false) is { } package
            ? new UpdatedElsewhere($"This copy of Wayfarer is {package}: the system's package manager updates it.", false) : null;
    }

    /// <summary>The package manager the file a package writes beside the app names (<see cref="ThisCopy.PackageManagerName"/>),
    /// or null where there is none; a file that reads as none is still a package manager's, unnamed.</summary>
    private static async Task<PackageManagerMark?> MarkOfAsync(string executable, CancellationToken cancel)
    {
        string marker = Path.Combine(Path.GetDirectoryName(executable)!, ThisCopy.PackageManagerName);
        return File.Exists(marker)
            ? ThisCopy.MarkOf(await File.ReadAllBytesAsync(marker, cancel).ConfigureAwait(false)) ?? new PackageManagerMark(null, null)
            : null;
    }

    /// <summary>The package a package manager installed the app from, as the person would name it, or null: the answer of
    /// each package database on the PATH for the app's path.</summary>
    private static async Task<string?> PackageOfAsync(string executable, CancellationToken cancel)
    {
        (string Tool, string Manager, string[] Arguments)[] databases =
        [
            ("dpkg-query", "dpkg", ["-S", executable]),
            ("rpm", "rpm", ["-qf", "--queryformat", "%{NAME}", executable]),
            ("pacman", "pacman", ["-Qqo", executable]),
        ];
        foreach ((string tool, string manager, string[] arguments) in databases)
        {
            if (Programs.OnPath(tool) is not { } found)
            {
                continue;
            }
            ChildResult owner = await ToolAsync(ChildProcesses.Command(found, arguments), AskLimit, cancel).ConfigureAwait(false);
            // dpkg-query says "package: path", rpm and pacman the package alone.
            if (owner.Succeeded && owner.FirstLine is { Length: > 0 } line)
            {
                string name = tool == "dpkg-query" ? line.Split(':')[0].Trim() : line;
                return $"{manager}'s package {name}";
            }
        }
        return null;
    }

    protected override async Task<UpdateOutcome> PrepareAsync(LatestRelease release, Action<int> progress, CancellationToken cancel)
    {

        if (PackageOf(release) is not { } package)
        {
            return Announced(release, "archive for this processor");
        }
        // Not held: removing old copies is the disk's work, which nothing drawn waits for.
        await BackgroundWork.Unheld(() => Clean(keep: release.Version), cancel).ConfigureAwait(false);
        // Only the person writes where a release is made ready, so no one else changes it before it is installed.
        UserTemporary.Own(Staging);
        string folder = Path.Combine(Staging, release.Version);
        UserTemporary.Own(folder);
        return await ForOneAsync(release, package, folder, progress, cancel).ConfigureAwait(false);
    }

    /// <summary>One person's release, unpacked beside the install from the archive downloaded into
    /// <paramref name="folder"/>, which goes once it is unpacked.</summary>
    private async Task<UpdateOutcome> ForOneAsync(LatestRelease release, ReleasePackage package, string folder, Action<int> progress, CancellationToken cancel)
    {
        string unpacked = UpdateFolders.Beside(Folder, release.Version);
        string packageFolder = Path.Combine(unpacked, "WayfarerWanderer");
        if (Directory.Exists(packageFolder))
        {
            // Made ready by an earlier start: checked again rather than downloaded again.
            try
            {
                Check(packageFolder, release.Version);
                return new UpdateOutcome.Ready($"Wayfarer Wanderer {release.Version} was unpacked before, to {packageFolder}, and checked again");
            }
            catch (UpdateRefusedException)
            {
                Remove(unpacked);
            }
        }
        (string saved, string expected) = await DownloadCheckedAsync(package, folder, progress, cancel).ConfigureAwait(false);
        await UnpackAsync(saved, unpacked, cancel).ConfigureAwait(false);
        File.Delete(saved);
        Check(packageFolder, release.Version);
        return new UpdateOutcome.Ready(
            $"Wayfarer Wanderer {release.Version} downloaded to {saved} and unpacked to {packageFolder}: {package.Download.Name}'s SHA-256 is {ReleaseSums.Name}' {expected}");
    }

    /// <summary>Unpacks the archive into a folder of its own, its app folder put in place only once all of it came
    /// out, so a folder an interrupted unpacking left is never taken for a release.</summary>
    private static async Task UnpackAsync(string archive, string unpacked, CancellationToken cancel)
    {
        UserTemporary.Own(unpacked);
        Remove(Path.Combine(unpacked, "WayfarerWanderer"));
        Remove(Path.Combine(unpacked, "partial"));
        string partial = Directory.CreateDirectory(Path.Combine(unpacked, "partial")).FullName;
        try
        {
            await using FileStream file = File.OpenRead(archive);
            await using GZipStream gzip = new(file, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, partial, overwriteFiles: false, cancel).ConfigureAwait(false);
        }
        catch (InvalidDataException error)
        {
            throw new UpdateRefusedException($"{Path.GetFileName(archive)} does not unpack as a gzipped tar archive", error.Message);
        }
        string[] top = [.. Directory.EnumerateFileSystemEntries(partial).Select(Path.GetFileName).OfType<string>()];
        if (top.Length != 2 || !Directory.Exists(Path.Combine(partial, "WayfarerWanderer")) || !File.Exists(Path.Combine(partial, "install.sh")))
        {
            throw new UpdateRefusedException($"{Path.GetFileName(archive)} holds {(top.Length == 0 ? "nothing" : string.Join(", ", top))}, expected WayfarerWanderer and install.sh");
        }
        Directory.Move(Path.Combine(partial, "WayfarerWanderer"), Path.Combine(unpacked, "WayfarerWanderer"));
        Directory.Delete(partial, recursive: true);
    }

    /// <summary>The release unpacked is the release's: install.sh's layout, its launcher, script and app executable, and
    /// the version recorded in its deps.json.</summary>
    private static void Check(string packageFolder, string version)
    {
        string executable = Path.Combine(packageFolder, Program.ProgramName);
        if (!File.Exists(executable) || !File.GetUnixFileMode(executable).HasFlag(UnixFileMode.UserExecute))
        {
            throw new UpdateRefusedException($"the archive has no executable {Program.ProgramName}");
        }
        string deps = Path.Combine(packageFolder, "WayfarerWanderer.App.deps.json");
        string? named = File.Exists(deps) ? UpdateFolders.AppVersion(File.ReadAllText(deps)) : null;
        if (named != version)
        {
            throw new UpdateRefusedException($"the archive's Wayfarer is {(named is null ? "of no version" : named)}, not {version}");
        }
    }

    /// <summary>The verified app directory beside the installation.</summary>
    protected override string Staged(string version) => Path.Combine(UpdateFolders.Beside(Folder, version), "WayfarerWanderer");
    protected override string? Replace(string staged, string version) => Swap(staged, version);
    protected override void Tidy(string version) => Remove(Path.Combine(Staging, version));
    protected override void Placed()
    {
        string script = Path.Combine(Folder, "refresh-desktop.sh");
        if (!File.Exists(script)) return; // Earlier releases carried registration only in install.sh.
        ChildResult result = ToolAsync(ChildProcesses.Command("/bin/sh", [script, Folder]), TimeSpan.FromSeconds(30),
            CancellationToken.None).GetAwaiter().GetResult();
        if (!result.Succeeded) IdeErrors.Write($"Updating desktop integration failed: {result.Said}");
    }

    private string? Swap(string packageFolder, string version)
    {
        int failed;
        try
        {
            failed = RenameAt(WorkingFolder, packageFolder, WorkingFolder, Folder, RenameExchange) == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
        catch (EntryPointNotFoundException)
        {
            failed = NotImplemented;
        }
        if (failed == 0)
        {
            return null;
        }
        if (failed is not (InvalidArgument or NotImplemented))
        {
            return Marshal.GetPInvokeErrorMessage(failed);
        }
        return InTwoRenames(packageFolder, Path.Combine(Path.GetDirectoryName(packageFolder)!, "replaced"), version).Why;
    }

    public override Task DiscardAsync()
    {
        Ready = null;

        // Every one: no version is named so.
        Clean(keep: string.Empty);
        return Task.CompletedTask;
    }

    public override Task TidyAsync()
    {
        Clean(keep: null);
        return Task.CompletedTask;
    }

    /// <summary>Every download and unpacked release but <paramref name="keep"/>'s; with none kept, only those not newer than
    /// the running version (<see cref="UpdateFolders.Stale"/>), so a release unpacked and not yet in place stays.</summary>
    private void Clean(string? keep)
    {
        List<string> stale = [];
        if (Directory.Exists(Staging))
        {
            stale.AddRange(Directory.EnumerateDirectories(Staging).Where(folder => UpdateFolders.Stale(Path.GetFileName(folder), running, keep)));
        }
        if (Path.GetDirectoryName(Folder) is { } parent && Directory.Exists(parent))
        {
            stale.AddRange(Directory.EnumerateDirectories(parent, UpdateFolders.Prefix + "*")
                .Where(folder => UpdateFolders.VersionOf(folder) is { } version && UpdateFolders.Stale(version, running, keep)));
        }
        foreach (string folder in stale)
        {
            Remove(folder);
        }
    }

    public override IReadOnlyList<int> OtherProcesses() => RunningAs(Path.Combine(Folder, Program.ProgramName));

    /// <summary>The install's launcher, started apart from this process, which it outlives, with this process's
    /// environment (its state folder, release feed as they were given).</summary>
    private static Task Launch(string install)
    {
        ChildProcesses.StartApart(ChildProcesses.Command(Path.Combine(install, Program.ProgramName), [], Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        return Task.CompletedTask;
    }

    /// <summary>AT_FDCWD: a path relative to the working folder, or absolute.</summary>
    private const int WorkingFolder = -100;

    private const uint RenameExchange = 0x2;

    /// <summary>EINVAL: the file system cannot exchange two folders.</summary>
    private const int InvalidArgument = 22;

    /// <summary>ENOSYS: the kernel has no renameat2.</summary>
    private const int NotImplemented = 38;

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt(int fromFolder, string from, int toFolder, string to, uint flags);
}
