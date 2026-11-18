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

        string javaHome = FirstNotEmpty(configuration.JavaHome, module.RuntimeToolchain.Home, module.CompileToolchain.Home, module.ImportToolchain.Home);
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

    private static string FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}

public sealed class WrapperJvmLaunchAdapter : IJvmLaunchAdapter
{
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
        if (configuration.JavaHome.Length > 0)
        {
            start.Environment["JAVA_HOME"] = configuration.JavaHome;
        }
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
        return await runner.RunAsync(adapter.Command(snapshot, configuration), output, cancellationToken).ConfigureAwait(false);
    }
}
