using Avalonia.Headless.XUnit;
using Passenger.Core;
using WayfarerWanderer.App;
using WayfarerWanderer.Jvm;
using Xunit;

namespace WayfarerWanderer.Tests;

/// <summary>The window's missing-JDK banner, after a real import: an IntelliJ module that names a JDK no JDK here meets
/// shows it, and Choose… with a JDK of the table, or Download… with the JDK it downloads, imports again and puts it
/// away.</summary>
public class MissingJdkBannerTests
{
    [AvaloniaFact]
    public async Task AMissingJdkShowsTheBannerAndChoosingOneImportsAgainAndPutsItAway()
    {
        // A name and a feature no JDK here has, so the test cannot pass by accident on a machine with every JDK.
        // Letters only: a release in the name (wanted-19…) would be asked for instead of the language level.
        string name = "wanted-" + new string([.. Guid.NewGuid().ToString("N")[..8].Select(c => char.IsDigit(c) ? (char)('g' + c - '0') : c)]);
        int absent = Math.Max(Environment.Version.Major, JdkRegistry.Current.All().Select(jdk => jdk.Feature).DefaultIfEmpty(0).Max()) + 40;
        string root = WorkspaceImportTests.Copy("idea/course");
        string iml = Path.Combine(root, "Course.iml");
        File.WriteAllText(iml, File.ReadAllText(iml).Replace("<orderEntry type=\"inheritedJdk\" />",
            $"<orderEntry type=\"jdk\" jdkName=\"{name}\" jdkType=\"JavaSDK\" />", StringComparison.Ordinal));
        string misc = Path.Combine(root, ".idea", "misc.xml");
        File.WriteAllText(misc, File.ReadAllText(misc).Replace("languageLevel=\"JDK_25\"", $"languageLevel=\"JDK_{absent}\"", StringComparison.Ordinal));
        string jdk = Directory.CreateTempSubdirectory("wayfarer-chosen-jdk-").FullName;
        Directory.CreateDirectory(Path.Combine(jdk, "bin"));
        File.WriteAllText(Path.Combine(jdk, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), string.Empty);
        File.WriteAllText(Path.Combine(jdk, "release"), $"JAVA_VERSION=\"{absent}.0.1\"\nIMPLEMENTOR=\"Eclipse Adoptium\"\n");
        WorkspaceTrust.Trust(root);
        EditorWindow window = EditorApplication.CreateWindow(null);
        window.Show();
        try
        {
            await window.OpenWorkspaceAsync(root, TestContext.Current.CancellationToken);
            Assert.True(window.JdkBanner.IsVisible);
            Assert.Equal($"Module 'Course' needs JDK {absent} (any vendor). No installed JDK matches.", window.JdkBannerText.Text);
            Assert.True(window.DownloadJdkButton.IsEnabled);
            Assert.True(window.ChooseJdkButton.IsEnabled);

            MissingJdk? asked = null;
            window.PickJdk = missing =>
            {
                asked = missing;
                return Task.FromResult<Jdk?>(new Jdk("picked", jdk, $"{absent}.0.1", absent, "Eclipse Adoptium", JdkOrigin.Detected));
            };
            await window.ChooseJdkAsync();

            Assert.Equal(new JdkRequirement(name, absent, null, JdkRequirement.Idea), asked?.Requirement);
            Assert.Equal(name, JdkRegistry.Current.Named(name)?.Name);
            Assert.False(window.JdkBanner.IsVisible);
            Assert.True(PathNames.Same(Path.GetFullPath(jdk), Assert.Single(window.Session!.Snapshot!.Modules).CompileToolchain.Home!));
        }
        finally
        {
            window.Close();
            await ProjectHosts.ReleaseAsync(root);
            JdkRegistry.Current.Remove(name);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root) ?? root);
            Directory.Delete(jdk, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task DownloadingTheMissingJdkImportsAgainAndPutsTheBannerAway()
    {
        // A name and a feature no JDK here has, so the test cannot pass by accident on a machine with every JDK.
        // Letters only: a release in the name (wanted-19…) would be asked for instead of the language level.
        string name = "wanted-" + new string([.. Guid.NewGuid().ToString("N")[..8].Select(c => char.IsDigit(c) ? (char)('g' + c - '0') : c)]);
        int absent = Math.Max(Environment.Version.Major, JdkRegistry.Current.All().Select(jdk => jdk.Feature).DefaultIfEmpty(0).Max()) + 40;
        string root = WorkspaceImportTests.Copy("idea/course");
        string iml = Path.Combine(root, "Course.iml");
        File.WriteAllText(iml, File.ReadAllText(iml).Replace("<orderEntry type=\"inheritedJdk\" />",
            $"<orderEntry type=\"jdk\" jdkName=\"{name}\" jdkType=\"JavaSDK\" />", StringComparison.Ordinal));
        string misc = Path.Combine(root, ".idea", "misc.xml");
        File.WriteAllText(misc, File.ReadAllText(misc).Replace("languageLevel=\"JDK_25\"", $"languageLevel=\"JDK_{absent}\"", StringComparison.Ordinal));
        string jdk = Directory.CreateTempSubdirectory("wayfarer-chosen-jdk-").FullName;
        Directory.CreateDirectory(Path.Combine(jdk, "bin"));
        File.WriteAllText(Path.Combine(jdk, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), string.Empty);
        File.WriteAllText(Path.Combine(jdk, "release"), $"JAVA_VERSION=\"{absent}.0.1\"\nIMPLEMENTOR=\"Eclipse Adoptium\"\n");
        WorkspaceTrust.Trust(root);
        EditorWindow window = EditorApplication.CreateWindow(null);
        window.Show();
        try
        {
            await window.OpenWorkspaceAsync(root, TestContext.Current.CancellationToken);
            Assert.True(window.JdkBanner.IsVisible);
            Assert.Equal($"Module 'Course' needs JDK {absent} (any vendor). No installed JDK matches.", window.JdkBannerText.Text);
            Assert.True(window.DownloadJdkButton.IsEnabled);
            Assert.True(window.ChooseJdkButton.IsEnabled);

            JdkRequirement? asked = null;
            window.DownloadJdk = wanted =>
            {
                asked = wanted;
                return Task.FromResult<Jdk?>(JdkRegistry.Current.Add(jdk, origin: JdkOrigin.Downloaded));
            };
            await window.DownloadMissingJdkAsync();

            // Kept under its own name: the IntelliJ name resolves to it by its release.
            Assert.Equal(new JdkRequirement(name, absent, null, JdkRequirement.Idea), asked);
            Assert.False(window.JdkBanner.IsVisible);
            Assert.True(PathNames.Same(Path.GetFullPath(jdk), Assert.Single(window.Session!.Snapshot!.Modules).CompileToolchain.Home!));
        }
        finally
        {
            window.Close();
            await ProjectHosts.ReleaseAsync(root);
            JdkRegistry.Current.Remove(name);
            foreach (Jdk kept in JdkRegistry.Current.All().Where(k => PathNames.Same(k.Home, jdk)))
            {
                JdkRegistry.Current.Remove(kept.Name);
            }
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root) ?? root);
            Directory.Delete(jdk, recursive: true);
        }
    }
}
