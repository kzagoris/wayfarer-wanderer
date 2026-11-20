using Avalonia.Threading;
using Passenger.Core;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.State;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

public sealed partial class ProjectSession : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private Task? disposal;
    private readonly WorkspaceImportSession importer;
    private readonly JvmBuild build = new();
    private readonly JvmLaunch launch = new();
    private readonly LaunchConfigurationStore launches;

    public ProjectSession(string root, BuildToolWindow buildWindow, TestToolWindow testWindow, DebugToolWindow debugWindow)
    {
        Root = Path.GetFullPath(root);
        BuildWindow = buildWindow;
        TestWindow = testWindow;
        DebugWindow = debugWindow;
        launches = new LaunchConfigurationStore(Path.Combine(Root, ".wayfarer", "launches"));
        // The last good model is kept in the IDE's state for this folder, never in the repository.
        string? cacheFile = EditorApplication.State is { } state
            ? Path.Combine(ProjectState.FolderOf(state.Root, Root), WorkspaceModelCache.FileName)
            : null;
        importer = new WorkspaceImportSession(cacheFile: cacheFile);
        importer.InputsChanged += () => IdeErrors.Observe(ReimportAsync());
    }

    /// <summary>Raised on the UI thread when an import the window did not ask for (a build file changed) ends with a
    /// model, current or kept stale.</summary>
    public event Action? SnapshotChanged;

    /// <summary>Whether the model shown is older than the build files: an import failed or is running.</summary>
    public bool IsStale => importer.State.IsStale;

    /// <summary>Opens the trusted folder with the model kept from the last run when it still stands, else imports it.</summary>
    public async Task<WorkspaceSnapshot?> OpenAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource combined = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token, cancellationToken);
        return await importer.OpenAsync(Root, combined.Token);
    }

    private async Task ReimportAsync()
    {
        try
        {
            await ImportAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (!lifetime.IsCancellationRequested)
            {
                OnUi(() => SnapshotChanged?.Invoke());
            }
        }
    }

    /// <summary>The model a build, test or run uses: once any import of this folder has ended, so the two never run side
    /// by side; the window's Cancel ends the wait.</summary>
    private async Task<WorkspaceSnapshot> SnapshotForWorkAsync(CancellationToken cancellationToken)
    {
        await importer.WhenIdleAsync(cancellationToken);
        return Snapshot ?? await ImportAsync(cancellationToken);
    }

    public string Root { get; }

    public WorkspaceSnapshot? Snapshot => importer.State.Current;

    public BuildToolWindow BuildWindow { get; }

    public TestToolWindow TestWindow { get; }

    public DebugToolWindow DebugWindow { get; }

    public ILaunchConfigurationStore Launches => launches;

    public bool IsRunning => operation is not null;

    public bool IsTrusted => WorkspaceTrust.Trusts(Root);

    public async Task<WorkspaceSnapshot> ImportAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource combined = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token, cancellationToken);
        WorkspaceSnapshot snapshot = await importer.RefreshAsync(Root, combined.Token)
            ?? throw new OperationCanceledException(combined.Token);
        return snapshot;
    }

    public async Task<ProcessResult> BuildAsync(
        string module,
        string task = "classes",
        IReadOnlyList<string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSnapshot snapshot = await SnapshotForWorkAsync(cancellationToken);
        CancellationTokenSource running = BeginOperation(cancellationToken);
        try
        {
            BuildWindow.Reset();
            return await build.RunAsync(
                snapshot,
                new JvmBuildRequest(Root, module, task, arguments ?? []),
                output => OnUi(() => BuildWindow.Append(output.Line)),
                running.Token);
        }
        finally
        {
            EndOperation(running);
        }
    }

    public async Task<TestRunResult> TestAsync(
        TestSelector selector,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSnapshot snapshot = await SnapshotForWorkAsync(cancellationToken);
        TestInvocation invocation = new(Path.Combine(Root, ".wayfarer", "test-results"), Root, selector);
        CancellationTokenSource running = BeginOperation(cancellationToken);
        TestWindow.Reset();
        ProcessResult process;
        bool cancelled = false;
        try
        {
            process = await build.RunAsync(
                snapshot,
                invocation.BuildRequest(),
                output => OnUi(() => BuildWindow.Append(output.Line)),
                running.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            throw;
        }
        finally
        {
            EndOperation(running);
        }
        TestRunResult result = invocation.Complete(process.ExitCode, cancelled);
        OnUi(() => TestWindow.Show(result));
        return result;
    }

    public async Task<ProcessResult> RunAsync(
        JvmLaunchConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSnapshot snapshot = await SnapshotForWorkAsync(cancellationToken);
        CancellationTokenSource running = BeginOperation(cancellationToken);
        try
        {
            BuildWindow.Reset();
            return await launch.RunAsync(
                snapshot,
                configuration,
                output => OnUi(() => BuildWindow.Append(output.Line)),
                running.Token);
        }
        finally
        {
            EndOperation(running);
        }
    }

    public async Task CancelAsync()
    {
        await importer.CancelAsync();
        CancellationTokenSource? current = operation;
        if (current is not null)
        {
            await current.CancelAsync();
        }
        if (debugWork is { } cleanup) await cleanup;
    }

    public void Trust() => WorkspaceTrust.Trust(Root);

    public ValueTask DisposeAsync() => new(disposal ??= DisposeSessionAsync());

    private async Task DisposeSessionAsync()
    {
        await lifetime.CancelAsync();
        await CancelAsync();
        // The window lets its folder go: a project host that serves no other window's folder ends with it.
        importer.Dispose();
        await ProjectHosts.ReleaseAsync(Root);
        lifetime.Dispose();
    }

    private CancellationTokenSource BeginOperation(CancellationToken cancellationToken)
    {
        if (lifetime.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(ProjectSession));
        }
        CancellationTokenSource next = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token, cancellationToken);
        CancellationTokenSource? previous = Interlocked.Exchange(ref operation, next);
        previous?.Cancel();
        return next;
    }

    private void EndOperation(CancellationTokenSource ended)
    {
        Interlocked.CompareExchange(ref operation, null, ended);
        ended.Dispose();
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
