using WayfarerWanderer.Jvm;
using Xunit;

namespace WayfarerWanderer.Tests;

/// <summary>What the missing-JDK banner says of a snapshot, which JDKs Choose… offers, and where a choice goes: the
/// project's into this machine's choices, an IntelliJ name into the table under that name, a build's into the table.</summary>
public sealed class MissingJdksTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("wayfarer-missing-jdks-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void EachModulesFirstMissingRequirementIsListedAndTheProjectsOnce()
    {
        JdkRequirement seventeen = new(null, 17, null, JdkRequirement.GradleToolchain);
        JdkRequirement project = new("temurin-21", 21, "temurin", JdkRequirement.Project);
        WorkspaceSnapshot snapshot = Snapshot(
            Module("api", Missing(seventeen), Missing(seventeen)),
            Module("web", new JvmToolchain("/jdks/21", 21, null) { Requirement = seventeen with { Feature = 21 } }, Missing(project)),
            Module("cli", Missing(project), Missing(project)),
            Module("whole", new JvmToolchain("/jdks/21", 21, null), new JvmToolchain("/jdks/21", 21, null)));

        IReadOnlyList<MissingJdk> missing = MissingJdks.Of(snapshot);
        Assert.Equal([new MissingJdk("api", seventeen), new MissingJdk(null, project)], missing);
        Assert.Equal("Module 'api' needs JDK 17 (any vendor), and 1 more. No installed JDK matches.", MissingJdks.Say(missing));
        Assert.Equal("The project needs JDK 21 (temurin). No installed JDK matches.", MissingJdks.Say([new MissingJdk(null, project)]));
        Assert.Equal("Module 'app' needs the JDK named corretto-11. No installed JDK matches.",
            MissingJdks.Say([new MissingJdk("app", new JdkRequirement("corretto-11", null, null, JdkRequirement.Idea))]));
        Assert.Empty(MissingJdks.Of(null));
        Assert.Empty(MissingJdks.Of(Snapshot(Module("whole", new JvmToolchain(null, null, null), new JvmToolchain(null, null, null)))));
    }

    [Fact]
    public void ChooseOffersTheTablesJdksOfTheFeatureAskedFor()
    {
        Jdk a = new("a", "/a", "17.0.1", 17, null, JdkOrigin.Added);
        Jdk b = new("b", "/b", "21.0.1", 21, null, JdkOrigin.Detected);
        Assert.Equal([a], MissingJdks.Candidates(new JdkRequirement(null, 17, null), [a, b]));
        Assert.Equal([a, b], MissingJdks.Candidates(new JdkRequirement("named", null, null), [a, b]));
    }

    [Fact]
    public void AChoiceForTheProjectStaysOnThisMachine()
    {
        JdkRegistry registry = Isolated();
        string home = FakeJdk("b", "21.0.2", "Amazon.com Inc.");
        Jdk detected = new JdkRegistry(null, () => [home], Path.Combine(root, "no-intellij")).All()[0];
        ProjectJdkChoices choices = new(null);
        string project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;

        Jdk kept = MissingJdks.Choose(project, new MissingJdk(null, new JdkRequirement("temurin-21", 21, "temurin", JdkRequirement.Project)), detected, registry, choices);
        Assert.Equal(kept.Name, choices.Chosen(project));
        Assert.Equal(kept.Home, registry.Named(kept.Name)?.Home);
        Assert.False(File.Exists(ProjectJdk.FileOf(project)), "the shared project file is not written");
    }

    [Fact]
    public void AChoiceForAnIntelliJNameKeepsTheJdkUnderThatNameAndABuildsIsKept()
    {
        JdkRegistry registry = Isolated();
        ProjectJdkChoices choices = new(null);
        string home = FakeJdk("c", "11.0.25", "Azul Systems, Inc.");
        Jdk chosen = new JdkRegistry(null, () => [home], Path.Combine(root, "no-intellij")).All()[0];

        Jdk kept = MissingJdks.Choose(root, new MissingJdk("app", new JdkRequirement("corretto-11", 11, null, JdkRequirement.Idea)), chosen, registry, choices);
        Assert.Equal("corretto-11", kept.Name);
        Assert.Null(choices.Chosen(root));

        string other = FakeJdk("d", "17.0.3", "Eclipse Adoptium");
        Jdk gradle = new JdkRegistry(null, () => [other], Path.Combine(root, "no-intellij")).All()[0];
        Jdk registered = MissingJdks.Choose(root, new MissingJdk("api", new JdkRequirement(null, 17, null, JdkRequirement.GradleToolchain)), gradle, registry, choices);
        Assert.Equal(JdkOrigin.Added, registered.Origin);
    }

    private static JvmToolchain Missing(JdkRequirement requirement) => new(null, requirement.Feature, null) { Requirement = requirement };

    private static JvmModule Module(string name, JvmToolchain compile, JvmToolchain runtime) =>
        new(name, name, "/work/" + name, [], new JvmClasspath([], [], []), new JvmToolchain("/jdks/import", 21, null), compile, runtime, [], []);

    private static WorkspaceSnapshot Snapshot(params JvmModule[] modules) =>
        new(1, "/work", JvmBuildSystem.Gradle, "/work/settings.gradle.kts", new JvmToolchain("/jdks/tooling", 21, null), modules);

    private JdkRegistry Isolated() => new(null, () => [], Path.Combine(root, "no-intellij"));

    private string FakeJdk(string name, string version, string vendor)
    {
        string home = Path.Combine(root, "jdks", name);
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), string.Empty);
        File.WriteAllText(Path.Combine(home, "release"), $"JAVA_VERSION=\"{version}\"\nIMPLEMENTOR=\"{vendor}\"\n");
        return home;
    }
}
