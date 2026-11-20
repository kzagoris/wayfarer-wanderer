using System.Diagnostics;
using System.Text.Json;

namespace WayfarerWanderer.Jvm;

/// <summary>
/// A project's JDK, kept as IntelliJ keeps a project SDK: New Project records the JDK chosen in the project's own
/// <c>.wayfarer/project.json</c>, and import, Build, Test and Run take it, so Maven and Gradle run on it whatever
/// JDK the environment names. A project with none recorded (cloned, or opened from elsewhere), or one whose JDK
/// folder is gone, takes the <c>JAVA_HOME</c> environment variable instead.
/// </summary>
public static class ProjectJdk
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string FileOf(string projectRoot) => Path.Combine(projectRoot, ".wayfarer", "project.json");

    /// <summary>The JDK the project records, while that folder still exists; null otherwise.</summary>
    public static string? Recorded(string projectRoot)
    {
        string file = FileOf(projectRoot);
        if (!File.Exists(file))
        {
            return null;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("jdk", out JsonElement jdk) &&
                   jdk.ValueKind == JsonValueKind.String &&
                   jdk.GetString() is { Length: > 0 } home &&
                   Directory.Exists(home)
                ? home
                : null;
        }
        catch (Exception failure) when (failure is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The Maven profiles the project asks for beyond Maven's own activation (<c>mavenProfiles</c>; a leading
    /// <c>-</c> turns one off), none when it names none.</summary>
    public static IReadOnlyList<string> MavenProfiles(string projectRoot)
    {
        string file = FileOf(projectRoot);
        if (!File.Exists(file))
        {
            return [];
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("mavenProfiles", out JsonElement profiles) &&
                   profiles.ValueKind == JsonValueKind.Array
                ? [.. profiles.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).Where(p => p.Length > 0)]
                : [];
        }
        catch (Exception failure) when (failure is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The JDK Maven, Gradle and the program run on: the recorded one, else <c>JAVA_HOME</c>.</summary>
    public static string? HomeOf(string projectRoot) =>
        Recorded(projectRoot) ??
        (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } environment ? environment : null);

    /// <summary>The text of a project file recording <paramref name="jdk"/>.</summary>
    public static string Serialize(string jdk) =>
        JsonSerializer.Serialize(new { schema = 1, jdk }, JsonOptions);

    /// <summary>Has a wrapper started by <paramref name="start"/> run on <paramref name="home"/>; none leaves the environment's.</summary>
    public static void Apply(ProcessStartInfo start, string? home)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (!string.IsNullOrWhiteSpace(home))
        {
            start.Environment["JAVA_HOME"] = home;
        }
    }
}
