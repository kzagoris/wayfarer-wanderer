using System.Globalization;

namespace WayfarerWanderer.Jvm;

/// <summary>JDT LS settings for a Maven or Gradle workspace: the JDKs the snapshot resolved and the JDK table. JDT LS
/// imports these builds itself (m2e, Buildship), but on its own it runs Gradle on its bundled JRE and so picks a
/// different language level than the build does; naming the import JDK and the module JDKs keeps the two the same, and
/// Buildship is told about the table's JDKs as the build is (<see cref="BuildJdks"/>), so the two imports agree.</summary>
public static class BuildLanguageSettings
{
    public static object Document(WorkspaceSnapshot snapshot, IReadOnlyList<Jdk>? jdks = null)
    {
        jdks ??= JdkRegistry.Current.All();
        return new
        {
            java = new
            {
                import = new { gradle = new { java = new { home = GradleJavaHome(snapshot) }, arguments = GradleArguments(snapshot, jdks) } },
                configuration = new { runtimes = Runtimes(snapshot, jdks) },
            },
        };
    }

    public static object? Value(WorkspaceSnapshot snapshot, string? section, IReadOnlyList<Jdk>? jdks = null) => section switch
    {
        "java.import.gradle.java.home" => GradleJavaHome(snapshot),
        "java.import.gradle.arguments" => GradleArguments(snapshot, jdks ?? JdkRegistry.Current.All()),
        "java.configuration.runtimes" => Runtimes(snapshot, jdks ?? JdkRegistry.Current.All()),
        _ => null,
    };

    /// <summary>Buildship's Gradle arguments: the table's JDKs as installation paths, quoted where a path has a space;
    /// null for a Maven workspace or an empty table.</summary>
    public static string? GradleArguments(WorkspaceSnapshot snapshot, IReadOnlyList<Jdk> jdks) =>
        snapshot.BuildSystem == JvmBuildSystem.Gradle && BuildJdks.GradleArgument(snapshot.Root, jdks) is { } argument
            ? argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument
            : null;

    /// <summary>The JDK the import ran Gradle on, so Buildship evaluates the build on the same one.</summary>
    public static string? GradleJavaHome(WorkspaceSnapshot snapshot) =>
        snapshot.BuildSystem == JvmBuildSystem.Gradle ? ImportToolchain(snapshot).Home : null;

    /// <summary>One runtime per Java version the modules compile or run with, the import JDK the default. A version a
    /// module asks for that has no home in the snapshot takes the table's JDK for it, so a JDK just chosen compiles in
    /// the editor before the reimport ends; a version no JDK meets gets no runtime.</summary>
    public static object[] Runtimes(WorkspaceSnapshot snapshot, IReadOnlyList<Jdk>? jdks = null)
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
        foreach (JvmToolchain toolchain in snapshot.Modules.SelectMany(module => new[] { module.CompileToolchain, module.RuntimeToolchain }))
        {
            int? wanted = toolchain.Requirement?.Feature ?? (toolchain.Missing ? toolchain.LanguageVersion : null);
            if (wanted is int feature && !homes.ContainsKey(feature)
                && JdkResolution.Choose(new JdkRequirement(null, feature, toolchain.Requirement?.Vendor), jdks ?? []) is { } chosen)
            {
                homes[feature] = chosen.Home;
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
