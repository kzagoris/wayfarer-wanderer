using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Passenger.Core;
using WayfarerWanderer.App.Updates;

namespace WayfarerWanderer.Tests;

public sealed class SelfUpdateTests
{
    [Fact]
    public async Task FreshProfileAndFailedChecksAreDueWhileAnAnsweredCheckWaitsOneDay()
    {
        string folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var schedule = new UpdateSchedule(Path.Combine(folder, "new-profile"));
            CancellationToken cancel = TestContext.Current.CancellationToken;
            Assert.True(schedule.IsDue(DateTime.UtcNow));
            await schedule.RecordAsync(true, cancel);
            Assert.False(schedule.IsDue(DateTime.UtcNow.AddHours(1)));
            Assert.True(schedule.IsDue(DateTime.UtcNow.AddDays(1).AddMinutes(1)));
            await schedule.RecordAsync(false, cancel);
            Assert.True(schedule.IsDue(DateTime.UtcNow));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LinuxArchiveIsInstalledOnlyAfterVerification(bool correctHash)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Exercises the Linux package installer");
            return;
        }
        string asset = Assert.IsType<string>(Releases.ThisSystemsAsset());
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string root = Directory.CreateTempSubdirectory().FullName;
        using HttpListener server = new();
        Uri address = new($"http://127.0.0.1:{WebAddress.FreeLoopbackPort()}/");
        server.Prefixes.Add(address.ToString());
        try
        {
            string layout = Path.Combine(root, "archive");
            string app = Directory.CreateDirectory(Path.Combine(layout, "WayfarerWanderer")).FullName;
            File.WriteAllText(Path.Combine(layout, "install.sh"), "");
            string executable = Path.Combine(app, "WayfarerWanderer.App");
            File.WriteAllText(executable, "new");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.WriteAllText(executable + ".deps.json", """{"libraries":{"WayfarerWanderer.App/2.0.0":{"type":"project"}}}""");
            using MemoryStream compressed = new();
            using (GZipStream gzip = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
                TarFile.CreateFromDirectory(layout, gzip, includeBaseDirectory: false);
            byte[] archive = compressed.ToArray();
            string sum = correctHash ? Convert.ToHexStringLower(SHA256.HashData(archive)) : new string('0', 64);
            var assets = new[] { asset, "SHA256SUMS" }.Select(name => new { name, browser_download_url = new Uri(address, name), size = 0 });
            var listing = new[] { new { tag_name = "v3.0.0-beta", assets }, new { tag_name = "v2.0.0", assets } };
            server.Start();
            Task serve = Task.Run(async () =>
            {
                foreach (byte[] bytes in new[] { JsonSerializer.SerializeToUtf8Bytes(listing), Encoding.UTF8.GetBytes($"{sum}  {asset}\n"), archive })
                {
                    HttpListenerContext request = await server.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10), cancel);
                    request.Response.ContentLength64 = bytes.Length;
                    await request.Response.OutputStream.WriteAsync(bytes, cancel);
                    request.Response.Close();
                }
            }, cancel);
            ReleaseAnswer.Newer newer = Assert.IsType<ReleaseAnswer.Newer>(await Releases.AskAsync(new ReleaseFeed(address, TimeSpan.FromSeconds(5)), "1.0.0", cancel));
            string installed = Directory.CreateDirectory(Path.Combine(root, "installed")).FullName;
            File.WriteAllText(Path.Combine(installed, "old"), "");
            LinuxUpdater updater = new(installed, Path.Combine(root, "stage"));
            UpdateOutcome outcome = await updater.DownloadAsync(newer.Release, _ => { }, cancel);
            await serve;
            Assert.Equal(correctHash, outcome is UpdateOutcome.Ready);
            Assert.True(File.Exists(Path.Combine(installed, "old")));
            updater.Install(restart: false);
            Assert.Equal(correctHash, File.Exists(Path.Combine(installed, "WayfarerWanderer.App")));
            Assert.Equal(!correctHash, File.Exists(Path.Combine(installed, "old")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
