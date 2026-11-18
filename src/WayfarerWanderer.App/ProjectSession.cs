using Avalonia.Threading;
using Passenger.Core;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

public sealed class ProjectSession : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private readonly WorkspaceImportSession importer = new();
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
    }

    public string Root { get; }

    public WorkspaceSnapshot? Snapshot => importer.State.Current;

    public BuildToolWindow BuildWindow { get; }

    public TestToolWindow TestWindow { get; }

    public DebugToolWindow DebugWindow { get; }

    public ILaunchConfigurationStore Launches => launches;

    public bool IsRunning => operation is { IsCancellationRequested: false };

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
        WorkspaceSnapshot snapshot = Snapshot ?? await ImportAsync(cancellationToken);
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
        WorkspaceSnapshot snapshot = Snapshot ?? await ImportAsync(cancellationToken);
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
        WorkspaceSnapshot snapshot = Snapshot ?? await ImportAsync(cancellationToken);
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
    }

    public void Trust() => WorkspaceTrust.Trust(Root);

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await CancelAsync();
        operation?.Dispose();
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
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }
        return next;
    }

    private void EndOperation(CancellationTokenSource ended)
    {
        if (Interlocked.CompareExchange(ref operation, null, ended) == ended)
        {
            ended.Dispose();
        }
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
