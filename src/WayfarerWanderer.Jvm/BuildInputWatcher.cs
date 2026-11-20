using Passenger.Core;

namespace WayfarerWanderer.Jvm;

/// <summary>
/// Watches the files a workspace's evaluation read (<see cref="WorkspaceSnapshot.BuildInputs"/>), plus the root build
/// files, and says once, a debounce after the last change, that the model may no longer stand. A file that comes, goes
/// or is written counts; one only touched (written with the same stamp) does not.
/// </summary>
public sealed class BuildInputWatcher : IDisposable
{
    private static readonly string[] RootFiles = ["pom.xml", "settings.gradle", "settings.gradle.kts", "build.gradle", "build.gradle.kts"];

    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Dictionary<string, JvmBuildInput?> watched = new(PathNames.Comparer);
    private readonly TimeSpan debounce;
    private readonly object gate = new();
    private readonly HashSet<string> pending = new(PathNames.Comparer);
    private Timer? timer;
    private bool disposed;

    public BuildInputWatcher(WorkspaceSnapshot snapshot, TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        foreach (JvmBuildInput input in snapshot.BuildInputs)
        {
            watched[Path.GetFullPath(input.Path)] = input;
        }
        foreach (string name in RootFiles)
        {
            watched.TryAdd(Path.GetFullPath(Path.Combine(snapshot.Root, name)), null);
        }
        foreach (IGrouping<string, string> folder in watched.Keys.GroupBy(path => Path.GetDirectoryName(path)!, PathNames.Comparer))
        {
            Watch(folder.Key);
        }
    }

    /// <summary>Raised on a timer's thread, once per burst of changes.</summary>
    public event Action? Changed;

    public IReadOnlyCollection<string> Files => watched.Keys;

    private void Watch(string folder)
    {
        // The nearest folder that exists, so a file whose folder comes later (gradle/libs.versions.toml) is still seen.
        string existing = folder;
        bool deep = false;
        while (!Directory.Exists(existing))
        {
            string? parent = Path.GetDirectoryName(existing);
            if (parent is null)
            {
                return;
            }
            existing = parent;
            deep = true;
        }
        if (watchers.Any(w => PathNames.Same(w.Path, existing) && (w.IncludeSubdirectories || !deep)))
        {
            return;
        }
        FileSystemWatcher watcher = new(existing)
        {
            IncludeSubdirectories = deep,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
        };
        watcher.Changed += (_, change) => Seen(change.FullPath);
        watcher.Created += (_, change) => Seen(change.FullPath);
        watcher.Deleted += (_, change) => Seen(change.FullPath);
        watcher.Renamed += (_, change) =>
        {
            Seen(change.OldFullPath);
            Seen(change.FullPath);
        };
        watcher.EnableRaisingEvents = true;
        watchers.Add(watcher);
    }

    private void Seen(string path)
    {
        string full = Path.GetFullPath(path);
        if (!watched.ContainsKey(full))
        {
            return;
        }
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            pending.Add(full);
            timer ??= new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            timer.Change(debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        string[] seen;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            seen = [.. pending];
            pending.Clear();
        }
        // Judged once the burst is over: an editor that saves the same bytes, or a touch put back, leaves every stamp
        // the evaluation read. A root build file the evaluation did not read always counts.
        if (seen.All(path => watched[path] is { } input && WorkspaceModelCache.Stands(input)))
        {
            return;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            timer?.Dispose();
        }
        foreach (FileSystemWatcher watcher in watchers)
        {
            watcher.Dispose();
        }
    }
}
