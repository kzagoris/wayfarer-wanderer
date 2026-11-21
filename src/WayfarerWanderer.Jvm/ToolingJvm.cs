using System.Text.RegularExpressions;

namespace WayfarerWanderer.Jvm;

/// <summary>The JVM that runs Wayfarer's own JVM tools (the project host, the debug adapter): never the project's JDK,
/// which the snapshot records separately.</summary>
public sealed record ToolingJvm(string Home, int Feature, string? Version, string? Vendor)
{
    public string Java => Path.Combine(Home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
}

/// <summary>No JVM new enough to run a tool was found, with the homes that were looked at.</summary>
public sealed class ToolingJvmMissingException(string message, IReadOnlyList<string> found) : Exception(message)
{
    public IReadOnlyList<string> Found { get; } = found;
}

/// <summary>
/// Finds the tooling JVM: the one configured in Wayfarer's settings, else <c>JAVA_HOME</c>, else the <c>java</c> on
/// <c>PATH</c>, each taken when it is new enough; else the newest new enough one in the <see cref="JdkRegistry"/>.
/// </summary>
public static partial class ToolingJvms
{
    public const int Minimum = 21;

    /// <summary>The JVM configured in Wayfarer's settings, which the app sets; null for none.</summary>
    public static string? Configured { get; set; }

    public static ToolingJvm Find(int minimum = Minimum)
    {
        List<string> seen = [];
        foreach (string home in JdkScanner.Preferred())
        {
            if (At(home) is { } jvm)
            {
                seen.Add($"{jvm.Home} (Java {jvm.Feature})");
                if (jvm.Feature >= minimum)
                {
                    return jvm;
                }
            }
        }
        ToolingJvm? newest = All()
            .Where(jvm => !seen.Any(said => said.StartsWith(jvm.Home + " ", StringComparison.Ordinal)))
            .Select(jvm => { seen.Add($"{jvm.Home} (Java {jvm.Feature})"); return jvm; })
            .Where(jvm => jvm.Feature >= minimum)
            .OrderByDescending(jvm => jvm.Feature)
            .FirstOrDefault();
        if (newest is not null)
        {
            return newest;
        }
        string named = seen.Count == 0 ? "no JVM was found" : "found " + string.Join(", ", seen);
        throw new ToolingJvmMissingException(
            $"The JVM project host needs Java {minimum} or later to run, and {named}. Choose a JDK {minimum} or later in Settings, or set JAVA_HOME to one.",
            seen);
    }

    /// <summary>Every JVM in the <see cref="JdkRegistry"/>, newest first.</summary>
    public static IReadOnlyList<ToolingJvm> All() =>
        [.. JdkRegistry.Current.All()
            .Select(jdk => new ToolingJvm(jdk.Home, jdk.Feature, jdk.Version, jdk.Vendor))
            .OrderByDescending(jvm => jvm.Feature)];

    /// <summary>The JVM at a home, read from its <c>release</c> file; null where there is no java to run.</summary>
    public static ToolingJvm? At(string home)
    {
        if (string.IsNullOrWhiteSpace(home))
        {
            return null;
        }
        string full = Path.GetFullPath(home);
        string java = Path.Combine(full, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (!File.Exists(java))
        {
            return null;
        }
        string? version = null;
        string? vendor = null;
        try
        {
            string release = File.ReadAllText(Path.Combine(full, "release"));
            version = ReleaseValue(release, "JAVA_VERSION");
            vendor = ReleaseValue(release, "IMPLEMENTOR");
        }
        catch (Exception missing) when (missing is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return version is null ? null : new ToolingJvm(full, Feature(version), version, vendor);
    }

    /// <summary>The feature release a version names: 8 for <c>1.8.0_402</c>, 22 for <c>22.0.1</c>.</summary>
    public static int Feature(string version)
    {
        string rest = version.StartsWith("1.", StringComparison.Ordinal) ? version[2..] : version;
        Match digits = LeadingDigits().Match(rest);
        return digits.Success ? int.Parse(digits.Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    private static string? ReleaseValue(string release, string key)
    {
        foreach (string line in release.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return trimmed[(key.Length + 1)..].Trim('"');
            }
        }
        return null;
    }

    [GeneratedRegex(@"^\d+")]
    private static partial Regex LeadingDigits();
}
