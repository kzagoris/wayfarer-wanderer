using System.Diagnostics;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

public sealed record JvmLaunchConfiguration
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = string.Empty;

    public string Module { get; init; } = string.Empty;

    public string MainClass { get; init; } = string.Empty;

    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>The JDK this configuration runs on, by its name in the JDK table, never a path, so a shared
    /// configuration travels as <c>project.json</c> does; empty for the module's runtime JDK. A name the table lacks is
    /// met by a JDK of the release the name carries (<see cref="LaunchJdks"/>).</summary>
    public string Jdk { get; init; } = string.Empty;

    /// <summary>A JDK home an older configuration recorded; read for that configuration only. New ones name a
    /// <see cref="Jdk"/>.</summary>
    public string JavaHome { get; init; } = string.Empty;

    public string Profile { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string BuildTask { get; init; } = string.Empty;

    public IReadOnlyList<string> BuildArguments { get; init; } = [];
}

public interface IJvmLaunchAdapter
{
    ProcessStartInfo Command(WorkspaceSnapshot snapshot, JvmLaunchConfiguration configuration);
}

public sealed class DirectJvmLaunchAdapter : IJvmLaunchAdapter
{
    public ProcessStartInfo Command(WorkspaceSnapshot snapshot, JvmLaunchConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(configuration);
        JvmModule module = snapshot.Module(configuration.Module)
            ?? throw new InvalidDataException($"The launch module '{configuration.Module}' is not in snapshot {snapshot.Generation}.");
        if (!module.MainClasses.Any(main => main.ClassName == configuration.MainClass))
        {
            throw new InvalidDataException($"The main class '{configuration.MainClass}' is not in module '{module.Name}'.");
        }

        string javaHome = configuration.Jdk.Length > 0
            ? LaunchJdks.Home(module, configuration, JdkRegistry.Current)
            : FirstNotEmpty(configuration.JavaHome, module.RuntimeToolchain.Home, module.CompileToolchain.Home, module.ImportToolchain.Home);
        if (javaHome.Length == 0)
        {
            throw new InvalidDataException("The launch configuration has no project JDK.");
        }
        string java = Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (module.Classpath.Runtime.Count == 0 && module.Classpath.Compile.Count == 0)
        {
            throw new InvalidDataException($"Module '{module.Name}' has no resolved runtime or compile classpath.");
        }
        List<string> arguments = [];
        if (configuration.Profile.Length > 0)
        {
            arguments.Add("-Dspring.profiles.active=" + configuration.Profile);
        }
        arguments.Add("-cp");
        IReadOnlyList<string> classpath = module.Classpath.Runtime.Count > 0
            ? module.Classpath.Runtime
            : module.Classpath.Compile;
        arguments.Add(string.Join(Path.PathSeparator, classpath));
        arguments.Add(configuration.MainClass);
        arguments.AddRange(configuration.Arguments);
        string workingDirectory = configuration.WorkingDirectory.Length > 0
            ? Path.GetFullPath(configuration.WorkingDirectory)
            : module.Root;
        return ChildProcesses.Command(java, arguments, workingDirectory, configuration.Environment);
    }

    /// <summary>Compiles the module with the project's build (its classes and those it depends on), then launches the main
    /// class directly on the runtime classpath and JDK the snapshot holds.</summary>
    public static async Task<ProcessStartInfo> ResolvedCommandAsync(
        WorkspaceSnapshot snapshot,
        JvmLaunchConfiguration configuration,
        CancellationToken cancellationToken = default,
        JvmBuild? build = null,
        Action<ProcessOutput>? output = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(configuration);
        WorkspaceTrust.Check(snapshot.Root);
        JvmModule module = snapshot.Module(configuration.Module)
            ?? throw new InvalidDataException($"Unknown module '{configuration.Module}'.");
        ProcessResult compiled = await (build ?? new JvmBuild()).RunAsync(
            snapshot, new JvmBuildRequest(snapshot.Root, module.Id, "classes", []), output, cancellationToken).ConfigureAwait(false);
        if (compiled.ExitCode != 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, compiled.Output.TakeLast(16).Select(line => line.Line)));
        }
        if (module.Classpath.Runtime.Count == 0 || module.Classpath.Runtime.Any(path => !Path.IsPathFullyQualified(path)))
        {
            throw new InvalidDataException($"Module '{module.Name}' has no runtime classpath in snapshot {snapshot.Generation}.");
        }
        return new DirectJvmLaunchAdapter().Command(snapshot, configuration);
    }

    private static string FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}

public sealed class WrapperJvmLaunchAdapter : IJvmLaunchAdapter
{
    /// <summary>
    /// Maven 3 runs a CLI goal on every project in the reactor, so with <c>-pl module -am</c> it also runs
    /// <c>exec:java</c> or <c>spring-boot:run</c> on the aggregator and libraries. Without <c>-am</c>, sibling
    /// modules do not resolve. A reactor module without an explicit build task is therefore launched directly.
    /// </summary>
    public static bool LaunchesDirectly(WorkspaceSnapshot snapshot, JvmLaunchConfiguration configuration) =>
        snapshot.BuildSystem == JvmBuildSystem.Maven && configuration.BuildTask.Length == 0 &&
        snapshot.Module(configuration.Module) is { } module && !Passenger.Core.PathNames.Same(module.Root, snapshot.Root);

