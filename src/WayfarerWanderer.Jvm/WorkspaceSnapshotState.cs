using Passenger.Core;

namespace WayfarerWanderer.Jvm;

public sealed class WorkspaceSnapshotState
{
    private readonly object gate = new();
    private long requestedGeneration;
    private WorkspaceSnapshot? current;
    private bool stale;

    public WorkspaceSnapshot? Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public bool IsStale
    {
        get
        {
            lock (gate)
            {
                return stale;
            }
        }
    }

    public long BeginImport()
    {
        lock (gate)
        {
            long generation = Interlocked.Increment(ref requestedGeneration);
            stale = current is not null;
            return generation;
        }
    }

    public bool Accept(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            if (snapshot.Generation != Volatile.Read(ref requestedGeneration))
            {
                return false;
            }
            current = snapshot;
            stale = false;
            return true;
        }
    }

    /// <summary>Shows a model kept from an earlier run before any import of this one: current when every file its
    /// evaluation read still stands, else stale. Refused once a snapshot or an import of this run exists.</summary>
    public bool Restore(WorkspaceSnapshot snapshot, bool isCurrent)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            if (current is not null || requestedGeneration != 0)
            {
                return false;
            }
            current = snapshot;
            stale = !isCurrent;
            return true;
        }
    }

    public bool Reject(long generation)
    {
        lock (gate)
        {
            if (generation != requestedGeneration || current is null)
            {
                return false;
            }
            stale = true;
            return true;
        }
    }
}

public sealed class WorkspaceImportSession : IDisposable
{
    private readonly IWorkspaceImporter importer;
    private readonly string? cacheFile;
    private readonly TimeSpan? debounce;
    private readonly object gate = new();
    private CancellationTokenSource? currentCancellation;
    private BuildInputWatcher? watcher;
    private bool disposed;

    /// <param name="importer">What evaluates the workspace; the JVM project host when null.</param>
    /// <param name="cacheFile">Where the last good model is kept for a warm reopen (<see cref="WorkspaceModelCache"/>);
    /// null keeps none. The default importer writes it after each good import.</param>
    /// <param name="debounce">How long the build inputs must be quiet before <see cref="InputsChanged"/>.</param>
    public WorkspaceImportSession(IWorkspaceImporter? importer = null, string? cacheFile = null, TimeSpan? debounce = null)
    {
        this.importer = importer ?? new ProjectHostImporter { CacheFile = cacheFile };
        this.cacheFile = cacheFile;
        this.debounce = debounce;
    }

    public WorkspaceSnapshotState State { get; } = new();

    /// <summary>Raised a debounce after a file the current model was evaluated from changed: the model may no longer
    /// stand, and whoever owns the session reimports.</summary>
    public event Action? InputsChanged;

    /// <summary>
    /// Opens a trusted workspace: the model kept from the last run, when every file its evaluation read is unchanged, is
    /// current and no import runs; a kept model that no longer stands is shown stale while an import runs; with none
    /// kept, it imports. Nothing is read before trust: an untrusted folder throws as an import would.
    /// </summary>
    public async Task<WorkspaceSnapshot?> OpenAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        WorkspaceTrust.Check(workspaceRoot);
        if (cacheFile is not null && WorkspaceModelCache.Read(cacheFile, workspaceRoot) is { } kept && State.Restore(kept.Snapshot, kept.Current))
        {
            Watch(kept.Snapshot);
            if (kept.Current)
            {
                return kept.Snapshot;
            }
        }
        return await RefreshAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
    }

    private void Watch(WorkspaceSnapshot snapshot)
    {
        if (!Directory.Exists(snapshot.Root))
        {
            return;
        }
        BuildInputWatcher next = new(snapshot, debounce);
        next.Changed += () => InputsChanged?.Invoke();
        BuildInputWatcher? previous;
        lock (gate)
        {
            if (disposed)
            {
                next.Dispose();
                return;
            }
            previous = watcher;
            watcher = next;
        }
        previous?.Dispose();
    }

    public void Dispose()
    {
        BuildInputWatcher? last;
        lock (gate)
        {
            disposed = true;
            last = watcher;
            watcher = null;
        }
        last?.Dispose();
    }

    private Task running = Task.CompletedTask;

    /// <summary>Whether an import is running now.</summary>
    public bool IsImporting => !Volatile.Read(ref running).IsCompleted;

    /// <summary>Done once no import runs: what Build, Test and Run wait for, so a build of the same folder never runs
    /// beside the import's (which would make Gradle start a second daemon). A failed import counts as done.</summary>
    public async Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        while (Volatile.Read(ref running) is { IsCompleted: false } current)
        {
            await Task.WhenAny(current).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<WorkspaceSnapshot?> RefreshAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        Task<WorkspaceSnapshot?> import = RefreshCoreAsync(workspaceRoot, cancellationToken);
        Volatile.Write(ref running, import);
        return import;
    }

    private async Task<WorkspaceSnapshot?> RefreshCoreAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        long generation = State.BeginImport();
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (gate)
        {
            previous = currentCancellation;
            currentCancellation = linked;
        }
        if (previous is not null)
        {
            await previous.CancelAsync().ConfigureAwait(false);
        }
        try
        {
            WorkspaceSnapshot snapshot = await importer.ImportAsync(workspaceRoot, generation, linked.Token).ConfigureAwait(false);
            if (!State.Accept(snapshot))
            {
                return null;
            }
            Watch(snapshot);
            return snapshot;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            State.Reject(generation);
            throw;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(currentCancellation, linked))
                {
                    currentCancellation = null;
                }
            }
            linked.Dispose();
        }
    }

    public async Task CancelAsync()
    {
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            cancellation = currentCancellation;
        }
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
    }
}
