using System.Globalization;

namespace WayfarerWanderer.Jvm;

/// <summary>JDT LS settings for an IntelliJ project that has no Maven or Gradle build: every module's sources and
/// libraries in one project, as JDT LS's invisible project takes them.</summary>
public static class IdeaLanguageSettings
{
    public static object Document(WorkspaceSnapshot snapshot) => new
    {
        java = new
        {
            project = new
            {
                sourcePaths = SourcePaths(snapshot),
                outputPath = OutputPath(snapshot),
                referencedLibraries = Libraries(snapshot),
            },
            configuration = new { runtimes = Runtimes(snapshot) },
        },
    };

    public static object? Value(WorkspaceSnapshot snapshot, string? section) => section switch
    {
        "java.project.sourcePaths" => SourcePaths(snapshot),
        "java.project.outputPath" => OutputPath(snapshot),
        "java.project.referencedLibraries" => Libraries(snapshot),
        "java.configuration.runtimes" => Runtimes(snapshot),
        _ => null,
    };

    public static string[] SourcePaths(WorkspaceSnapshot snapshot) =>
        [.. snapshot.Modules.SelectMany(module => module.SourceRoots)
            .Select(root => Relative(snapshot, root.Path))
            .Distinct(StringComparer.Ordinal)];

    public static string OutputPath(WorkspaceSnapshot snapshot)
    {
        string? classes = snapshot.Modules.Select(module => module.Classpath.Runtime.FirstOrDefault()).FirstOrDefault(path => path is not null);
        return classes is null ? "bin" : Relative(snapshot, classes);
    }

    /// <summary>Every module's libraries; another module's classes are left out, since its sources are among the
    /// source paths.</summary>
    public static string[] Libraries(WorkspaceSnapshot snapshot) =>
        [.. snapshot.Modules.SelectMany(module => module.SourceSets.Count > 0
                ? module.SourceSets.SelectMany(set => set.CompileClasspath).Where(entry => entry.ModuleRef is null).Select(entry => entry.Path)
                : module.Classpath.Compile)
            .Distinct(StringComparer.Ordinal)];

    public static object[] Runtimes(WorkspaceSnapshot snapshot)
    {
        // The JDK the module compiles with (the one IntelliJ names), never the tooling JVM that runs the host.
        JvmToolchain toolchain = snapshot.Modules.FirstOrDefault()?.CompileToolchain ?? new JvmToolchain(null, null, null);
        if (string.IsNullOrWhiteSpace(toolchain.Home))
        {
            return [];
        }
        string name = toolchain.LanguageVersion is int level
            ? "JavaSE-" + level.ToString(CultureInfo.InvariantCulture)
            : "JavaSE";
        return [new { name, path = toolchain.Home, @default = true }];
    }

    private static string Relative(WorkspaceSnapshot snapshot, string path) =>
        Path.GetRelativePath(snapshot.Root, path).Replace('\\', '/');
}
