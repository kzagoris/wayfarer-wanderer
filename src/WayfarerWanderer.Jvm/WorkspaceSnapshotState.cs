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

public sealed class WorkspaceImportSession
{
    private readonly IWorkspaceImporter importer;
    private readonly object gate = new();
    private CancellationTokenSource? currentCancellation;

    public WorkspaceImportSession(IWorkspaceImporter? importer = null)
    {
        this.importer = importer ?? new WorkspaceImporter();
    }

    public WorkspaceSnapshotState State { get; } = new();

    public async Task<WorkspaceSnapshot?> RefreshAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
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
            return State.Accept(snapshot) ? snapshot : null;
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
