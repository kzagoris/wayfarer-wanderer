using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>What Project Structure changes: the JDK table (rename, remove with what uses a JDK, a missing home located
/// again, sources roots, two windows' changes both kept) and an IntelliJ project's own files (libraries, a module's
/// order entries, the project JDK), written as IntelliJ writes them and read back by the host.</summary>
public sealed class ProjectStructureTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("wayfarer-structure-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void RenameUpdatesTheChoicesAndTheToolingJdkButNeverTheProjectsFiles()
    {
        string file = Path.Combine(root, "state", "jdks.json");
        JdkRegistry registry = Isolated(file);
        ProjectJdkChoices choices = new(Path.Combine(root, "state", "project-jdks.json"));
        string project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        Jdk jdk = registry.Add(FakeJdk("t21", "21.0.5", "Eclipse Adoptium", sources: true));
        choices.Choose(project, jdk.Name);
        registry.ToolingName = jdk.Name;

        Jdk renamed = registry.Rename("temurin-21", "work-21", choices);
        Assert.Equal("work-21", renamed.Name);
        Assert.Equal("work-21", choices.Chosen(project));
        Assert.Equal("work-21", registry.ToolingName);
        Assert.Equal(Path.Combine(jdk.Home, "lib", "src.zip"), renamed.Sources);
        Assert.Equal([project, "Wayfarer's project host"], registry.Users("work-21", choices));
        Assert.False(File.Exists(ProjectJdk.FileOf(project)));

        Assert.True(registry.Remove("work-21", choices));
        Assert.Null(choices.Chosen(project));
        Assert.Null(registry.ToolingName);
        Assert.Empty(registry.All());
    }

    [Fact]
    public void AKeptJdkWhoseHomeIsGoneIsListedAsMissingAndCanBeLocated()
    {
        JdkRegistry registry = Isolated(Path.Combine(root, "jdks.json"));
        string home = FakeJdk("gone", "17.0.12", "Amazon.com Inc.");
        registry.Add(home, "work-17");
        Directory.Delete(home, recursive: true);

        Assert.Empty(registry.All());
        MissingKeptJdk missing = Assert.Single(registry.MissingKept());
        Assert.Equal(("work-17", Path.GetFullPath(home)), (missing.Name, missing.Home));

        string moved = FakeJdk("moved", "17.0.13", "Amazon.com Inc.");
        Jdk located = registry.Locate("work-17", moved);
        Assert.Equal(("work-17", Path.GetFullPath(moved)), (located.Name, located.Home));
        Assert.Empty(registry.MissingKept());
        Assert.Null(located.Sources);
    }

    [Fact]
    public void TwoWindowsChangesToTheTableBothStand()
    {
        string file = Path.Combine(root, "jdks.json");
        JdkRegistry one = Isolated(file);
        JdkRegistry two = Isolated(file);
        one.Add(FakeJdk("a", "17.0.12", "Amazon.com Inc."));
        two.Add(FakeJdk("b", "21.0.5", "Eclipse Adoptium"));
        Assert.Equal(["corretto-17", "temurin-21"], Isolated(file).All().Select(jdk => jdk.Name));
        Assert.Equal(["corretto-17", "temurin-21"], one.All().Select(jdk => jdk.Name));
    }

    [Fact]
    public void OnlyADownloadedJdkUnderTheJdksFolderMayHaveItsFilesDeleted()
    {
        string jdks = Path.Combine(root, "jdks");
        Jdk downloaded = new("temurin-21", Path.Combine(jdks, "temurin-21.0.5", "Contents", "Home"), "21.0.5", 21, null, JdkOrigin.Downloaded);
        Assert.Equal(Path.Combine(jdks, "temurin-21.0.5"), JdkRegistry.DeletableFolder(downloaded, jdks));
        Assert.Null(JdkRegistry.DeletableFolder(downloaded with { Origin = JdkOrigin.IntelliJ }, jdks));
        Assert.Null(JdkRegistry.DeletableFolder(downloaded with { Home = Path.Combine(root, "elsewhere") }, jdks));
    }

    [Fact]
    public void LibrariesAndOrderEntriesAreWrittenAsIntelliJWritesThem()
    {
        string project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        Directory.CreateDirectory(Path.Combine(project, ".idea"));
        File.WriteAllText(Path.Combine(project, ".idea", "misc.xml"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <project version="4">
              <component name="ProjectRootManager" version="2" languageLevel="JDK_17" project-jdk-name="17" project-jdk-type="JavaSDK" custom="kept">
                <output url="file://$PROJECT_DIR$/out" />
              </component>
            </project>
            """);
        string iml = Path.Combine(project, "app.iml");
        File.WriteAllText(iml, """
            <?xml version="1.0" encoding="UTF-8"?>
            <module type="JAVA_MODULE" version="4">
              <component name="NewModuleRootManager" inherit-compiler-output="true">
                <content url="file://$MODULE_DIR$" />
                <orderEntry type="inheritedJdk" />
                <orderEntry type="sourceFolder" forTests="false" />
              </component>
              <component name="Unknown"><kept /></component>
            </module>
            """);
        string lib = Directory.CreateDirectory(Path.Combine(project, "lib")).FullName;
        File.WriteAllText(Path.Combine(lib, "a.jar"), string.Empty);

        IdeaProjectWriter.AddMavenLibrary(project, "org.junit.jupiter:junit-jupiter:5.11.0", transitive: true);
        IdeaProjectWriter.AddJarsLibrary(project, "local", [Path.Combine(lib, "a.jar"), lib]);
        IdeaProjectWriter.AddDependency(iml, "org.junit.jupiter:junit-jupiter:5.11.0", "TEST");
        IdeaProjectWriter.AddDependency(iml, "local");
        IdeaProjectWriter.SetProjectJdk(project, "temurin-21", 21);

        IReadOnlyList<IdeaProjectLibrary> libraries = IdeaProjectWriter.Libraries(project);
        Assert.Equal(["local", "org.junit.jupiter:junit-jupiter:5.11.0"], libraries.Select(l => l.Name).Order(StringComparer.Ordinal));
        IdeaProjectLibrary junit = libraries.Single(l => l.MavenId is not null);
        Assert.Equal(["jar://$MAVEN_REPOSITORY$/org/junit/jupiter/junit-jupiter/5.11.0/junit-jupiter-5.11.0.jar!/"], junit.Classes);
        Assert.Equal(["jar://$PROJECT_DIR$/lib/a.jar!/", "file://$PROJECT_DIR$/lib"], libraries.Single(l => l.Name == "local").Classes);
        string module = File.ReadAllText(iml);
        Assert.Contains("<orderEntry type=\"library\" scope=\"TEST\" name=\"org.junit.jupiter:junit-jupiter:5.11.0\" level=\"project\" />", module, StringComparison.Ordinal);
        Assert.Contains("<orderEntry type=\"library\" name=\"local\" level=\"project\" />", module, StringComparison.Ordinal);
        Assert.Contains("<component name=\"Unknown\"><kept /></component>", module, StringComparison.Ordinal);
        Assert.Contains("\n    <orderEntry type=\"library\" name=\"local\"", module, StringComparison.Ordinal);
        string misc = File.ReadAllText(Path.Combine(project, ".idea", "misc.xml"));
        Assert.Contains("project-jdk-name=\"temurin-21\"", misc, StringComparison.Ordinal);
        Assert.Contains("languageLevel=\"JDK_21\"", misc, StringComparison.Ordinal);
        Assert.Contains("custom=\"kept\"", misc, StringComparison.Ordinal);
        Assert.StartsWith("<?xml", misc, StringComparison.Ordinal);

        IdeaProjectWriter.RemoveLibrary(project, "local", [iml]);
        Assert.DoesNotContain("name=\"local\"", File.ReadAllText(iml), StringComparison.Ordinal);
        Assert.Single(IdeaProjectWriter.Libraries(project));
        Assert.Throws<ArgumentException>(() => IdeaProjectWriter.AddMavenLibrary(project, "org.junit.jupiter:junit-jupiter:5.11.0", true));
        Assert.Throws<ArgumentException>(() => IdeaProjectWriter.AddMavenLibrary(project, "not-coordinates", true));
    }

    private JdkRegistry Isolated(string? file) => new(file, () => [], Path.Combine(root, "no-intellij"));

    private string FakeJdk(string name, string version, string vendor, bool sources = false)
    {
        string home = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), string.Empty);
        File.WriteAllText(Path.Combine(home, "release"), $"JAVA_VERSION=\"{version}\"\nIMPLEMENTOR=\"{vendor}\"\n");
        if (sources)
        {
            Directory.CreateDirectory(Path.Combine(home, "lib"));
            File.WriteAllText(Path.Combine(home, "lib", "src.zip"), string.Empty);
        }
        return home;
    }
}
