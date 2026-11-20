namespace WayfarerWanderer.Jvm;

public enum JvmBuildSystem
{
    Maven,
    Gradle,
    Idea,
}

public enum JvmSourceKind
{
    MainJava,
    MainKotlin,
    TestJava,
    TestKotlin,
    MainResources,
    TestResources,
    Generated,
}

public sealed record JvmSourceRoot(string Path, JvmSourceKind Kind);

public sealed record JvmClasspath
{
    public JvmClasspath(
        IReadOnlyList<string> compile,
        IReadOnlyList<string> test,
        IReadOnlyList<string> runtime)
    {
        Compile = compile ?? throw new ArgumentNullException(nameof(compile));
        Test = test ?? throw new ArgumentNullException(nameof(test));
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public JvmClasspath(string compile, string test, string runtime)
        : this(Split(compile), Split(test), Split(runtime))
    {
    }

    public IReadOnlyList<string> Compile { get; }

    public IReadOnlyList<string> Test { get; }

    public IReadOnlyList<string> Runtime { get; }

    private static IReadOnlyList<string> Split(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }
        return value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

public sealed record JvmToolchain(string? Home, int? LanguageVersion, string? Vendor);

public sealed record JvmMainClass(string ClassName, string SourcePath)
{
    /// <summary>True where the build names it (<c>application.mainClass</c>, a Boot or exec plugin's configuration),
    /// false where a scan of the sources found it.</summary>
    public bool Configured { get; init; }
}

/// <summary>One classpath entry, in order: its file or folder, and either the library's coordinates
/// (<c>group:artifact:version[:classifier]</c>) or the source set whose outputs it is (<see cref="JvmSourceSet.Id"/>).</summary>
public sealed record JvmClasspathEntry(string Path, string? Coordinates, string? ModuleRef);

/// <summary>A source set of a module (Gradle's <c>main</c>, <c>test</c>, a plugin's <c>testFixtures</c>; Maven's main and
/// test), with its own roots, outputs and ordered classpaths. Ids are <c>module:set</c>.</summary>
public sealed record JvmSourceSet(
    string Id,
    string Name,
    bool IsTest,
    IReadOnlyList<JvmSourceRoot> Roots,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<JvmClasspathEntry> CompileClasspath,
    IReadOnlyList<JvmClasspathEntry> RuntimeClasspath);

/// <summary>A file a build evaluation read, stamped as it was read: last write in milliseconds since the epoch and
/// length, -1 for one that was absent.</summary>
public sealed record JvmBuildInput(string Path, long Modified, long Length);

public sealed record JvmModule(
    string Id,
    string Name,
    string Root,
    IReadOnlyList<JvmSourceRoot> SourceRoots,
    JvmClasspath Classpath,
    JvmToolchain ImportToolchain,
    JvmToolchain CompileToolchain,
    JvmToolchain RuntimeToolchain,
    IReadOnlyList<string> Tasks,
    IReadOnlyList<JvmMainClass> MainClasses)
{
    /// <summary>Library jars named by an IntelliJ module whose files are not on disk.</summary>
    public IReadOnlyList<string> AbsentLibraries { get; init; } = [];

    /// <summary>The module's source sets as the build evaluates them; <see cref="SourceRoots"/> and
    /// <see cref="Classpath"/> are made of its main and test sets.</summary>
    public IReadOnlyList<JvmSourceSet> SourceSets { get; init; } = [];

    /// <summary>Why part of the module could not be evaluated (a dependency that does not resolve, a missing toolchain)
    /// while the rest of it stands; null when it evaluated whole.</summary>
    public string? Problem { get; init; }

    /// <summary>The Maven profiles active for the module.</summary>
    public IReadOnlyList<string> ActiveProfiles { get; init; } = [];

    public JvmSourceSet? SourceSet(string name) =>
        SourceSets.FirstOrDefault(set => string.Equals(set.Name, name, StringComparison.Ordinal));
}

public sealed record WorkspaceSnapshot(
    long Generation,
    string Root,
    JvmBuildSystem BuildSystem,
    string BuildFile,
    JvmToolchain ToolingToolchain,
    IReadOnlyList<JvmModule> Modules)
{
    /// <summary>The folder an IntelliJ project compiles into (<c>out</c>), when this snapshot is one.</summary>
    public string? OutputRoot { get; init; }

    /// <summary>The files the evaluation read: a change to one means the snapshot may no longer stand.</summary>
    public IReadOnlyList<JvmBuildInput> BuildInputs { get; init; } = [];

    public IReadOnlyList<JvmMainClass> MainClasses { get; } = [.. Modules.SelectMany(module => module.MainClasses)];

    public IReadOnlyList<string> Tasks { get; } = [.. Modules.SelectMany(module => module.Tasks).Distinct(StringComparer.Ordinal)];

    public JvmModule? Module(string id) =>
        Modules.FirstOrDefault(module => string.Equals(module.Id, id, StringComparison.Ordinal)) ??
        (id == "." ? Modules.FirstOrDefault(module => Passenger.Core.PathNames.Same(module.Root, Root)) : null);
}
