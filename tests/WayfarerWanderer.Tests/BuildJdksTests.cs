using System.Diagnostics;
using System.Xml.Linq;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>Wayfarer's JDK table handed to Gradle (installation paths merged with the person's and the project's own)
/// and Maven (a generated toolchains file that keeps the installation's toolchains), and to JDT LS the same way.</summary>
public sealed class BuildJdksTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("wayfarer-build-jdks-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void GradleKeepsThePersonsAndTheProjectsPathsFirst()
    {
        string user = Directory.CreateDirectory(Path.Combine(root, "gradle-home")).FullName;
        string project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        File.WriteAllText(Path.Combine(user, "gradle.properties"), "org.gradle.java.installations.paths=/opt/mine,/opt/shared\n");
        File.WriteAllText(Path.Combine(project, "gradle.properties"), "# a comment\norg.gradle.java.installations.paths = /opt/project\n");
        Jdk[] table = [Jdk("temurin-21", "/opt/shared", "21.0.5", "Eclipse Adoptium"), Jdk("corretto-17", "/opt/corretto", "17.0.12", "Amazon.com Inc.")];

        Assert.Equal(["/opt/mine", "/opt/shared", "/opt/project", "/opt/corretto"], BuildJdks.GradleInstallationPaths(project, table, user));
        Assert.Equal("-Porg.gradle.java.installations.paths=/opt/mine,/opt/shared,/opt/project,/opt/corretto", BuildJdks.GradleArgument(project, table, user));
        Assert.Null(BuildJdks.GradleArgument(project, [], user));
    }

    [Fact]
    public void MavenToolchainsKeepTheInstallationsAndNameEachJdk()
    {
        string installation = Path.Combine(root, "toolchains.xml");
        File.WriteAllText(installation, """
            <?xml version="1.0" encoding="UTF-8"?>
            <toolchains xmlns="http://maven.apache.org/TOOLCHAINS/1.1.0">
              <toolchain><type>jdk</type><provides><version>11</version><vendor>company</vendor></provides>
                <configuration><jdkHome>/opt/company-11</jdkHome></configuration></toolchain>
            </toolchains>
            """);
        Jdk[] table = [Jdk("temurin-21", "/opt/t21", "21.0.5", "Eclipse Adoptium"), Jdk("temurin-21.0.7", "/opt/t217", "21.0.7", "Eclipse Adoptium"), Jdk("corretto-8", "/opt/c8", "1.8.0_402", "Amazon.com Inc.")];

        XNamespace ns = "http://maven.apache.org/TOOLCHAINS/1.1.0";
        XDocument document = XDocument.Parse(BuildJdks.MavenToolchains(table, installation));
        List<(string Version, string? Vendor, string? Id, string Home)> toolchains = [.. document.Root!.Elements(ns + "toolchain").Select(toolchain => (
            toolchain.Element(ns + "provides")!.Element(ns + "version")!.Value,
            toolchain.Element(ns + "provides")!.Element(ns + "vendor")?.Value,
            toolchain.Element(ns + "provides")!.Element(ns + "id")?.Value,
            toolchain.Element(ns + "configuration")!.Element(ns + "jdkHome")!.Value))];

        Assert.Equal(
            [
                ("11", "company", null, "/opt/company-11"),
                ("21.0.5", "temurin", "temurin-21", "/opt/t21"),
                ("21.0.7", "temurin", "temurin-21.0.7", "/opt/t217"),
                ("1.8.0_402", "corretto", "corretto-8", "/opt/c8"),
                ("21", "temurin", null, "/opt/t217"),
                ("1.8", "corretto", null, "/opt/c8"),
            ],
            toolchains);
        Assert.Equal(string.Empty, BuildJdks.MavenToolchains([], installation));
    }

    [Fact]
    public void TheWrappersAreToldAboutTheTable()
    {
        string home = FakeJdk("t21", "21.0.5", "Eclipse Adoptium");
        JdkRegistry registry = new(Path.Combine(root, "state", "jdks.json"), () => [home], Path.Combine(root, "no-intellij"));
        string project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        File.WriteAllText(Path.Combine(project, "gradlew"), string.Empty);
        File.WriteAllText(Path.Combine(project, "gradlew.bat"), string.Empty);
        File.WriteAllText(Path.Combine(project, "mvnw"), string.Empty);
        File.WriteAllText(Path.Combine(project, "mvnw.cmd"), string.Empty);
        JvmBuildRequest request = new(project, ".", "build", []);

        ProcessStartInfo gradle = new GradleJvmBuildAdapter { Registry = registry }.Command(request);
        Assert.Equal("-Porg.gradle.java.installations.paths=" + home, gradle.ArgumentList[0]);
        Assert.Contains("build", gradle.ArgumentList);

        ProcessStartInfo maven = new MavenJvmBuildAdapter { Registry = registry }.Command(request);
        Assert.Equal("--global-toolchains", maven.ArgumentList[0]);
        string toolchains = maven.ArgumentList[1];
        Assert.Equal(Path.Combine(root, "state", "toolchains.xml"), toolchains);
        Assert.Contains(home, File.ReadAllText(toolchains), StringComparison.Ordinal);

        JdkRegistry empty = new(null, () => [], Path.Combine(root, "no-intellij"));
        Assert.Equal(["build", "--console=plain"], new GradleJvmBuildAdapter { Registry = empty }.Command(request).ArgumentList);
    }

    [Fact]
    public void JdtLsSeesTheTableAndARuntimeForAJdkAModuleAsksFor()
    {
        string seventeen = FakeJdk("c17", "17.0.12", "Amazon.com Inc.");
        Jdk[] table = [Jdk("corretto-17", seventeen, "17.0.12", "Amazon.com Inc.")];
        JdkRequirement wanted = new(null, 17, null, JdkRequirement.GradleToolchain);
        JvmToolchain missing = new(null, 17, null) { Requirement = wanted };
        JvmToolchain import = new(null, null, null);
        JvmModule module = new("app", "app", root, [], new JvmClasspath([], [], []), import, missing, missing, [], []);
        WorkspaceSnapshot snapshot = new(1, root, JvmBuildSystem.Gradle, "settings.gradle.kts", import, [module]);

        Assert.Equal("-Porg.gradle.java.installations.paths=" + seventeen, BuildLanguageSettings.Value(snapshot, "java.import.gradle.arguments", table));
        object runtime = Assert.Single(BuildLanguageSettings.Runtimes(snapshot, table));
        Assert.Contains("JavaSE-17", runtime.ToString(), StringComparison.Ordinal);
        Assert.Contains(seventeen, runtime.ToString(), StringComparison.Ordinal);
        Assert.Empty(BuildLanguageSettings.Runtimes(snapshot, []));
    }

    private static Jdk Jdk(string name, string home, string version, string vendor) =>
        new(name, home, version, ToolingJvms.Feature(version), vendor, JdkOrigin.Added);

    private string FakeJdk(string name, string version, string vendor)
    {
        string home = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), string.Empty);
        File.WriteAllText(Path.Combine(home, "release"), $"JAVA_VERSION=\"{version}\"\nIMPLEMENTOR=\"{vendor}\"\n");
        return home;
    }
}
