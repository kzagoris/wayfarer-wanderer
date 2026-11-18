using System.Diagnostics;

namespace WayfarerWanderer.Jvm;

public interface IJvmDebugAdapter
{
    string Id { get; }

    ProcessStartInfo Command(WorkspaceSnapshot snapshot, JvmLaunchConfiguration configuration);
}

public sealed class JvmDebugSession
{
    private readonly IChildProcessRunner runner;
    private readonly IJvmDebugAdapter adapter;

    public JvmDebugSession(IJvmDebugAdapter adapter, IChildProcessRunner? runner = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.runner = runner ?? new ChildProcessRunner();
    }

    public string AdapterId => adapter.Id;

    public ProcessStartInfo Command(WorkspaceSnapshot snapshot, JvmLaunchConfiguration configuration) =>
        adapter.Command(snapshot, configuration);

    public async Task<ProcessResult> RunAsync(
        WorkspaceSnapshot snapshot,
        JvmLaunchConfiguration configuration,
        Action<ProcessOutput>? output = null,
        CancellationToken cancellationToken = default)
    {
        Passenger.Core.WorkspaceTrust.Check(snapshot.Root);
        return await runner.RunAsync(adapter.Command(snapshot, configuration), output, cancellationToken).ConfigureAwait(false);
    }
}
