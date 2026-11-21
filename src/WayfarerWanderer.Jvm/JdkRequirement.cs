namespace WayfarerWanderer.Jvm;

/// <summary>
/// The JDK a project or a module asks for, as IntelliJ asks for a project SDK by name: a name in the JDK table, a feature
/// release, a distribution (<c>temurin</c>, <c>corretto</c>, …), any of which may be absent, and where the request came
/// from (<see cref="Source"/>: the build's own declaration or the project's <c>.wayfarer/project.json</c>). Each machine
/// resolves it from its own <see cref="JdkRegistry"/> by <see cref="JdkResolution.Choose"/>.
/// </summary>
public sealed record JdkRequirement(string? Name, int? Feature, string? Vendor, string? Source = null)
{
    /// <summary>A Gradle <c>java.toolchain.languageVersion</c>.</summary>
    public const string GradleToolchain = "gradle-toolchain";

    /// <summary>A Maven <c>maven-toolchains-plugin</c> JDK requirement.</summary>
    public const string MavenRelease = "maven-release";

    /// <summary>An IntelliJ module's or project's JDK name, with its language level as the feature.</summary>
    public const string Idea = "idea";

    /// <summary>The project's own <c>.wayfarer/project.json</c>.</summary>
    public const string Project = "project";

    /// <summary>Gradle's daemon JVM criteria (<c>gradle/gradle-daemon-jvm.properties</c>).</summary>
    public const string GradleDaemon = "gradle-daemon";

    /// <summary>How the window names it: <c>JDK 17 (any vendor)</c>, <c>JDK 21 (temurin)</c>, or the JDK's name.</summary>
    public string Describe() =>
        Feature is int feature
            ? $"JDK {feature} ({(Vendor is { Length: > 0 } vendor ? JdkRegistry.Distribution(vendor) : "any vendor")})"
            : Name is { Length: > 0 } name ? $"the JDK named {name}" : "a JDK";
}

/// <summary>
/// How a <see cref="JdkRequirement"/> picks a JDK from the table. The host follows the same order
/// (<c>ImportJvms.choose</c>), and both test suites read the cases in <c>jvm/project-host/protocol/jdk-resolution.json</c>.
/// </summary>
public static class JdkResolution
{
    /// <summary>The JDK of the exact name (any case), else of the same feature and distribution, else of the same
    /// feature from any vendor; the newest update first and table order on a tie. Null when none matches.</summary>
    public static Jdk? Choose(JdkRequirement requirement, IEnumerable<Jdk> jdks)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(jdks);
        List<Jdk> table = [.. jdks];
        if (requirement.Name is { Length: > 0 } name &&
            table.FirstOrDefault(jdk => string.Equals(jdk.Name, name, StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            return named;
        }
        if (requirement.Feature is not int feature)
        {
            return null;
        }
        List<Jdk> sameFeature = [.. table.Where(jdk => jdk.Feature == feature)];
        if (requirement.Vendor is { Length: > 0 } vendor)
        {
            string distribution = JdkRegistry.Distribution(vendor);
            if (Newest(sameFeature.Where(jdk => JdkRegistry.Distribution(jdk.Vendor) == distribution)) is { } ofVendor)
            {
                return ofVendor;
            }
        }
        return Newest(sameFeature);
    }

    /// <summary>Compares version strings by their numbers in order (<c>21.0.13</c> after <c>21.0.5</c>, <c>11</c> before
    /// <c>11.0.25</c>).</summary>
    public static int CompareVersions(string? left, string? right)
    {
        List<long> a = Numbers(left);
        List<long> b = Numbers(right);
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            long x = i < a.Count ? a[i] : -1;
            long y = i < b.Count ? b[i] : -1;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }
        return 0;
    }

    private static Jdk? Newest(IEnumerable<Jdk> jdks)
    {
        Jdk? best = null;
        foreach (Jdk jdk in jdks)
        {
            if (best is null || CompareVersions(jdk.Version, best.Version) > 0)
            {
                best = jdk;
            }
        }
        return best;
    }

    private static List<long> Numbers(string? version)
    {
        List<long> numbers = [];
        long? current = null;
        foreach (char c in version ?? string.Empty)
        {
            if (char.IsAsciiDigit(c))
            {
                current = Math.Min(((current ?? 0) * 10) + (c - '0'), long.MaxValue / 10);
            }
            else if (current is long done)
            {
                numbers.Add(done);
                current = null;
            }
        }
        if (current is long last)
        {
            numbers.Add(last);
        }
        return numbers;
    }
}
