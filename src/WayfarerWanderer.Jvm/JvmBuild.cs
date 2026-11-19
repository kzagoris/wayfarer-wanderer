using System.Diagnostics;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

public sealed record JvmBuildRequest(string WorkspaceRoot, string Module, string Task, IReadOnlyList<string> AdditionalArguments);

public interface IJvmBuildAdapter
{
    JvmBuildSystem BuildSystem { get; }

    bool CanBuild(string workspaceRoot);

    ProcessStartInfo Command(JvmBuildRequest request);
}

public abstract class JvmBuildAdapter : IJvmBuildAdapter
{
    public abstract JvmBuildSystem BuildSystem { get; }

    protected abstract string WrapperName { get; }

    protected abstract string WindowsWrapperName { get; }

    public bool CanBuild(string workspaceRoot) => File.Exists(WrapperPath(workspaceRoot));

    public ProcessStartInfo Command(JvmBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string wrapper = WrapperPath(request.WorkspaceRoot);
        if (!File.Exists(wrapper))
        {
            throw new FileNotFoundException($"The {BuildSystem} wrapper is missing. Configure an explicit build tool before running.", wrapper);
        }
        ProcessStartInfo start = ChildProcesses.Command(wrapper, Arguments(request), request.WorkspaceRoot);
        Configure(start);
        return start;
    }

    protected abstract IReadOnlyList<string> Arguments(JvmBuildRequest request);

    protected virtual void Configure(ProcessStartInfo start)
    {
    }

    protected string WrapperPath(string workspaceRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Path.GetFullPath(Path.Combine(workspaceRoot, WrapperName));
        }
        string windows = Path.GetFullPath(Path.Combine(workspaceRoot, WindowsWrapperName));
        return File.Exists(windows) ? windows : Path.GetFullPath(Path.Combine(workspaceRoot, WrapperName));
    }
}

public sealed class MavenJvmBuildAdapter : JvmBuildAdapter
{
    public override JvmBuildSystem BuildSystem => JvmBuildSystem.Maven;

    protected override string WrapperName => "mvnw";

    protected override string WindowsWrapperName => "mvnw.cmd";

    protected override void Configure(ProcessStartInfo start)
    {
        start.Environment.Remove("MAVEN_ARGS");
        start.Environment.Remove("MAVEN_OPTS");
    }

    protected override IReadOnlyList<string> Arguments(JvmBuildRequest request)
    {
        List<string> arguments = [];
        if (request.Module.Length > 0 && request.Module != ".")
        {
            arguments.AddRange(["-pl", request.Module, "-am"]);
        }
        arguments.Add(request.Task == "classes" ? "compile" : request.Task);
        arguments.AddRange(request.AdditionalArguments);
        return arguments;
    }
}

public sealed class GradleJvmBuildAdapter : JvmBuildAdapter
{
    public override JvmBuildSystem BuildSystem => JvmBuildSystem.Gradle;

    protected override string WrapperName => "gradlew";

    protected override string WindowsWrapperName => "gradlew.bat";

    protected override IReadOnlyList<string> Arguments(JvmBuildRequest request)
    {
        string task = request.Module.Length > 0 && request.Module != "."
            ? $":{request.Module.Trim(':').Replace('\\', ':').Replace('/', ':')}:{request.Task.TrimStart(':')}"
            : request.Task;
        return [task, "--console=plain", .. request.AdditionalArguments];
    }
}

public sealed class JvmBuild
{
    private readonly IChildProcessRunner runner;
    private readonly IReadOnlyList<IJvmBuildAdapter> adapters;

    public JvmBuild(IChildProcessRunner? runner = null)
        : this(runner ?? new ChildProcessRunner(), [new MavenJvmBuildAdapter(), new GradleJvmBuildAdapter()])
    {
    }

    public JvmBuild(IChildProcessRunner runner, IReadOnlyList<IJvmBuildAdapter> adapters)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
    }

    public async Task<ProcessResult> RunAsync(
        WorkspaceSnapshot snapshot,
        JvmBuildRequest request,
        Action<ProcessOutput>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        WorkspaceTrust.Check(request.WorkspaceRoot);
        if (snapshot.Module(request.Module) is { } requestedModule &&
            Passenger.Core.PathNames.Same(requestedModule.Root, request.WorkspaceRoot))
        {
            request = request with { Module = "." };
        }
        IJvmBuildAdapter adapter = adapters.FirstOrDefault(candidate => candidate.BuildSystem == snapshot.BuildSystem && candidate.CanBuild(request.WorkspaceRoot))
            ?? throw new FileNotFoundException($"No {snapshot.BuildSystem} wrapper was found in '{request.WorkspaceRoot}'.");
        ProcessStartInfo start = adapter.Command(request);
        ProjectJdk.Apply(start, snapshot.ToolingToolchain.Home);
        return await runner.RunAsync(start, output, cancellationToken).ConfigureAwait(false);
    }
}
