using System.Net;
using System.Security.Cryptography;
using Passenger.Core;
using Velopack.Locators;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Updates;

namespace WayfarerWanderer.Tests;

public sealed class PackagedUpdateTests
{
    [Fact]
    public async Task NativeUpdaterAcceptsThePackagedRelease()
    {
        string? downloads = Environment.GetEnvironmentVariable("WAYFARER_TEST_PACKAGE_DIR");
        Assert.SkipWhen(downloads is null, "Requires the release workflow's packaged artifacts");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string root = Directory.CreateTempSubdirectory().FullName;
        using var server = new HttpListener();
        Uri address = new($"http://127.0.0.1:{WebAddress.FreeLoopbackPort()}/");
        server.Prefixes.Add(address.ToString());
        try
        {
            string installed = Directory.CreateDirectory(Path.Combine(root, "WayfarerWanderer.app")).FullName;
            string staging = Path.Combine(root, "updates");
            string asset = Assert.IsType<string>(Releases.ThisSystemsAsset());
            var files = Directory.GetFiles(downloads!).ToDictionary(path => Path.GetFileName(path), path => path);
            Updater updater;
            if (OperatingSystem.IsWindows())
            {
                string packages = Directory.CreateDirectory(Path.Combine(root, "packages")).FullName;
                updater = new VelopackUpdater(new TestVelopackLocator("WayfarerWanderer", "0.0.0", packages, installed,
                    root, Path.Combine(root, "Update.exe"), "win-x64"));
            }
            else
            {
                await using var package = File.OpenRead(files[asset]);
                string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(package, cancel));
                files["SHA256SUMS"] = Path.Combine(root, "SHA256SUMS");
                await File.WriteAllTextAsync(files["SHA256SUMS"], $"{hash}  {asset}\n", cancel);
                if (OperatingSystem.IsMacOS())
                {
                    string source = Assert.IsType<string>(Environment.GetEnvironmentVariable("INSTALLED_APP"));
                    var bundle = Assert.IsType<string>(MacBundles.Of(source));
                    var copied = await ChildProcesses.RunAsync(ChildProcesses.Command("/usr/bin/ditto", [bundle, installed]), cancel: cancel);
                    Assert.True(copied.Succeeded, copied.Said);
                    updater = new DiskImageUpdater(installed, staging);
                }
                else if (OperatingSystem.IsLinux()) updater = new LinuxUpdater(installed, staging);
                else throw new PlatformNotSupportedException();
            }
            LatestRelease release = new(OwnVersion.Shown, $"v{OwnVersion.Shown}", address, asset)
            {
                Files = [.. files.Select(f => new ReleaseFile(f.Key, new Uri(address, f.Key), new FileInfo(f.Value).Length))],
            };
            server.Start();
            using var serving = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            Task requests = ServeAsync(server, files, serving.Token);
            try
            {
                var result = await updater.DownloadAsync(release, _ => { }, cancel);
                Assert.True(result is UpdateOutcome.Ready, result.Said);
                Assert.Equal(OwnVersion.Shown, updater.Ready);
                // The Windows apply helper waits for its caller to exit. Linux desktop registration targets HOME.
                if (OperatingSystem.IsMacOS())
                {
                    File.WriteAllText(Path.Combine(installed, "old-copy"), "");
                    updater.Install(restart: false);
                    Assert.Null(updater.NotInstalled);
                    Assert.False(File.Exists(Path.Combine(installed, "old-copy")));
                }
            }
            finally
            {
                serving.Cancel();
                await requests;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ServeAsync(HttpListener server, Dictionary<string, string> files, CancellationToken cancel)
    {
        try
        {
            while (true)
            {
                var request = await server.GetContextAsync().WaitAsync(cancel);
                using var response = request.Response;
                await using var file = File.OpenRead(files[request.Request.Url!.AbsolutePath.TrimStart('/')]);
                response.ContentLength64 = file.Length;
                await file.CopyToAsync(response.OutputStream, cancel);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }
}
