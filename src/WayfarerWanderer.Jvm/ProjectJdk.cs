using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

/// <summary>
/// A project's JDK, kept as IntelliJ keeps a project SDK: the project's own <c>.wayfarer/project.json</c> asks for one by
/// requirement (schema 2: <c>{"jdk": {"name", "feature", "vendor"}}</c>), so a clone or another machine resolves it from
/// its own <see cref="JdkRegistry"/>, and import, Build, Test and Run take the JDK it resolves to. A per-machine choice
/// (<see cref="ProjectJdkChoices"/>) wins over the shared requirement and never goes into the file. A schema 1 file,
/// which held an absolute path, still reads: a home that exists is registered and counts as a requirement for it. A
/// project that asks for nothing, or whose requirement no JDK meets, takes <c>JAVA_HOME</c> instead.
/// </summary>
public static class ProjectJdk
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FileOf(string projectRoot) => Path.Combine(projectRoot, ".wayfarer", "project.json");

    /// <summary>What the project asks for, with <see cref="JdkRequirement.Project"/> as its source; null for nothing.
    /// A schema 1 path that still holds a JDK is registered in <paramref name="registry"/> (the current table when
    /// null), and the file is left as it is.</summary>
    public static JdkRequirement? Requirement(string projectRoot, JdkRegistry? registry = null)
    {
        if (Read(projectRoot) is not { } document || document["jdk"] is not { } jdk)
        {
            return null;
        }
        try
        {
            if (jdk.GetValueKind() == JsonValueKind.String)
            {
                return jdk.GetValue<string>() is { Length: > 0 } home && Directory.Exists(home) && Registered(home, registry ?? JdkRegistry.Current) is { } found
                    ? new JdkRequirement(found.Name, found.Feature, JdkRegistry.Distribution(found.Vendor), JdkRequirement.Project)
                    : null;
            }
            if (jdk is not JsonObject requirement)
            {
                return null;
            }
            string? name = Text(requirement["name"]);
            int? feature = requirement["feature"] is { } f && f.GetValueKind() == JsonValueKind.Number && f.GetValue<double>() is >= 1 and < 1000 and var n ? (int)n : null;
            string? vendor = Text(requirement["vendor"]);
            return name is null && feature is null && vendor is null ? null : new JdkRequirement(name, feature, vendor, JdkRequirement.Project);
        }
        catch (Exception unreadable) when (unreadable is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The JDK the project runs on: the one chosen for it on this machine, else the one its requirement
    /// resolves to (<see cref="JdkResolution.Choose"/>); null when it asks for none or none meets it.</summary>
    public static Jdk? Resolve(string projectRoot, JdkRegistry? registry = null, ProjectJdkChoices? choices = null)
    {
        JdkRegistry table = registry ?? JdkRegistry.Current;
        if ((choices ?? ProjectJdkChoices.Current).Chosen(projectRoot) is { } chosen && table.Named(chosen) is { } picked)
        {
            return picked;
        }
        return Requirement(projectRoot, table) is { } requirement ? JdkResolution.Choose(requirement, table.All()) : null;
    }

    /// <summary>The home of the JDK the project resolves to; null otherwise.</summary>
    public static string? Recorded(string projectRoot) => Resolve(projectRoot)?.Home;

    /// <summary>The Maven profiles the project asks for beyond Maven's own activation (<c>mavenProfiles</c>; a leading
    /// <c>-</c> turns one off), none when it names none.</summary>
    public static IReadOnlyList<string> MavenProfiles(string projectRoot) =>
        Read(projectRoot)?["mavenProfiles"] is JsonArray profiles ? [.. profiles.Select(Text).OfType<string>()] : [];

    /// <summary>The JDK Maven, Gradle and the program run on: the resolved one, else <c>JAVA_HOME</c>.</summary>
    public static string? HomeOf(string projectRoot) =>
        Recorded(projectRoot) ??
        (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } environment ? environment : null);

    /// <summary>Writes <paramref name="requirement"/> as the project's JDK in schema 2, keeping every other key the file
    /// holds (<c>mavenProfiles</c> and whatever else shares it). A file that does not read is replaced.</summary>
    public static void Write(string projectRoot, JdkRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        JsonObject document = Read(projectRoot) ?? [];
        JsonObject written = new() { ["schema"] = 2, ["jdk"] = Node(requirement) };
        foreach ((string key, JsonNode? value) in document.ToList())
        {
            if (key is not ("schema" or "jdk"))
            {
                document.Remove(key);
                written[key] = value;
            }
        }
        string file = FileOf(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        AtomicFile.Write(file, Encoding.UTF8.GetBytes(written.ToJsonString(JsonOptions)));
    }

    /// <summary>The text of a new project file asking for <paramref name="requirement"/>.</summary>
    public static string Serialize(JdkRequirement requirement) =>
        new JsonObject { ["schema"] = 2, ["jdk"] = Node(requirement) }.ToJsonString(JsonOptions);

    /// <summary>Has a wrapper started by <paramref name="start"/> run on <paramref name="home"/>; none leaves the environment's.</summary>
    public static void Apply(ProcessStartInfo start, string? home)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (!string.IsNullOrWhiteSpace(home))
        {
            start.Environment["JAVA_HOME"] = home;
        }
    }

    private static JsonObject Node(JdkRequirement requirement) => new()
    {
        ["name"] = requirement.Name,
        ["feature"] = requirement.Feature,
        ["vendor"] = requirement.Vendor,
    };

    /// <summary>The JDK at <paramref name="home"/> as the table names it, kept there if it was only detected.</summary>
    private static Jdk? Registered(string home, JdkRegistry registry)
    {
        string full = Path.GetFullPath(home);
        if (registry.All().FirstOrDefault(jdk => jdk.Origin != JdkOrigin.Detected && PathNames.Same(jdk.Home, full)) is { } known)
        {
            return known;
        }
        try
        {
            return registry.Add(full);
        }
        catch (Exception unusable) when (unusable is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is not null && node.GetValueKind() == JsonValueKind.String && node.GetValue<string>() is { Length: > 0 } text ? text : null;

    private static JsonObject? Read(string projectRoot)
    {
        string file = FileOf(projectRoot);
        if (!File.Exists(file))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
        }
        catch (Exception failure) when (failure is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// The JDK the person chose for a project on this machine, by its name in the <see cref="JdkRegistry"/>, kept per folder
/// in Wayfarer's state as trust is (<c>project-jdks.json</c>) and never in the shared <c>.wayfarer/project.json</c>.
/// It wins over the project's requirement when that resolves to the wrong JDK here, or to none.
/// </summary>
public sealed class ProjectJdkChoices
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly Lock gate = new();
    private List<Choice> choices;

    /// <param name="file">Where the choices are saved; null keeps them in memory only.</param>
    public ProjectJdkChoices(string? file)
    {
        File = file;
        choices = Load(file);
    }

    /// <summary>The choices the app and the importer use; the app points them at its state folder at start.</summary>
    public static ProjectJdkChoices Current { get; set; } = new(null);

    public string? File { get; }

    /// <summary>The name of the JDK chosen for the project at <paramref name="projectRoot"/>; null for none.</summary>
    public string? Chosen(string projectRoot)
    {
        string folder = Path.GetFullPath(projectRoot);
        lock (gate)
        {
            return choices.FirstOrDefault(choice => PathNames.Same(choice.Folder, folder))?.Jdk;
        }
    }

    /// <summary>Chooses the JDK named <paramref name="jdk"/> for the project, or forgets the choice for null, and saves.</summary>
    public void Choose(string projectRoot, string? jdk)
    {
        string folder = Path.GetFullPath(projectRoot);
        lock (gate)
        {
            List<Choice> others = [.. choices.Where(choice => !PathNames.Same(choice.Folder, folder))];
            choices = jdk is { Length: > 0 } ? [.. others, new Choice(folder, jdk)] : others;
            if (File is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(File))!);
                AtomicFile.Write(File, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Table(1, choices), JsonOptions)));
            }
        }
    }

    private static List<Choice> Load(string? file)
    {
        if (file is null || !System.IO.File.Exists(file))
        {
            return [];
        }
        try
        {
            Table? table = JsonSerializer.Deserialize<Table>(System.IO.File.ReadAllText(file), JsonOptions);
            return [.. (table?.Choices ?? []).Where(choice => choice is { Folder.Length: > 0, Jdk.Length: > 0 } && Path.IsPathFullyQualified(choice.Folder))];
        }
        catch (Exception unreadable) when (unreadable is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed record Choice(string Folder, string Jdk);

    private sealed record Table(int Schema, IReadOnlyList<Choice>? Choices);
}
