using System.Text.Json;
using System.Text.Json.Serialization;

namespace WayfarerWanderer.Jvm;

/// <summary>
/// The JVM project host's protocol as the app reads it: one UTF-8 JSON object per line, as Passenger's project host
/// speaks it. The host's Java records (<c>jvm/project-host/host/.../Protocol.java</c>) are the other end; the golden
/// examples under <c>jvm/project-host/protocol</c> are read by the tests of both. Names follow the Build Server
/// Protocol; <see cref="HostModel"/> maps them onto <see cref="WorkspaceSnapshot"/>.
/// </summary>
public static class HostProtocol
{
    public const int Version = 1;

    public const string Import = "import";
    public const string Cancel = "cancel";
    public const string Ping = "ping";

    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

public sealed record HostRequest(int Id, string Method, object? Params);

public sealed record HostJvm(string Home, string? Version, int? Feature, string? Vendor);

public sealed record HostReady(int Protocol, HostJvm Java, long Pid);

/// <summary>A JDK of the app's <see cref="JdkRegistry"/>, sent with each import so the host chooses among them rather
/// than searching the disk itself.</summary>
public sealed record HostJdk(string Name, string Home, string? Version, int? Feature, string? Vendor);

public sealed record HostImportParams(
    string Folder,
    long Generation,
    string? Jdk,
    IReadOnlyList<string> MavenProfiles,
    string? BuildTool,
    IReadOnlyList<HostJdk> Jdks);

public sealed record HostCancelParams(int Request);

public sealed record HostTargetEvaluated(HostBuildTarget Target);

public sealed record HostWorkspaceResult(
    long Generation,
    string Root,
    string BuildSystem,
    string BuildFile,
    HostJvm? ImportJvm,
    IReadOnlyList<HostBuildTarget> Targets,
    IReadOnlyList<HostBuildInput>? BuildInputs,
    string? OutputRoot);

public sealed record HostBuildInput(string Path, long Modified, long Length);

public sealed record HostBuildTarget(
    string Id,
    string DisplayName,
    string BaseDirectory,
    IReadOnlyList<string>? LanguageIds,
    HostJvmEnvironment? JvmBuildTarget,
    HostJvmEnvironment? RunEnvironment,
    IReadOnlyList<HostSourceSet>? SourceSets,
    IReadOnlyList<string>? Tasks,
    IReadOnlyList<HostMainClass>? MainClasses,
    IReadOnlyList<string>? ActiveProfiles,
    IReadOnlyList<string>? AbsentLibraries,
    string? Problem);

public sealed record HostJvmEnvironment(string? JavaHome, int? JavaVersion, string? Vendor);

public sealed record HostSourceSet(
    string Id,
    string Name,
    bool Test,
    IReadOnlyList<HostSourceItem>? Sources,
    IReadOnlyList<string>? Outputs,
    IReadOnlyDictionary<string, IReadOnlyList<HostDependencyModule>>? DependencyModules);

public sealed record HostSourceItem(string Uri, string Kind, bool Generated);

public sealed record HostDependencyModule(string Path, string? Coordinates, string? ModuleRef);

public sealed record HostMainClass(string ClassName, string? SourcePath, string? Origin);

/// <summary>The host's model mapped onto the app's snapshot records.</summary>
public static class HostModel
{
    public static WorkspaceSnapshot ToSnapshot(HostWorkspaceResult result, HostJvm toolingJvm)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(toolingJvm);
        JvmBuildSystem buildSystem = result.BuildSystem switch
        {
            "maven" => JvmBuildSystem.Maven,
            "gradle" => JvmBuildSystem.Gradle,
            "idea" => JvmBuildSystem.Idea,
            _ => throw new InvalidDataException($"the project host named an unknown build system '{result.BuildSystem}'"),
        };
        JvmToolchain import = result.ImportJvm is { } jvm ? new JvmToolchain(jvm.Home, jvm.Feature, jvm.Vendor) : new JvmToolchain(null, null, null);
        return new WorkspaceSnapshot(
            result.Generation,
            result.Root,
            buildSystem,
            result.BuildFile,
            new JvmToolchain(toolingJvm.Home, toolingJvm.Feature, toolingJvm.Vendor),
            [.. result.Targets.Select(target => ToModule(target, import))])
        {
            OutputRoot = result.OutputRoot,
            BuildInputs = [.. (result.BuildInputs ?? []).Select(input => new JvmBuildInput(input.Path, input.Modified, input.Length))],
        };
    }

