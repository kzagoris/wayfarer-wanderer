using System.Globalization;

namespace WayfarerWanderer.Jvm;

/// <summary>JDT LS settings for a Maven or Gradle workspace: the JDKs the snapshot resolved. JDT LS imports these builds
/// itself (m2e, Buildship), but on its own it runs Gradle on its bundled JRE and so picks a different language level
/// than the build does; naming the import JDK and the module JDKs keeps the two the same.</summary>
public static class BuildLanguageSettings
{
    public static object Document(WorkspaceSnapshot snapshot) => new
    {
        java = new
        {
            import = new { gradle = new { java = new { home = GradleJavaHome(snapshot) } } },
            configuration = new { runtimes = Runtimes(snapshot) },
        },
    };

    public static object? Value(WorkspaceSnapshot snapshot, string? section) => section switch
    {
        "java.import.gradle.java.home" => GradleJavaHome(snapshot),
        "java.configuration.runtimes" => Runtimes(snapshot),
        _ => null,
    };

    /// <summary>The JDK the import ran Gradle on, so Buildship evaluates the build on the same one.</summary>
    public static string? GradleJavaHome(WorkspaceSnapshot snapshot) =>
        snapshot.BuildSystem == JvmBuildSystem.Gradle ? ImportToolchain(snapshot).Home : null;

    /// <summary>One runtime per Java version the modules compile or run with, the import JDK the default.</summary>
    public static object[] Runtimes(WorkspaceSnapshot snapshot)
    {
        JvmToolchain import = ImportToolchain(snapshot);
        Dictionary<int, string> homes = [];
        foreach (JvmToolchain toolchain in snapshot.Modules
            .SelectMany(module => new[] { module.CompileToolchain, module.RuntimeToolchain })
            .Prepend(import))
        {
            if (toolchain is { LanguageVersion: int level, Home: { Length: > 0 } home } && Directory.Exists(home)
                && Release(home) is int release && release == level)
            {
                homes.TryAdd(level, home);
            }
        }
        return [.. homes.Select(pair => new
        {
            name = "JavaSE-" + pair.Key.ToString(CultureInfo.InvariantCulture),
            path = pair.Value,
            @default = import.LanguageVersion == pair.Key,
        })];
    }

    private static JvmToolchain ImportToolchain(WorkspaceSnapshot snapshot) =>
        snapshot.Modules.FirstOrDefault()?.ImportToolchain ?? new JvmToolchain(null, null, null);

    /// <summary>The feature version a JDK's <c>release</c> file names; a module compiled at release 17 on JDK 24 has
    /// home 24 and level 17, and naming JDK 24 "JavaSE-17" would mislead JDT LS.</summary>
    private static int? Release(string home)
    {
        string file = Path.Combine(home, "release");
        if (!File.Exists(file))
        {
            return null;
        }
        foreach (string line in File.ReadLines(file))
        {
            if (line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal))
            {
                string version = line["JAVA_VERSION=".Length..].Trim('"');
                string feature = version.Split('.', '-', '+')[0];
                if (feature == "1" && version.Split('.') is [_, var legacy, ..])
                {
                    feature = legacy;
                }
                return int.TryParse(feature, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : null;
            }
        }
        return null;
    }
}
