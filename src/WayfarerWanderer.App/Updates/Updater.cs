// Adapted from vendored Passenger, MIT (notices/Passenger/LICENSE).
using System.Diagnostics;
using Passenger.Core;
using Velopack.Locators;

namespace WayfarerWanderer.App.Updates;
using WayfarerWanderer.App.Shell;

/// <summary>How this copy of Wayfarer installs a newer release: it downloads the release's package for this system in
/// the background, checks it against the SHA-256 the release gives (and the app's signature on macOS), and installs it
/// as Wayfarer ends, starting the new version when the person chose Restart to Update. Only a copy that Wayfarer's
/// installer (Windows), disk image (macOS) or archive (Linux) put in place has one.</summary>
public abstract class Updater
{
    /// <summary>Where this copy is installed, for the log.</summary>
    public abstract string Installed { get; }

    /// <summary>The version made ready to install, or null.</summary>
    public string? Ready { get; protected set; }

    /// <summary>Why the last <see cref="Install"/> left the copy as it was, for the next start to say; null when it
    /// installed, or had nothing to install.</summary>
    public string? NotInstalled { get; protected set; }

    /// <summary>Whether this copy leaves a newer release to something else after all (a package manager), found off the
    /// caller's thread before anything is downloaded: null when it installs the release itself.</summary>
    public virtual Task<UpdatedElsewhere?> ElsewhereAsync(CancellationToken cancel) => Task.FromResult<UpdatedElsewhere?>(null);

    /// <summary>Removes what an earlier update left beside this copy (the release it replaced), as the copy starts.</summary>
    public virtual Task TidyAsync() => Task.CompletedTask;

    /// <summary>Downloads the release's package for this system and checks it, off the caller's thread, reporting the
    /// percent done. Never throws but for <paramref name="cancel"/>: a refusal or a failure is an answer.</summary>
    public async Task<UpdateOutcome> DownloadAsync(LatestRelease release, Action<int> progress, CancellationToken cancel)
    {
        Ready = null;
        try
        {
            UpdateOutcome outcome = await PrepareAsync(release, progress, cancel).ConfigureAwait(false);
            if (outcome is UpdateOutcome.Ready)
            {
                Ready = release.Version;
            }
            return outcome;
        }
        catch (UpdateRefusedException refused)
        {
            await DiscardAsync().ConfigureAwait(false);
            return new UpdateOutcome.Refused(refused.Message,
                $"Wayfarer Wanderer {release.Version} was not installed: {refused.Message}{(refused.Detail is { } detail ? $" ({detail})" : string.Empty)}");
        }
        catch (Exception error) when (error is UpdateFailedException or IOException or UnauthorizedAccessException or TimeoutException
            or System.ComponentModel.Win32Exception)
        {
            await DiscardAsync().ConfigureAwait(false);
            return new UpdateOutcome.Failed(error.Message, $"could not download Wayfarer Wanderer {release.Version}: {error.Message}");
        }
    }

    /// <summary>What <see cref="DownloadAsync"/> does for this system: throws <see cref="UpdateRefusedException"/> for a
    /// package that is not what the release says, <see cref="UpdateFailedException"/> for one that would not come.</summary>
    protected abstract Task<UpdateOutcome> PrepareAsync(LatestRelease release, Action<int> progress, CancellationToken cancel);

    /// <summary>Installs the version made ready, as the process ends (its window closed, what it started ended), and
    /// starts it when <paramref name="restart"/>. A failure is logged, and leaves the copy as it was.</summary>
    public abstract void Install(bool restart);

    /// <summary>Drops a download made or under way, as Check for updates turned off asks.</summary>
    public abstract Task DiscardAsync();

    /// <summary>The other processes running from this install, by id: each system's own look.</summary>
    public abstract IReadOnlyList<int> OtherProcesses();

