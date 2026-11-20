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

public sealed record JvmMainClass(string ClassName, string SourcePath);

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

    public IReadOnlyList<JvmMainClass> MainClasses { get; } = [.. Modules.SelectMany(module => module.MainClasses)];

    public IReadOnlyList<string> Tasks { get; } = [.. Modules.SelectMany(module => module.Tasks).Distinct(StringComparer.Ordinal)];

    public JvmModule? Module(string id) =>
        Modules.FirstOrDefault(module => string.Equals(module.Id, id, StringComparison.Ordinal)) ??
        (id == "." ? Modules.FirstOrDefault(module => Passenger.Core.PathNames.Same(module.Root, Root)) : null);
}
