using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>Wayfarer's JDK table: kept JDKs saved under unique names, IntelliJ's JDK table read under IntelliJ's names,
/// detected JDKs listed without being kept, and the one scan the tooling JVM and the project host both use.</summary>
public sealed class JdkRegistryTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("wayfarer-jdks-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void KeptJdksAreNamedAsIntelliJSuggestsAndSurviveARestart()
    {
        string first = FakeJdk("a", "21.0.5", "Eclipse Adoptium");
        string second = FakeJdk("b", "21.0.7", "Eclipse Adoptium");
        string file = Path.Combine(root, "state", "jdks.json");
        JdkRegistry registry = Isolated(file);

        Jdk a = registry.Add(first);
        Jdk b = registry.Add(second, origin: JdkOrigin.Downloaded);
        Assert.Equal("temurin-21", a.Name);
        Assert.Equal("temurin-21.0.7", b.Name);
        Assert.Equal(21, b.Feature);
        Assert.Equal(JdkOrigin.Downloaded, b.Origin);
        Assert.Equal(a.Name, registry.Add(first).Name);

        JdkRegistry reopened = Isolated(file);
        Assert.Equal(["temurin-21", "temurin-21.0.7"], reopened.All().Select(jdk => jdk.Name));
        Assert.Equal(Path.GetFullPath(second), reopened.Named("TEMURIN-21.0.7")!.Home);
    }

    [Fact]
    public void AddRefusesAFolderWithoutAJdkAndANameAlreadyTaken()
    {
        JdkRegistry registry = Isolated(null);
        Assert.Throws<ArgumentException>(() => registry.Add(Directory.CreateDirectory(Path.Combine(root, "empty")).FullName));
        registry.Add(FakeJdk("a", "17.0.12", "Amazon.com Inc."), "work");
        Assert.Throws<ArgumentException>(() => registry.Add(FakeJdk("b", "21", "Amazon.com Inc."), "Work"));
        Assert.True(registry.Remove("work"));
        Assert.False(registry.Remove("work"));
    }

    [Fact]
    public void DetectedJdksAreListedAfterKeptOnesWithoutBeingSaved()
    {
        string kept = FakeJdk("kept", "17.0.12", "Amazon.com Inc.");
        string detected = FakeJdk("detected", "17.0.13", "Amazon.com Inc.");
        string file = Path.Combine(root, "jdks.json");
        JdkRegistry registry = new(file, () => [detected, kept, Path.Combine(root, "nothing")], Path.Combine(root, "no-intellij"));
        registry.Add(kept);

        IReadOnlyList<Jdk> all = registry.All();
        Assert.Equal(2, all.Count);
        Assert.Equal(("corretto-17", JdkOrigin.Added), (all[0].Name, all[0].Origin));
        Assert.Equal(("corretto-17.0.13", JdkOrigin.Detected), (all[1].Name, all[1].Origin));
        Assert.DoesNotContain("detected", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void AKeptJdkWhoseFolderIsGoneIsLeftOutButStaysKept()
    {
        string home = FakeJdk("moved", "11.0.25", "Azul Systems, Inc.");
        string file = Path.Combine(root, "jdks.json");
        Isolated(file).Add(home);
        Directory.Move(home, home + "-elsewhere");
        Assert.Empty(Isolated(file).All());
        Directory.Move(home + "-elsewhere", home);
        Assert.Equal("zulu-11", Assert.Single(Isolated(file).All()).Name);
    }

    [Fact]
    public void IntelliJsJdkTableIsReadUnderItsNamesNewestProductFirst()
    {
        string jdk = FakeJdk("fake-jdk", "25.0.1", "Oracle Corporation");
        string older = FakeJdk("older-jdk", "25.0.0", "Oracle Corporation");
        string config = Path.Combine(root, "config");
        Table(Path.Combine(config, "IntelliJIdea2026.2"), ("openjdk-25", jdk, "JavaSDK"), ("Python 3.13", Path.Combine(root, "python"), "Python SDK"));
        Table(Path.Combine(config, "IntelliJIdea2025.1"), ("openjdk-25", older, "JavaSDK"), ("old-one", older, null));

        IReadOnlyList<(string Name, string Home)> table = JdkRegistry.IntelliJTable(config);
        Assert.Equal([("openjdk-25", Path.GetFullPath(jdk)), ("old-one", Path.GetFullPath(older))], table);

        JdkRegistry registry = new(null, () => [jdk], config);
        IReadOnlyList<Jdk> all = registry.All();
        Assert.Equal(["openjdk-25", "old-one"], all.Select(j => j.Name));
        Assert.All(all, j => Assert.Equal(JdkOrigin.IntelliJ, j.Origin));
    }

    [Fact]
    public void AnUnreadableOrHostileJdkTableIsSkipped()
    {
        string config = Path.Combine(root, "config");
        string options = Directory.CreateDirectory(Path.Combine(config, "IdeaIC2026.1", "options")).FullName;
        File.WriteAllText(Path.Combine(options, "jdk.table.xml"),
            "<!DOCTYPE application [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><application>&x;</application>");
        Assert.Empty(JdkRegistry.IntelliJTable(config));
        Assert.Empty(JdkRegistry.IntelliJTable(Path.Combine(root, "absent")));
    }

    [Theory]
    [InlineData("Eclipse Adoptium", "temurin")]
    [InlineData("Amazon.com Inc.", "corretto")]
    [InlineData("Azul Systems, Inc.", "zulu")]
    [InlineData("BellSoft", "liberica")]
    [InlineData("Microsoft", "ms")]
    [InlineData("JetBrains s.r.o.", "jbr")]
    [InlineData("Oracle Corporation", "openjdk")]
    [InlineData("International Business Machines Corporation", "semeru")]
    [InlineData("SAP SE", "sapmachine")]
    [InlineData(null, "jdk")]
    public void DistributionsAreNamedAsIntelliJNamesThem(string? vendor, string expected) =>
        Assert.Equal(expected, JdkRegistry.Distribution(vendor));

    [Fact]
    public void TheToolingJvmIsChosenFromTheRegistry()
    {
        IReadOnlyList<ToolingJvm> all = ToolingJvms.All();
        Assert.Equal([.. JdkRegistry.Current.All().Select(jdk => jdk.Home).Order()], [.. all.Select(jvm => jvm.Home).Order()]);
    }

    [Fact]
    public void TheToolingJdkIsKeptByNameBesideTheTable()
    {
        string file = Path.Combine(root, "state", "jdks.json");
        JdkRegistry registry = Isolated(file);
        Jdk jdk = registry.Add(FakeJdk("t21", "21.0.5", "Eclipse Adoptium"));
        registry.ToolingName = jdk.Name;

        JdkRegistry reopened = Isolated(file);
        Assert.Equal("temurin-21", reopened.ToolingName);
        Assert.Equal(jdk.Home, reopened.Named(reopened.ToolingName!)!.Home);
        reopened.ToolingName = null;
        Assert.Null(Isolated(file).ToolingName);
        Assert.Single(Isolated(file).All());
    }

    private JdkRegistry Isolated(string? file) => new(file, () => [], Path.Combine(root, "no-intellij"));

    private string FakeJdk(string name, string version, string vendor)
    {
        string home = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), string.Empty);
        File.WriteAllText(Path.Combine(home, "release"), $"JAVA_VERSION=\"{version}\"\nIMPLEMENTOR=\"{vendor}\"\n");
        return home;
    }

    private static void Table(string product, params (string Name, string Home, string? Type)[] jdks)
    {
        string options = Directory.CreateDirectory(Path.Combine(product, "options")).FullName;
        string entries = string.Concat(jdks.Select(jdk =>
            $"<jdk version=\"2\"><name value=\"{jdk.Name}\" />{(jdk.Type is null ? "" : $"<type value=\"{jdk.Type}\" />")}<homePath value=\"{jdk.Home}\" /></jdk>"));
        File.WriteAllText(Path.Combine(options, "jdk.table.xml"), $"<application><component name=\"ProjectJdkTable\">{entries}</component></application>");
    }
}
