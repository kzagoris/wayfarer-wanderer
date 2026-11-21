using System.Text.Json;
using System.Text.Json.Nodes;
using WayfarerWanderer.Jvm;
using Xunit;

namespace WayfarerWanderer.Tests;

/// <summary>A project asks for its JDK by requirement in <c>.wayfarer/project.json</c> (schema 2), resolved against the
/// JDK table by the order the golden <c>jdk-resolution.json</c> sets, which the host's tests read too.</summary>
public sealed class ProjectJdkTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("wayfarer-project-jdk-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    public static TheoryData<string> GoldenCases()
    {
        TheoryData<string> cases = [];
        foreach (JsonNode? item in Golden()["cases"]!.AsArray())
        {
            cases.Add(item!["why"]!.GetValue<string>());
        }
        return cases;
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void RequirementsResolveInTheSharedOrder(string why)
    {
        JsonObject golden = Golden();
        List<Jdk> table = [.. golden["jdks"]!.AsArray().Select(jdk => new Jdk(
            jdk!["name"]!.GetValue<string>(), jdk["home"]!.GetValue<string>(), jdk["version"]!.GetValue<string>(),
            jdk["feature"]!.GetValue<int>(), jdk["vendor"]?.GetValue<string>(), JdkOrigin.Added))];
        JsonNode item = golden["cases"]!.AsArray().First(c => c!["why"]!.GetValue<string>() == why)!;
        JsonNode requirement = item["requirement"]!;
        JdkRequirement wanted = new(requirement["name"]?.GetValue<string>(), requirement["feature"]?.GetValue<int>(), requirement["vendor"]?.GetValue<string>());
        Assert.Equal(item["chosen"]?.GetValue<string>(), JdkResolution.Choose(wanted, table)?.Name);
    }

    [Fact]
    public void DistributionsReadAsTheGoldenFileSays()
    {
        foreach (JsonNode? item in Golden()["distributions"]!.AsArray())
        {
            Assert.Equal(item!["distribution"]!.GetValue<string>(), JdkRegistry.Distribution(item["vendor"]?.GetValue<string>()));
        }
    }

    [Fact]
    public void ASchemaTwoRequirementRoundTripsAndKeepsOtherKeys()
    {
        Write("""{ "schema": 1, "jdk": "C:/gone", "mavenProfiles": ["-slow"], "custom": { "kept": true } }""");
        ProjectJdk.Write(root, new JdkRequirement("temurin-21", 21, "temurin"));

        JsonObject written = JsonNode.Parse(File.ReadAllText(ProjectJdk.FileOf(root)))!.AsObject();
        Assert.Equal(2, written["schema"]!.GetValue<int>());
        Assert.Equal("temurin-21", written["jdk"]!["name"]!.GetValue<string>());
        Assert.Equal(21, written["jdk"]!["feature"]!.GetValue<int>());
        Assert.Equal("temurin", written["jdk"]!["vendor"]!.GetValue<string>());
        Assert.True(written["custom"]!["kept"]!.GetValue<bool>());
        Assert.Equal(["-slow"], ProjectJdk.MavenProfiles(root));
        Assert.Equal(new JdkRequirement("temurin-21", 21, "temurin", JdkRequirement.Project), ProjectJdk.Requirement(root, Isolated()));
    }

    [Fact]
    public void ARequirementResolvesAgainstTheTableAndAMissingOneDoesNot()
    {
        JdkRegistry registry = Isolated();
        string home = FakeJdk("a", "21.0.7", "Eclipse Adoptium");
        registry.Add(home);
        Write("""{ "schema": 2, "jdk": { "name": "work", "feature": 21, "vendor": "temurin" } }""");
        Assert.Equal(Path.GetFullPath(home), ProjectJdk.Resolve(root, registry)?.Home);

        Write("""{ "schema": 2, "jdk": { "name": null, "feature": 99, "vendor": null } }""");
        Assert.Null(ProjectJdk.Resolve(root, registry));
        Assert.Equal(99, ProjectJdk.Requirement(root, registry)?.Feature);
    }

    [Fact]
    public void ASchemaOnePathStillReadsForThisProjectWithoutTouchingTheTableOrTheFile()
    {
        string state = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        string project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        JdkRegistry registry = Isolated(Path.Combine(state, "jdks.json"));
        string home = FakeJdk("old", "17.0.12", "Amazon.com Inc.");
        string text = JsonSerializer.Serialize(new { schema = 1, jdk = home });
        Write(text, project);

        Assert.Equal(new JdkRequirement("corretto-17", 17, "corretto", JdkRequirement.Project), ProjectJdk.Requirement(project, registry));
        Assert.Equal(Path.GetFullPath(home), ProjectJdk.Resolve(project, registry)?.Home);
        Assert.Empty(registry.All());
        Assert.False(File.Exists(registry.File), "reading a project's file never writes the person's JDK table");
        Assert.Equal(text, File.ReadAllText(ProjectJdk.FileOf(project)));

        registry.Add(home, "work");
        Assert.Equal(new JdkRequirement("work", 17, "corretto", JdkRequirement.Project), ProjectJdk.Requirement(project, registry));
    }

    [Fact]
    public void ASchemaOnePathInsideTheProjectIsNotTaken()
    {
        string project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        string planted = Path.Combine(project, "jdks", "planted");
        Directory.CreateDirectory(Path.Combine(planted, "bin"));
        File.WriteAllText(Path.Combine(planted, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), string.Empty);
        File.WriteAllText(Path.Combine(planted, "release"), "JAVA_VERSION=\"21.0.1\"\n");
        Write(JsonSerializer.Serialize(new { schema = 1, jdk = planted }), project);
        Assert.Null(ProjectJdk.Requirement(project, Isolated()));
        Assert.Null(ProjectJdk.Resolve(project, Isolated()));
    }

    [Fact]
    public void ASchemaOnePathThatIsGoneAskForNothingAndACorruptFileIsIgnored()
    {
        Write(JsonSerializer.Serialize(new { schema = 1, jdk = Path.Combine(root, "gone") }));
        Assert.Null(ProjectJdk.Requirement(root, Isolated()));
        Write("{ not json");
        Assert.Null(ProjectJdk.Requirement(root, Isolated()));
        Assert.Null(ProjectJdk.Resolve(root, Isolated()));
        Assert.Empty(ProjectJdk.MavenProfiles(root));
    }

    [Fact]
    public void APerMachineChoiceWinsOverTheSharedRequirementAndStaysOutOfTheFile()
    {
        JdkRegistry registry = Isolated();
        registry.Add(FakeJdk("a", "21.0.7", "Eclipse Adoptium"));
        string chosen = FakeJdk("b", "21.0.2", "Amazon.com Inc.");
        registry.Add(chosen, "mine");
        Write("""{ "schema": 2, "jdk": { "name": "temurin-21", "feature": 21, "vendor": "temurin" } }""");
        string before = File.ReadAllText(ProjectJdk.FileOf(root));
        ProjectJdkChoices choices = new(Path.Combine(root, "state", "project-jdks.json"));

        choices.Choose(root, "mine");
        Assert.Equal(Path.GetFullPath(chosen), ProjectJdk.Resolve(root, registry, choices)?.Home);
        Assert.Equal("mine", new ProjectJdkChoices(choices.File).Chosen(root));
        Assert.Equal(before, File.ReadAllText(ProjectJdk.FileOf(root)));

        choices.Choose(root, null);
        Assert.NotEqual(Path.GetFullPath(chosen), ProjectJdk.Resolve(root, registry, choices)?.Home);
    }

    internal static JsonObject Golden() =>
        JsonNode.Parse(File.ReadAllText(ProjectHostTests.Example("jdk-resolution.json")))!.AsObject();

    private void Write(string text, string? project = null)
    {
        string file = ProjectJdk.FileOf(project ?? root);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    private JdkRegistry Isolated(string? file = null) => new(file, () => [], Path.Combine(root, "no-intellij"));

    private string FakeJdk(string name, string version, string vendor)
    {
        string home = Path.Combine(root, "jdks", name);
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), string.Empty);
        File.WriteAllText(Path.Combine(home, "release"), $"JAVA_VERSION=\"{version}\"\nIMPLEMENTOR=\"{vendor}\"\n");
        return home;
    }
}
