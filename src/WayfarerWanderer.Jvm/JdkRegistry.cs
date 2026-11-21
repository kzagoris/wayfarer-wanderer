using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

/// <summary>How a JDK came to be in the <see cref="JdkRegistry"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JdkOrigin>))]
public enum JdkOrigin
{
    /// <summary>Found where JDKs are usually installed, and not kept.</summary>
    Detected,

    /// <summary>Chosen from disk by the person, or kept once a project used it.</summary>
    Added,

    /// <summary>Downloaded by Wayfarer.</summary>
    Downloaded,

    /// <summary>Named in an IntelliJ IDEA JDK table (<c>jdk.table.xml</c>), under IntelliJ's name for it.</summary>
    IntelliJ,
}

/// <summary>A JDK under the name projects and settings refer to it by, read from its home's <c>release</c> file.</summary>
public sealed record Jdk(string Name, string Home, string Version, int Feature, string? Vendor, JdkOrigin Origin);

/// <summary>
/// Wayfarer's JDK table, kept per person as IntelliJ keeps <c>jdk.table.xml</c>: the JDKs the person added or Wayfarer
/// downloaded (saved in <see cref="File"/>), the JDKs IntelliJ's own tables name, and every JDK found where installers
/// and version managers put them. Each has a name unique in the table. This is the one place JDKs are looked for: the
/// tooling JVM is chosen from it, and the project host is sent it rather than searching the disk itself.
/// </summary>
public sealed class JdkRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Func<IEnumerable<string>> candidates;
    private readonly string? intelliJConfig;
    private readonly Lock gate = new();
    private List<Kept> kept;

    /// <param name="file">Where the kept JDKs are saved; null keeps them in memory only.</param>
    /// <param name="candidates">JDK homes to detect; the usual install folders when null.</param>
    /// <param name="intelliJConfig">The folder of JetBrains products' configuration folders; the person's when null.</param>
    public JdkRegistry(string? file, Func<IEnumerable<string>>? candidates = null, string? intelliJConfig = null)
    {
        File = file;
        this.candidates = candidates ?? JdkScanner.Candidates;
        this.intelliJConfig = intelliJConfig;
        kept = Load(file);
    }

    /// <summary>The table the app and the host use; the app points it at its state folder at start.</summary>
    public static JdkRegistry Current { get; set; } = new(null);

    public string? File { get; }

    /// <summary>Every JDK on this machine, kept ones first, then IntelliJ's, then detected ones; each home once, each
    /// name once. A kept JDK whose home no longer holds one is left out (and stays kept, should it come back).</summary>
    public IReadOnlyList<Jdk> All()
    {
        List<Kept> saved;
        lock (gate)
        {
            saved = [.. kept];
        }
        List<Jdk> all = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        void Offer(string home, string? name, JdkOrigin origin)
        {
            // A version manager's alias ("21" linking to "21.0.5") is the JDK it links to, listed once.
            string real = Resolved(home);
            if (ToolingJvms.At(real) is not { } jvm || all.Any(known => PathNames.Same(known.Home, jvm.Home)))
            {
                return;
            }
            string unique = name is { Length: > 0 } ? Unique(name, jvm, names) : Unique(SuggestName(jvm.Vendor, jvm.Feature), jvm, names);
            names.Add(unique);
            all.Add(new Jdk(unique, jvm.Home, jvm.Version!, jvm.Feature, jvm.Vendor, origin));
        }
        foreach (Kept jdk in saved)
        {
            Offer(jdk.Home, jdk.Name, jdk.Origin);
        }
        foreach ((string name, string home) in IntelliJTable(intelliJConfig ?? JdkScanner.IntelliJConfig()))
        {
            Offer(home, name, JdkOrigin.IntelliJ);
        }
        foreach (string home in candidates())
        {
            Offer(home, null, JdkOrigin.Detected);
        }
        return all;
    }

    /// <summary>The JDK of that name, whatever case it is written in; null for none.</summary>
    public Jdk? Named(string name) =>
        All().FirstOrDefault(jdk => string.Equals(jdk.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Keeps the JDK at <paramref name="home"/> under <paramref name="name"/> (one suggested from its vendor and
    /// version when null) and saves the table. A home already kept is returned as it is.</summary>
    /// <exception cref="ArgumentException">The folder holds no JDK, or the name is taken by another JDK.</exception>
    public Jdk Add(string home, string? name = null, JdkOrigin origin = JdkOrigin.Added)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ToolingJvm jvm = ToolingJvms.At(Resolved(home)) ?? throw new ArgumentException($"{home} is not a JDK: it has no bin/java and release file.", nameof(home));
        lock (gate)
        {
            if (kept.FirstOrDefault(jdk => PathNames.Same(jdk.Home, jvm.Home)) is { } already)
            {
                return All().First(jdk => PathNames.Same(jdk.Home, already.Home));
            }
        }
        List<Jdk> others = [.. All().Where(jdk => !PathNames.Same(jdk.Home, jvm.Home))];
        string chosen;
        if (name is { Length: > 0 })
        {
            if (others.Any(jdk => jdk.Origin != JdkOrigin.Detected && string.Equals(jdk.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"Another JDK is already named {name}.", nameof(name));
            }
            chosen = name;
        }
        else
        {
            HashSet<string> taken = new(others.Where(jdk => jdk.Origin != JdkOrigin.Detected).Select(jdk => jdk.Name), StringComparer.OrdinalIgnoreCase);
            chosen = Unique(SuggestName(jvm.Vendor, jvm.Feature), jvm, taken);
        }
        lock (gate)
        {
            kept = [.. kept, new Kept(chosen, jvm.Home, origin)];
            Save();
        }
        return All().First(jdk => PathNames.Same(jdk.Home, jvm.Home));
    }

    /// <summary>Forgets a kept JDK, leaving its files alone; false when no kept JDK has that name.</summary>
    public bool Remove(string name)
    {
        lock (gate)
        {
            int before = kept.Count;
            kept = [.. kept.Where(jdk => !string.Equals(jdk.Name, name, StringComparison.OrdinalIgnoreCase))];
            if (kept.Count == before)
            {
                return false;
            }
            Save();
            return true;
        }
    }

    /// <summary>The name IntelliJ would suggest for a JDK: its distribution and feature release, such as
    /// <c>temurin-21</c> or <c>corretto-17</c>.</summary>
    public static string SuggestName(string? vendor, int feature) => $"{Distribution(vendor)}-{feature}";

    /// <summary>The short name of a distribution, from the <c>IMPLEMENTOR</c> its <c>release</c> file names; a short
    /// name (as a project's requirement writes it) is itself.</summary>
    public static string Distribution(string? vendor)
    {
        string v = vendor?.Trim().ToLowerInvariant() ?? string.Empty;
        if (Distributions.Contains(v))
        {
            return v;
        }
        return v switch
        {
            _ when v.Contains("adoptium", StringComparison.Ordinal) || v.Contains("temurin", StringComparison.Ordinal) => "temurin",
            _ when v.Contains("adoptopenjdk", StringComparison.Ordinal) => "adopt",
            _ when v.Contains("amazon", StringComparison.Ordinal) => "corretto",
            _ when v.Contains("azul", StringComparison.Ordinal) => "zulu",
            _ when v.Contains("bellsoft", StringComparison.Ordinal) => "liberica",
            _ when v.Contains("microsoft", StringComparison.Ordinal) => "ms",
            _ when v.Contains("jetbrains", StringComparison.Ordinal) => "jbr",
            _ when v.Contains("graalvm", StringComparison.Ordinal) => "graalvm",
            _ when v.Contains("international business machines", StringComparison.Ordinal) || v.StartsWith("ibm", StringComparison.Ordinal) => "semeru",
            _ when v.Contains("sap", StringComparison.Ordinal) => "sapmachine",
            _ when v.Contains("alibaba", StringComparison.Ordinal) => "dragonwell",
            _ when v.Contains("tencent", StringComparison.Ordinal) => "kona",
            _ when v.Contains("oracle", StringComparison.Ordinal) => "openjdk",
            _ => "jdk",
        };
    }

    private static readonly HashSet<string> Distributions =
        ["temurin", "adopt", "corretto", "zulu", "liberica", "ms", "jbr", "graalvm", "semeru", "sapmachine", "dragonwell", "kona", "openjdk", "jdk"];

    /// <summary>The Java SDKs JetBrains products' <c>jdk.table.xml</c> files name, newest product first, each name
    /// once, with <c>$USER_HOME$</c> expanded. Non-Java SDKs (Python, Android, …) are left out.</summary>
    public static IReadOnlyList<(string Name, string Home)> IntelliJTable(string? configRoot)
    {
        List<(string Name, string Home)> found = [];
        if (configRoot is null || !Directory.Exists(configRoot))
        {
            return found;
        }
        IEnumerable<string> tables;
        try
        {
            tables = [.. Directory.GetDirectories(configRoot)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Select(product => Path.Combine(product, "options", "jdk.table.xml"))
                .Where(System.IO.File.Exists)];
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return found;
        }
        foreach (string table in tables)
        {
            foreach ((string name, string home) in ReadTable(table))
            {
                if (!found.Any(known => string.Equals(known.Name, name, StringComparison.Ordinal)))
                {
                    found.Add((name, home));
                }
            }
        }
        return found;
    }

    private static IEnumerable<(string Name, string Home)> ReadTable(string table)
    {
        XmlDocument document = new() { XmlResolver = null };
        try
        {
            using XmlReader reader = XmlReader.Create(table, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document.Load(reader);
        }
        catch (Exception unreadable) when (unreadable is XmlException or IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (XmlElement jdk in document.GetElementsByTagName("jdk").OfType<XmlElement>())
        {
            string? type = Value(jdk, "type");
            string? name = Value(jdk, "name");
            string? home = Value(jdk, "homePath");
            if ((type is null || type == "JavaSDK") && name is { Length: > 0 } && home is { Length: > 0 })
            {
                string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                yield return (name, Path.GetFullPath(home.Replace("$USER_HOME$", user, StringComparison.Ordinal)));
            }
        }
    }

    private static string? Value(XmlElement parent, string child) =>
        parent.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.Name == child)?.GetAttribute("value") is { Length: > 0 } value ? value : null;

    private static string Resolved(string home)
    {
        try
        {
            return new DirectoryInfo(home).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? home;
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return home;
        }
    }

    /// <summary><paramref name="wanted"/> where it is free, else with the full version, else numbered.</summary>
    private static string Unique(string wanted, ToolingJvm jvm, HashSet<string> taken)
    {
        if (!taken.Contains(wanted))
        {
            return wanted;
        }
        string versioned = $"{Distribution(jvm.Vendor)}-{jvm.Version}";
        if (!taken.Contains(versioned))
        {
            return versioned;
        }
        for (int n = 2; ; n++)
        {
            if (!taken.Contains($"{versioned} ({n})"))
            {
                return $"{versioned} ({n})";
            }
        }
    }

    private static List<Kept> Load(string? file)
    {
        if (file is null || !System.IO.File.Exists(file))
        {
            return [];
        }
        try
        {
            Table? table = JsonSerializer.Deserialize<Table>(System.IO.File.ReadAllText(file), JsonOptions);
            return [.. (table?.Jdks ?? []).Where(jdk => jdk is { Name.Length: > 0, Home.Length: > 0 })];
        }
        catch (Exception unreadable) when (unreadable is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save()
    {
        if (File is null)
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(File))!);
        AtomicFile.Write(File, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Table(1, kept), JsonOptions)));
    }

    private sealed record Kept(string Name, string Home, JdkOrigin Origin);

    private sealed record Table(int Schema, IReadOnlyList<Kept>? Jdks);
}

/// <summary>Where JDKs are looked for on disk: the configured tooling JVM, <c>JAVA_HOME</c>, the <c>java</c> on
/// <c>PATH</c>, then the folders installers and version managers use.</summary>
public static class JdkScanner
{
    /// <summary>Every candidate home, preferred ones first; not yet checked to hold a JDK.</summary>
    public static IEnumerable<string> Candidates() => Preferred().Concat(Installed());

    /// <summary>The homes the person points at: Wayfarer's tooling JVM setting, <c>JAVA_HOME</c>, then <c>PATH</c>.</summary>
    public static IEnumerable<string> Preferred()
    {
        if (ToolingJvms.Configured is { Length: > 0 } configured)
        {
            yield return configured;
        }
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome)
        {
            yield return javaHome;
        }
        if (OnPath() is { } onPath)
        {
            yield return onPath;
        }
    }

    /// <summary>JDK homes in the folders installers and version managers use.</summary>
    public static IEnumerable<string> Installed()
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<string> parents =
        [
            Path.Combine(user, ".jdks"),
            Path.Combine(user, ".sdkman", "candidates", "java"),
            Path.Combine(user, ".asdf", "installs", "java"),
            Path.Combine(user, ".local", "share", "mise", "installs", "java"),
            Path.Combine(user, ".gradle", "jdks"),
        ];
        if (OperatingSystem.IsWindows())
        {
            foreach (string programs in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(p => p.Length > 0))
            {
                parents.AddRange(new[] { "Java", "Eclipse Adoptium", "Amazon Corretto", "Microsoft", "Zulu", "BellSoft", "Semeru" }.Select(vendor => Path.Combine(programs, vendor)));
            }
            string scoop = Environment.GetEnvironmentVariable("SCOOP") is { Length: > 0 } root ? root : Path.Combine(user, "scoop");
            parents.AddRange(Children(Path.Combine(scoop, "apps")));
            parents.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mise", "installs", "java"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (string bundle in Children("/Library/Java/JavaVirtualMachines").Concat(Children(Path.Combine(user, "Library", "Java", "JavaVirtualMachines"))))
            {
                yield return Path.Combine(bundle, "Contents", "Home");
            }
        }
        else
        {
            parents.Add("/usr/lib/jvm");
            parents.Add("/usr/java");
            parents.Add("/opt/java");
        }
        foreach (string parent in parents)
        {
            foreach (string child in Children(parent))
            {
                yield return child;
            }
        }
    }

    /// <summary>The folder JetBrains products keep their configuration folders in.</summary>
    public static string IntelliJConfig()
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JetBrains");
        }
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(user, "Library", "Application Support", "JetBrains");
        }
        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(user, ".config");
        return Path.Combine(config, "JetBrains");
    }

    /// <summary>The home of the <c>java</c> first on <c>PATH</c>, through any links; null for none.</summary>
    public static string? OnPath()
    {
        string name = OperatingSystem.IsWindows() ? "java.exe" : "java";
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(folder.Trim('"'), name);
            if (!File.Exists(candidate))
            {
                continue;
            }
            string resolved = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
            return Path.GetDirectoryName(Path.GetDirectoryName(resolved));
        }
        return null;
    }

    internal static IEnumerable<string> Children(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder) : [];
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
