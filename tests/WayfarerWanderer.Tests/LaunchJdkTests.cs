using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Passenger.Core;
using WayfarerWanderer.App;
using WayfarerWanderer.Jvm;
using Xunit;

namespace WayfarerWanderer.Tests;

/// <summary>A run configuration's own JDK, by name: found by name or by the release the name carries, refused when older
/// than the module compiles for, launched directly on, and shown in the missing-JDK banner when none matches.</summary>
[Collection(JvmTrustCollection.Name)]
public sealed class LaunchJdkTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("wayfarer-launch-jdk-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void ANamedJdkResolvesByNameThenByItsReleaseAndAnOlderOneIsRefused()
    {
        JdkRegistry registry = new(null, () => [], Path.Combine(root, "no-intellij"));
        Jdk seventeen = registry.Add(FakeJdk("c17", "17.0.12", "Amazon.com Inc."), "work-17");
        Jdk twentyOne = registry.Add(FakeJdk("t21", "21.0.5", "Eclipse Adoptium"));
        JvmModule module = Module(compilesFor: 17);

        Assert.Equal(seventeen.Home, LaunchJdks.Home(module, Configuration("work-17"), registry));
        Assert.Equal(twentyOne.Home, LaunchJdks.Home(module, Configuration("corretto-21"), registry));
        LaunchJdkMissingException missing = Assert.Throws<LaunchJdkMissingException>(() => LaunchJdks.Home(module, Configuration("zulu-25"), registry));
        Assert.Equal(new JdkRequirement("zulu-25", 25, null, JdkRequirement.Launch), missing.Requirement);
        InvalidDataException old = Assert.Throws<InvalidDataException>(() => LaunchJdks.Home(Module(compilesFor: 21), Configuration("work-17"), registry));
        Assert.Contains("Java 17", old.Message, StringComparison.Ordinal);
        Assert.Contains("Java 21", old.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AJdkNameCarriesItsReleaseAsTheHostReadsIt()
    {
        Assert.Equal(21, JdkRequirement.FeatureOfName("corretto-21"));
        Assert.Equal(21, JdkRequirement.FeatureOfName("temurin-21.0.5"));
        Assert.Equal(17, JdkRequirement.FeatureOfName("17 (2)"));
        Assert.Equal(8, JdkRequirement.FeatureOfName("1.8"));
        Assert.Equal(8, JdkRequirement.FeatureOfName("jdk1.8.0_202"));
        Assert.Equal(21, JdkRequirement.FeatureOfName("JDK-21"));
        Assert.Null(JdkRequirement.FeatureOfName("my-jdk-v2"));
        Assert.Null(JdkRequirement.FeatureOfName("wanted-0012ab34"));
        Assert.Null(JdkRequirement.FeatureOfName("Project JDK"));
    }

    [Fact]
    public void ADirectLaunchRunsOnTheNamedJdk()
    {
        string name = "launch-" + Guid.NewGuid().ToString("N")[..8];
        Jdk jdk = JdkRegistry.Current.Add(FakeJdk("named", "21.0.5", "Eclipse Adoptium"), name);
        try
        {
            JvmModule module = Module(compilesFor: 17);
            WorkspaceSnapshot snapshot = new(1, root, JvmBuildSystem.Gradle, "settings.gradle.kts", module.ImportToolchain, [module]);
            ProcessStartInfo start = new DirectJvmLaunchAdapter().Command(snapshot, Configuration(name));
            Assert.Equal(Path.Combine(jdk.Home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), start.FileName);
        }
        finally
        {
            JdkRegistry.Current.Remove(name);
        }
    }

    [AvaloniaFact]
    public async Task ARunConfigurationsMissingJdkShowsTheBannerAndChoosingOneKeepsItUnderThatName()
    {
        int absent = Math.Max(Environment.Version.Major, JdkRegistry.Current.All().Select(jdk => jdk.Feature).DefaultIfEmpty(0).Max()) + 40;
        string wanted = $"nowhere-{absent}";
        string project = WorkspaceImportTests.Copy("idea/course");
        string chosen = FakeJdk("chosen", $"{absent}.0.1", "Eclipse Adoptium");
        // The project itself names a JDK of this machine, so only the run configuration's is missing.
        Jdk? own = JdkRegistry.Current.All().FirstOrDefault(jdk => jdk.Feature >= 8);
        Assert.SkipWhen(own is null, "No JDK here for the project itself.");
        string misc = Path.Combine(project, ".idea", "misc.xml");
        File.WriteAllText(misc, File.ReadAllText(misc).Replace("project-jdk-name=\"openjdk-25\"", $"project-jdk-name=\"{own!.Name}\"", StringComparison.Ordinal)
            .Replace("languageLevel=\"JDK_25\"", own.Feature <= 8 ? "languageLevel=\"JDK_1_8\"" : $"languageLevel=\"JDK_{own.Feature}\"", StringComparison.Ordinal));
        WorkspaceTrust.Trust(project);
        EditorWindow window = EditorApplication.CreateWindow(null);
        window.Show();
        try
        {
            await window.OpenWorkspaceAsync(project, TestContext.Current.CancellationToken);
            Assert.False(window.JdkBanner.IsVisible);
            window.PickRunJdk = _ => Task.FromResult<string?>(wanted);
            await window.ChooseRunJdkAsync();
            JvmLaunchConfiguration saved = Assert.Single(await window.Session!.Launches.ListAsync(TestContext.Current.CancellationToken));
            Assert.Equal((wanted, string.Empty), (saved.Jdk, saved.JavaHome));

            await window.RunCurrentAsync();
            Assert.True(window.JdkBanner.IsVisible);
            Assert.Equal($"The run configuration '{saved.Name}' needs JDK {absent} (any vendor). No installed JDK matches.", window.JdkBannerText.Text);

            window.PickJdk = _ => Task.FromResult<Jdk?>(new Jdk("picked", chosen, $"{absent}.0.1", absent, "Eclipse Adoptium", JdkOrigin.Detected));
            await window.ChooseJdkAsync();
            Assert.False(window.JdkBanner.IsVisible);
            Assert.True(PathNames.Same(Path.GetFullPath(chosen), JdkRegistry.Current.Named(wanted)!.Home));
        }
        finally
        {
            window.Close();
            JdkRegistry.Current.Remove(wanted);
            await ProjectHosts.ReleaseAsync(project);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(project) ?? project);
        }
    }

    private JvmModule Module(int compilesFor)
    {
        JvmToolchain toolchain = new(null, compilesFor, null);
        return new JvmModule("app", "app", root, [], new JvmClasspath(["app.jar"], [], ["app.jar"]), toolchain, toolchain, toolchain, ["run"],
            [new JvmMainClass("example.App", string.Empty)]);
    }

    private static JvmLaunchConfiguration Configuration(string jdk) =>
        new() { Name = "App", Module = "app", MainClass = "example.App", Jdk = jdk };

    private string FakeJdk(string name, string version, string vendor)
    {
        string home = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), string.Empty);
        File.WriteAllText(Path.Combine(home, "release"), $"JAVA_VERSION=\"{version}\"\nIMPLEMENTOR=\"{vendor}\"\n");
        return home;
    }
}