    /// <summary>This system's package of a release and the release's SHA256SUMS, or null where it carries either not, which
    /// <see cref="Announced"/> says.</summary>
    protected static ReleasePackage? PackageOf(LatestRelease release) =>
        Releases.ThisSystemsAsset() is { } asset && release.File(asset) is { } download && release.File(ReleaseSums.Name) is { } sums
            ? new ReleasePackage(download, sums)
            : null;

    /// <summary>A release only announced, saying what it does not carry: this system's package, <paramref name="kind"/> where
    /// this processor has no asset's name, else its SHA256SUMS.</summary>
    protected static UpdateOutcome.Unavailable Announced(LatestRelease release, string kind)
    {
        string? asset = Releases.ThisSystemsAsset();
        string missing = asset is null || release.File(asset) is null ? asset ?? kind : ReleaseSums.Name;
        return new UpdateOutcome.Unavailable($"Wayfarer Wanderer {release.Version} is only announced: its release carries no {missing}");
    }

    /// <summary>Downloads a release's package into <paramref name="folder"/>, checked against the SHA-256 the release's
    /// SHA256SUMS gives it: the file saved and that SHA-256. An <see cref="UpdateRefusedException"/> where the sums give
    /// none or the download's differs; the file is the caller's to remove then.</summary>
    protected static async Task<(string Saved, string Sha256)> DownloadCheckedAsync(ReleasePackage package, string folder, Action<int> progress, CancellationToken cancel)
    {
        string name = package.Download.Name;
        string expected = await SumAsync(package, cancel).ConfigureAwait(false);
        string saved = Path.Combine(folder, name);
        string actual = await Releases.DownloadAsync(package.Download, saved, progress, cancel).ConfigureAwait(false);
        if (actual != expected)
        {
            throw new UpdateRefusedException($"{name} does not match the SHA-256 the release's {ReleaseSums.Name} gives", $"it is {actual}; {ReleaseSums.Name} says {expected}");
        }
        return (saved, expected);
    }

    /// <summary>The SHA-256 the release's SHA256SUMS gives its package for this system; an
    /// <see cref="UpdateRefusedException"/> where it gives none.</summary>
    protected static async Task<string> SumAsync(ReleasePackage package, CancellationToken cancel) =>
        ReleaseSums.Of(await Releases.ReadTextAsync(package.Sums, cancel).ConfigureAwait(false), package.Download.Name)
            ?? throw new UpdateRefusedException($"the release's {ReleaseSums.Name} gives no SHA-256 for {package.Download.Name}");

    /// <summary>The other Wayfarer processes whose executable is <paramref name="executable"/>, as the system names a
    /// process's executable, links resolved: the look of macOS and Linux.</summary>
    protected static IReadOnlyList<int> RunningAs(string executable)
    {
        string named = PathNames.Canonical(executable);
        List<int> here = [];
        foreach (Process running in Process.GetProcesses())
        {
            using (running)
            {
                if (running.Id != Environment.ProcessId && Libc.ExecutableOf(running.Id) is { } path && PathNames.Same(path, named))
                {
                    here.Add(running.Id);
                }
            }
        }
        return here;
    }

    /// <summary>Whether other processes running from <paramref name="from"/> hold the install of
    /// <paramref name="version"/> back, each named in the log and in why it did not install (<see cref="NotInstalled"/>).</summary>
    protected bool HeldBack(IReadOnlyList<int> others, string version, string from)
    {
        if (others.Count == 0)
        {
            return false;
        }
        string named = string.Join(", ", others);
        IdeErrors.Write($"Updates: Wayfarer Wanderer {version} waits to install: another Wayfarer runs from {from}: {named}");
        NotInstalled = $"another Wayfarer ran from {from} ({named})";
        return true;
    }