    public static JvmModule ToModule(HostBuildTarget target, JvmToolchain import)
    {
        ArgumentNullException.ThrowIfNull(target);
        List<JvmSourceSet> sets = [.. (target.SourceSets ?? []).Select(ToSourceSet)];
        JvmSourceSet? main = sets.FirstOrDefault(set => set.Name == "main");
        JvmSourceSet? test = sets.FirstOrDefault(set => set.Name == "test") ?? sets.FirstOrDefault(set => set.IsTest);
        List<JvmSourceRoot> roots = [.. sets.SelectMany(set => set.Roots)];
        JvmClasspath classpath = new(
            Paths(main?.CompileClasspath),
            Paths(test?.RuntimeClasspath),
            Paths(main?.RuntimeClasspath));
        return new JvmModule(
            target.Id,
            target.DisplayName,
            target.BaseDirectory,
            roots,
            classpath,
            import,
            Toolchain(target.JvmBuildTarget),
            Toolchain(target.RunEnvironment ?? target.JvmBuildTarget),
            target.Tasks ?? [],
            [.. (target.MainClasses ?? []).Select(main => new JvmMainClass(main.ClassName, main.SourcePath ?? string.Empty) { Configured = main.Origin == "configured" })])
        {
            SourceSets = sets,
            Problem = target.Problem,
            ActiveProfiles = target.ActiveProfiles ?? [],
            AbsentLibraries = target.AbsentLibraries ?? [],
        };
    }

    private static JvmSourceSet ToSourceSet(HostSourceSet set)
    {
        bool test = set.Test;
        IReadOnlyDictionary<string, IReadOnlyList<HostDependencyModule>> modules =
            set.DependencyModules ?? new Dictionary<string, IReadOnlyList<HostDependencyModule>>();
        return new JvmSourceSet(
            set.Id,
            set.Name,
            test,
            [.. (set.Sources ?? []).Select(source => new JvmSourceRoot(source.Uri, Kind(source, test)))],
            set.Outputs ?? [],
            Entries(modules, "compile"),
            Entries(modules, "runtime"));
    }

    private static IReadOnlyList<JvmClasspathEntry> Entries(IReadOnlyDictionary<string, IReadOnlyList<HostDependencyModule>> modules, string scope) =>
        modules.TryGetValue(scope, out IReadOnlyList<HostDependencyModule>? entries)
            ? [.. entries.Select(entry => new JvmClasspathEntry(entry.Path, entry.Coordinates, entry.ModuleRef))]
            : [];

    private static JvmSourceKind Kind(HostSourceItem source, bool test) =>
        source.Generated ? JvmSourceKind.Generated
        : (source.Kind, test) switch
        {
            ("kotlin", false) => JvmSourceKind.MainKotlin,
            ("kotlin", true) => JvmSourceKind.TestKotlin,
            ("resources", false) => JvmSourceKind.MainResources,
            ("resources", true) => JvmSourceKind.TestResources,
            (_, true) => JvmSourceKind.TestJava,
            _ => JvmSourceKind.MainJava,
        };

    private static JvmToolchain Toolchain(HostJvmEnvironment? environment) =>
        environment is null ? new JvmToolchain(null, null, null) : new JvmToolchain(environment.JavaHome, environment.JavaVersion, environment.Vendor);

    private static IReadOnlyList<string> Paths(IReadOnlyList<JvmClasspathEntry>? entries) =>
        entries is null ? [] : [.. entries.Select(entry => entry.Path)];
}
