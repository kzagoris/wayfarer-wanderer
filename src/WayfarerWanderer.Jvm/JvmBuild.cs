using System.Diagnostics;
using System.Globalization;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

public sealed record JvmBuildRequest(string WorkspaceRoot, string Module, string Task, IReadOnlyList<string> AdditionalArguments);

public interface IJvmBuildAdapter
{
    JvmBuildSystem BuildSystem { get; }

    bool CanBuild(string workspaceRoot);

    /// <summary>The command that runs the request with the build the snapshot was imported from.</summary>
    ProcessStartInfo Command(WorkspaceSnapshot snapshot, JvmBuildRequest request);
}

public abstract class JvmBuildAdapter : IJvmBuildAdapter
{
    public abstract JvmBuildSystem BuildSystem { get; }

    protected abstract string WrapperName { get; }

    protected abstract string WindowsWrapperName { get; }

    public bool CanBuild(string workspaceRoot) => File.Exists(WrapperPath(workspaceRoot));

    public ProcessStartInfo Command(WorkspaceSnapshot snapshot, JvmBuildRequest request) => Command(request);

    /// <summary>The JDK table the build is told about (<see cref="BuildJdks"/>); the app's by default.</summary>
    public JdkRegistry Registry { get; init; } = JdkRegistry.Current;

    public ProcessStartInfo Command(JvmBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string wrapper = WrapperPath(request.WorkspaceRoot);
        if (!File.Exists(wrapper))
        {
            throw new FileNotFoundException($"The {BuildSystem} wrapper is missing. Configure an explicit build tool before running.", wrapper);
        }
        ProcessStartInfo start = ChildProcesses.Command(wrapper, [.. JdkArguments(request.WorkspaceRoot, Registry), .. Arguments(request)], request.WorkspaceRoot);
        Configure(start);
        return start;
    }

    protected abstract IReadOnlyList<string> Arguments(JvmBuildRequest request);

    /// <summary>The arguments that tell the build about the JDK table, ahead of the request's own.</summary>
    protected abstract IReadOnlyList<string> JdkArguments(string workspaceRoot, JdkRegistry registry);

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

    protected override IReadOnlyList<string> JdkArguments(string workspaceRoot, JdkRegistry registry) =>
        BuildJdks.MavenToolchainsFile(workspaceRoot, registry.All(), BuildJdks.StateFolder(registry)) is { } toolchains
            ? ["--global-toolchains", toolchains]
            : [];

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

    protected override IReadOnlyList<string> JdkArguments(string workspaceRoot, JdkRegistry registry) =>
        BuildJdks.GradleArgument(workspaceRoot, registry.All()) is { } paths ? [paths] : [];

    protected override IReadOnlyList<string> Arguments(JvmBuildRequest request)
    {
        string task = request.Module.Length > 0 && request.Module != "."
            ? $":{request.Module.Trim(':').Replace('\\', ':').Replace('/', ':')}:{request.Task.TrimStart(':')}"
            : request.Task;
        return [task, "--console=plain", .. request.AdditionalArguments];
    }
}

public sealed class IdeaJvmBuildAdapter : IJvmBuildAdapter
{
    public JvmBuildSystem BuildSystem => JvmBuildSystem.Idea;

    public bool CanBuild(string workspaceRoot) => true;

