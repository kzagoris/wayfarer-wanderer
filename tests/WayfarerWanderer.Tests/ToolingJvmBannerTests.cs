using Avalonia.Headless.XUnit;
using Passenger.Core;
using WayfarerWanderer.App;
using WayfarerWanderer.Jvm;
using Xunit;

namespace WayfarerWanderer.Tests;

/// <summary>No JVM new enough to run the project host: the window offers to download Temurin 21, and the JDK it gets
/// becomes the tooling JDK by name and the folder is read.</summary>
[Collection(JvmTrustCollection.Name)]
public class ToolingJvmBannerTests
{
    [AvaloniaFact]
    public async Task NoToolingJvmOffersADownloadThatBecomesTheToolingJdk()
    {
        string root = WorkspaceImportTests.Copy("idea/course");
        WorkspaceTrust.Trust(root);
        Func<ToolingJvm> before = ProjectHosts.FindJvm;
        string? toolingBefore = JdkRegistry.Current.ToolingName;
        ToolingJvm real = ToolingJvms.Find();
        Jdk? downloaded = null;
        EditorWindow window = EditorApplication.CreateWindow(null);
        window.Show();
        try
        {
            ProjectHosts.FindJvm = () => ToolingJvms.Find(minimum: 999);
            await window.OpenWorkspaceAsync(root, TestContext.Current.CancellationToken);
            Assert.True(window.ToolingJvmBanner.IsVisible);
            Assert.Null(window.Session!.Snapshot);

            JdkRequirement? asked = null;
            window.DownloadJdk = wanted =>
            {
                asked = wanted;
                // The download put a JVM in the table that the host can run on.
                ProjectHosts.FindJvm = before;
                downloaded = JdkRegistry.Current.Add(real.Home);
                return Task.FromResult<Jdk?>(downloaded);
            };
            await window.DownloadToolingJvmAsync();

            Assert.Equal(ToolingJvms.RecoveryRequirement, asked);
            Assert.False(window.ToolingJvmBanner.IsVisible);
            Assert.Equal(downloaded!.Name, JdkRegistry.Current.ToolingName);
            Assert.NotNull(window.Session!.Snapshot);
        }
        finally
        {
            ProjectHosts.FindJvm = before;
            JdkRegistry.Current.ToolingName = toolingBefore;
            if (downloaded is not null)
            {
                JdkRegistry.Current.Remove(downloaded.Name);
            }
            window.Close();
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root) ?? root);
        }
    }
}
