using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

public sealed class KotlinServerInstallsTests
{
    [Fact]
    public void NewestPicksTheHighestVersionThatHasALauncher()
    {
        string folder = Directory.CreateTempSubdirectory("wayfarer kotlin ").FullName;
        try
        {
            foreach (string version in new[] { "9.0.1", "271.2085.0", "1000.0.0", "not-a-version" })
            {
                string home = Path.Combine(folder, version);
                Directory.CreateDirectory(Path.Combine(home, "bin"));
                // 1000.0.0 is an unfinished unpack: no launcher yet.
                if (version != "1000.0.0") File.WriteAllText(KotlinServerInstalls.Launcher(home), "");
            }
            // Numeric, not ordinal: 271 beats 9.
            Assert.Equal(Path.Combine(folder, "271.2085.0"), KotlinServerInstalls.Newest(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void NewestIsNullWithoutAnInstall() =>
        Assert.Null(KotlinServerInstalls.Newest(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
}