    /// <summary>Removes a folder an update made, a failure logged.</summary>
    protected static void Remove(string? folder)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Updates: {folder} could not be removed: {error.Message}");
        }
    }

    /// <summary>A tool run where a failure to start or finish is an exit code of -1 saying why.</summary>
    protected static async Task<ChildResult> ToolAsync(ProcessStartInfo command, TimeSpan limit, CancellationToken cancel)
    {
        try
        {
            return await ChildProcesses.RunAsync(command, limit: limit, cancel: cancel).ConfigureAwait(false);
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            return new ChildResult(-1, string.Empty, error.Message);
        }
    }

    /// <summary>What a copy that never installs a release itself, whatever put it in place, says of a newer one: a portable
    /// one (<see cref="ThisCopy.Data"/>), its new build put in its place by hand, and one a package manager installed
    /// (<see cref="ThisCopy.PackageManager"/>), that manager's to update. Null for any other.</summary>
    public static UpdatedElsewhere? LeftElsewhere() =>
        ThisCopy.Data is { } data ? Portable(data, Releases.ThisSystemsAsset(portable: true))
        : ThisCopy.PackageManager is { } manager ? Packaged(manager)
        : null;

    /// <summary>What a portable copy keeping its state in <paramref name="data"/> says of a newer release.</summary>
    public static UpdatedElsewhere Portable(string data, string? asset) =>
        new($"This copy of Wayfarer is portable, keeping its state in {data}, so it never replaces itself: "
            + $"put the release's {asset ?? "portable build"} in its place, keeping its {ThisCopy.DataName} folder.", Download: false);

    /// <summary>What a copy a package manager installed says of a newer release.</summary>
    public static UpdatedElsewhere Packaged(PackageManagerMark manager) =>
        new($"This copy of Wayfarer is {manager.Said}: {manager.Updates}.", Download: false);

    /// <summary>The updater of the running copy, or null with why it cannot update itself: portable or a package manager's
    /// (<see cref="LeftElsewhere"/>), not where Wayfarer's installer, disk image or archive put it, or where it cannot
    /// replace itself.</summary>
    public static Updater? OfThisCopy(string stateFolder, out string why)
    {
        if (LeftElsewhere() is { } elsewhere)
        {
            why = elsewhere.Why;
            return null;
        }
        if (OperatingSystem.IsMacOS())
        {
            return DiskImageUpdater.OfThisBundle(Path.Combine(stateFolder, "updates"), out why);
        }
        if (OperatingSystem.IsWindows())
        {
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "sq.version")) || !VelopackLocator.IsCurrentSet || VelopackLocator.Current.CurrentlyInstalledVersion is null)
            {
                why = $"{AppContext.BaseDirectory} is not where Wayfarer's installer put it";
                return null;
            }
            why = string.Empty;
            return new VelopackUpdater(VelopackLocator.Current);
        }
        if (OperatingSystem.IsLinux())
        {
            return LinuxUpdater.OfThisInstall(Path.Combine(stateFolder, "updates"), out why);
        }
        why = "Wayfarer installs updates itself on macOS, Windows and Linux";
        return null;
    }
}

/// <summary>A release's package for this system, and the SHA256SUMS that gives its SHA-256.</summary>
public sealed record ReleasePackage(ReleaseFile Download, ReleaseFile Sums);

/// <summary>A copy whose updates come from elsewhere: what the person is told in place of a download, and whether the
/// notice still links this system's download (a copy that would update itself once installed) or only the release's
/// page (a package manager's copy, which nothing else may write over).</summary>
public sealed record UpdatedElsewhere(string Why, bool Download);

/// <summary>What downloading an update came to, with a line for the IDE's log.</summary>
public abstract record UpdateOutcome(string Said)
{
    /// <summary>Downloaded and checked, to install as Wayfarer ends.</summary>
    public sealed record Ready(string Said) : UpdateOutcome(Said);

    /// <summary>The release carries nothing this copy installs (no package for this system), so it is only announced.</summary>
    public sealed record Unavailable(string Said) : UpdateOutcome(Said);

    /// <summary>Downloaded, but not what the release says it is: removed, and the person told why.</summary>
    public sealed record Refused(string Reason, string Said) : UpdateOutcome(Said);

    /// <summary>Not downloaded: no network, an error, a full disk.</summary>
    public sealed record Failed(string Reason, string Said) : UpdateOutcome(Said);
}