    public ProcessStartInfo Command(WorkspaceSnapshot snapshot, JvmBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Task == "test")
        {
            throw new InvalidDataException("IntelliJ course projects have no JUnit build.");
        }
        if (request.Task is not ("classes" or "compile"))
        {
            throw new InvalidDataException($"IntelliJ course projects have no '{request.Task}' task.");
        }
        JvmModule module = snapshot.Module(request.Module)
            ?? throw new InvalidDataException($"The module '{request.Module}' is not in snapshot {snapshot.Generation}.");
        JvmToolchain toolchain = module.CompileToolchain;
        if (string.IsNullOrWhiteSpace(toolchain.Home) || !Directory.Exists(toolchain.Home))
        {
            throw new InvalidDataException(MissingJdk(toolchain.Vendor));
        }
        string output = module.Classpath.Runtime.FirstOrDefault()
            ?? throw new InvalidDataException($"Module '{module.Name}' has no output directory.");
        List<string> sources = JavaSources(module);
        if (sources.Count == 0)
        {
            throw new InvalidDataException($"Module '{module.Name}' has no Java sources to compile.");
        }
        Directory.CreateDirectory(output);
        List<string> arguments = [];
        if (toolchain.LanguageVersion is int level)
        {
            arguments.Add("--release");
            arguments.Add(level.ToString(CultureInfo.InvariantCulture));
        }
        // Line numbers, source names and local variables, so the debugger shows a frame's variables as IntelliJ's build does.
        arguments.Add("-g");
        arguments.Add("-d");
        arguments.Add(output);
        if (module.Classpath.Compile.Count > 0)
        {
            arguments.Add("-cp");
            arguments.Add(string.Join(Path.PathSeparator, module.Classpath.Compile));
        }
        arguments.AddRange(sources);
        string javac = Path.Combine(toolchain.Home, "bin", OperatingSystem.IsWindows() ? "javac.exe" : "javac");
        return ChildProcesses.Command(javac, arguments, module.Root);
    }

    /// <summary>The modules a module's classes need compiled first, the deepest first: those whose classes its main
    /// compile classpath names (another module's <c>name:main</c>), theirs in turn.</summary>
    public static IReadOnlyList<JvmModule> CompileOrder(WorkspaceSnapshot snapshot, JvmModule module)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(module);
        List<JvmModule> order = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        void Visit(JvmModule current)
        {
            if (!seen.Add(current.Id))
            {
                return;
            }
            foreach (JvmClasspathEntry entry in current.SourceSet("main")?.CompileClasspath ?? [])
            {
                if (entry.ModuleRef is { } reference && reference.EndsWith(":main", StringComparison.Ordinal)
                    && snapshot.Module(reference[..^":main".Length]) is { } dependency && dependency.Id != current.Id)
                {
                    Visit(dependency);
                }
            }
            order.Add(current);
        }
        Visit(module);
        return order;
    }

    /// <summary>Whether a module has Java sources to compile.</summary>
    public static bool HasSources(JvmModule module) => JavaSources(module).Count > 0;

    /// <summary>Why an IntelliJ module has no JDK to compile with: the JDK it names, and JAVA_HOME.</summary>
    public static string MissingJdk(string? jdkName)
    {
        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        string named = string.IsNullOrWhiteSpace(jdkName) ? "(none)" : jdkName;
        string fallback = string.IsNullOrWhiteSpace(javaHome) ? "(not set)" : javaHome;
        return $"The IntelliJ JDK '{named}' was not found. JAVA_HOME is '{fallback}'.";
    }

    private static List<string> JavaSources(JvmModule module)
    {
        List<string> files = [];
        foreach (JvmSourceRoot root in module.SourceRoots)
        {
            if (root.Kind is not (JvmSourceKind.MainJava or JvmSourceKind.MainKotlin) || !Directory.Exists(root.Path))
            {
                continue;
            }
            files.AddRange(Directory.EnumerateFiles(root.Path, "*.java", SearchOption.AllDirectories));
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }
}

public sealed class JvmBuild
{
    private readonly IChildProcessRunner runner;
    private readonly IReadOnlyList<IJvmBuildAdapter> adapters;

    public JvmBuild(IChildProcessRunner? runner = null)
        : this(runner ?? new ChildProcessRunner(), [new MavenJvmBuildAdapter(), new GradleJvmBuildAdapter(), new IdeaJvmBuildAdapter()])
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
        if (adapter is IdeaJvmBuildAdapter && snapshot.Module(request.Module) is { } target)
        {
            // An IntelliJ module's dependency modules compile first, each with javac, as IntelliJ's build does.
            foreach (JvmModule dependency in IdeaJvmBuildAdapter.CompileOrder(snapshot, target).Where(m => m.Id != target.Id && IdeaJvmBuildAdapter.HasSources(m)))
            {
                ProcessResult compiled = await runner.RunAsync(adapter.Command(snapshot, request with { Module = dependency.Id }), output, cancellationToken).ConfigureAwait(false);
                if (compiled.ExitCode != 0)
                {
                    return compiled;
                }
            }
        }
        ProcessStartInfo start = adapter.Command(snapshot, request);
        // The wrapper runs on the JDK the import ran it on, never on the tooling JVM that runs the host.
        JvmModule? module = snapshot.Module(request.Module) ?? snapshot.Modules.FirstOrDefault();
        ProjectJdk.Apply(start, module?.ImportToolchain.Home);
        return await runner.RunAsync(start, output, cancellationToken).ConfigureAwait(false);
    }
}