    public ProcessStartInfo Command(WorkspaceSnapshot snapshot, JvmLaunchConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(configuration);
        JvmModule module = snapshot.Module(configuration.Module)
            ?? throw new InvalidDataException($"The launch module '{configuration.Module}' is not in snapshot {snapshot.Generation}.");
        string task = configuration.BuildTask;
        if (task.Length == 0)
        {
            bool boot = module.SourceRoots
                .Where(root => root.Kind is JvmSourceKind.MainJava or JvmSourceKind.MainKotlin)
                .Where(root => Directory.Exists(root.Path))
                .SelectMany(root => Directory.EnumerateFiles(root.Path, "*.*", SearchOption.AllDirectories))
                .Any(path => (path.EndsWith(".java", StringComparison.OrdinalIgnoreCase) ||
                              path.EndsWith(".kt", StringComparison.OrdinalIgnoreCase)) &&
                             File.ReadAllText(path).Contains("SpringBootApplication", StringComparison.Ordinal));
            task = snapshot.BuildSystem == JvmBuildSystem.Maven
                ? boot ? "spring-boot:run" : "exec:java"
                : boot ? "bootRun" : "run";
        }
        List<string> arguments = [.. configuration.Arguments];
        if (snapshot.BuildSystem == JvmBuildSystem.Maven)
        {
            if (task == "exec:java")
            {
                arguments.Insert(0, "-Dexec.mainClass=" + configuration.MainClass);
                if (configuration.Arguments.Count > 0)
                {
                    arguments.RemoveAll(argument => configuration.Arguments.Contains(argument, StringComparer.Ordinal));
                    arguments.Add("-Dexec.args=" + string.Join(" ", configuration.Arguments.Select(QuoteArgument)));
                }
            }
            else if (task == "spring-boot:run")
            {
                arguments = configuration.Arguments.Count > 0
                    ? ["-Dspring-boot.run.arguments=" + string.Join(" ", configuration.Arguments.Select(QuoteArgument))]
                    : [];
                if (configuration.Profile.Length > 0)
                {
                    arguments.Add("-Dspring-boot.run.profiles=" + configuration.Profile);
                }
            }
            arguments.InsertRange(0, configuration.BuildArguments);
        }
        else
        {
            if (configuration.Profile.Length > 0)
            {
                arguments.Add("--spring.profiles.active=" + configuration.Profile);
            }
            if (arguments.Count > 0)
            {
                arguments = ["--args=" + string.Join(" ", arguments.Select(QuoteArgument))];
            }
            arguments.InsertRange(0, configuration.BuildArguments);
        }
        string selectedModule = Passenger.Core.PathNames.Same(module.Root, snapshot.Root) ? "." : module.Id;
        JvmBuildRequest request = new(snapshot.Root, selectedModule, task, arguments);
        ProcessStartInfo start = snapshot.BuildSystem == JvmBuildSystem.Maven
            ? new MavenJvmBuildAdapter().Command(request)
            : new GradleJvmBuildAdapter().Command(request);
        foreach (KeyValuePair<string, string> variable in configuration.Environment)
        {
            start.Environment[variable.Key] = variable.Value;
        }
        ProjectJdk.Apply(start, configuration.JavaHome.Length > 0 ? configuration.JavaHome : module.RuntimeToolchain.Home);
        return start;
    }

    private static string QuoteArgument(string value) =>
        value.IndexOfAny([' ', '"']) >= 0 ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;
}

public sealed class JvmLaunch
{
    private readonly IChildProcessRunner runner;
    private readonly IJvmLaunchAdapter adapter;

    public JvmLaunch(IChildProcessRunner? runner = null, IJvmLaunchAdapter? adapter = null)
    {
        this.runner = runner ?? new ChildProcessRunner();
        this.adapter = adapter ?? new WrapperJvmLaunchAdapter();
    }

    public ProcessStartInfo Command(WorkspaceSnapshot snapshot, JvmLaunchConfiguration configuration) =>
        adapter.Command(snapshot, configuration);

    public async Task<ProcessResult> RunAsync(
        WorkspaceSnapshot snapshot,
        JvmLaunchConfiguration configuration,
        Action<ProcessOutput>? output = null,
        CancellationToken cancellationToken = default)
    {
        WorkspaceTrust.Check(snapshot.Root);
        if (snapshot.BuildSystem == JvmBuildSystem.Idea)
        {
            JvmModule module = snapshot.Module(configuration.Module)
                ?? throw new InvalidDataException($"The launch module '{configuration.Module}' is not in snapshot {snapshot.Generation}.");
            ProcessResult compiled = await new JvmBuild(runner).RunAsync(
                snapshot,
                new JvmBuildRequest(snapshot.Root, module.Id, "classes", []),
                output,
                cancellationToken).ConfigureAwait(false);
            if (compiled.ExitCode != 0)
            {
                return compiled;
            }
            return await runner.RunAsync(new DirectJvmLaunchAdapter().Command(snapshot, configuration), output, cancellationToken).ConfigureAwait(false);
        }
        // A configuration that names its JDK is launched directly on it once the build compiled it: a build tool's own
        // run task would take the build's toolchain instead.
        if (configuration.Jdk.Length > 0 && snapshot.Module(configuration.Module) is { } named)
        {
            LaunchJdks.Home(named, configuration, JdkRegistry.Current);
        }
        ProcessStartInfo start = adapter is WrapperJvmLaunchAdapter && (configuration.Jdk.Length > 0 || WrapperJvmLaunchAdapter.LaunchesDirectly(snapshot, configuration))
            ? await DirectJvmLaunchAdapter.ResolvedCommandAsync(snapshot, configuration, cancellationToken).ConfigureAwait(false)
            : adapter.Command(snapshot, configuration);
        return await runner.RunAsync(start, output, cancellationToken).ConfigureAwait(false);
    }
}
