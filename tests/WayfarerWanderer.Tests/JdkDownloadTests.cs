using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>Downloading a JDK from a fake foojay catalogue: the archive checked against its checksum, unpacked (zip and
/// tar.gz, a macOS bundle, a zip without Unix modes), moved into the JDK folder and registered; and what a broken,
/// cancelled, cut-off, escaping or doubled download leaves.</summary>
public sealed class JdkDownloadTests : IDisposable
{
    private const string Version = "21.0.5+11";
    private readonly string root = Directory.CreateTempSubdirectory("wayfarer-download-").FullName;
    private readonly FakeCatalogue fake = new();
    private readonly JdkRegistry registry;
    private readonly JdkDownloader downloader;
    private readonly JdkCatalogue catalogue;

    public JdkDownloadTests()
    {
        registry = new JdkRegistry(Path.Combine(root, "state", "jdks.json"), () => [], Path.Combine(root, "no-intellij"));
        HttpClient http = new(fake);
        catalogue = new JdkCatalogue(http, new Uri("https://catalogue.test/disco/v3.0"));
        downloader = new JdkDownloader(http, catalogue, registry, Path.Combine(root, "jdks"), Path.Combine(root, "state"));
    }

    private string Jdks => Path.Combine(root, "jdks");

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A lock file still closing.
        }
    }

    [Fact]
    public async Task TheCatalogueListsPackagesForThisPlatform()
    {
        fake.Archive = Zip(Version);
        JdkPlatform platform = new("linux", "aarch64", "musl");
        IReadOnlyList<JdkPackage> packages = await catalogue.PackagesAsync(21, "temurin", platform, TestContext.Current.CancellationToken);
        JdkPackage package = Assert.Single(packages);
        Assert.Equal(("temurin", Version, 21, true), (package.Distribution, package.JavaVersion, package.Feature, package.FreeInProduction));
        string query = fake.Requests.Single(uri => uri.AbsolutePath.EndsWith("/packages", StringComparison.Ordinal)).Query;
        Assert.Contains("operating_system=linux", query, StringComparison.Ordinal);
        Assert.Contains("architecture=aarch64", query, StringComparison.Ordinal);
        Assert.Contains("lib_c_type=musl", query, StringComparison.Ordinal);
        Assert.Contains("archive_type=tar.gz", query, StringComparison.Ordinal);
        Assert.Equal([25, 21, 17], await catalogue.FeaturesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("temurin", (await catalogue.DistributionsAsync(TestContext.Current.CancellationToken))[0].Id);
    }

    [Fact]
    public void NativeImageBuildsAndTheIdeRuntimeAreNotOffered()
    {
        JdkDistribution[] all = [.. new[] { "zulu", "temurin", "mandrel", "liberica_native", "liberica", "jetbrains", "graalvm_community", "graalvm_ce17", "graalvm", "gluon_graalvm", "corretto" }
            .Select(id => new JdkDistribution(id, id))];
        Assert.Equal(["zulu", "temurin", "liberica", "corretto"], JdkCatalogue.Offered(all).Select(d => d.Id));
        Assert.Equal(["zulu", "temurin", "liberica", "graalvm", "corretto"], JdkCatalogue.Offered(all, "graalvm").Select(d => d.Id));
    }

    [Fact]
    public async Task AZipIsCheckedUnpackedMovedAndRegistered()
    {
        fake.Archive = Zip(Version);
        List<JdkDownloadProgress> seen = [];
        Jdk jdk = await downloader.DownloadAsync(Package("zip"), new SyncProgress(seen.Add), TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(Jdks, "temurin-21.0.5"), jdk.Home);
        Assert.Equal(("temurin-21", 21, JdkOrigin.Downloaded), (jdk.Name, jdk.Feature, jdk.Origin));
        Assert.Equal(jdk.Home, registry.Named("temurin-21")!.Home);
        Assert.Contains(seen, p => p.Stage == "Downloading" && p.Received > 0);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "state", "downloads"), "*.part"));
        Assert.DoesNotContain(Directory.EnumerateDirectories(Jdks), folder => Path.GetFileName(folder).StartsWith(".stage-", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(jdk.Home, "bin", "java")).HasFlag(UnixFileMode.UserExecute), "a zip without modes still gives a runnable java");
        }
    }

    [Fact]
    public async Task AMacBundleRegistersItsContentsHome()
    {
        fake.Archive = TarGz(Version, mac: true);
        Jdk jdk = await downloader.DownloadAsync(Package("tar.gz"), cancel: TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(Jdks, "temurin-21.0.5", "Contents", "Home"), jdk.Home);
        Assert.True(File.Exists(Path.Combine(Jdks, "temurin-21.0.5", "Contents", "Info.plist")));
    }

    [Fact]
    public async Task AChecksumThatDiffersInstallsNothing()
    {
        fake.Archive = Zip(Version);
        fake.Checksum = new string('0', 64);
        JdkDownloadFailed failed = await Assert.ThrowsAsync<JdkDownloadFailed>(() => downloader.DownloadAsync(Package("zip"), cancel: TestContext.Current.CancellationToken));
        Assert.Contains("SHA-256", failed.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Jdks, "temurin-21.0.5")));
        Assert.Empty(registry.All());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "state", "downloads"), "*.part"));
    }

    [Fact]
    public async Task ACutOffDownloadLeavesOnlyItsPartWhichTheNextStartRemoves()
    {
        fake.Archive = Zip(Version);
        fake.CutOff = true;
        await Assert.ThrowsAsync<JdkDownloadFailed>(() => downloader.DownloadAsync(Package("zip"), cancel: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(Jdks, "temurin-21.0.5")));
        Assert.Empty(registry.All());
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(root, "state", "downloads"), "*.part"));
        Directory.CreateDirectory(Path.Combine(Jdks, ".stage-deadbeef"));

        JdkDownloader.CleanUp(Path.Combine(root, "state"), Jdks);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "state", "downloads"), "*.part"));
        Assert.False(Directory.Exists(Path.Combine(Jdks, ".stage-deadbeef")));
    }

    [Fact]
    public async Task CancelEndsTheDownloadAndInstallsNothing()
    {
        fake.Archive = Zip(Version);
        fake.Stall = true;
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<Jdk> download = downloader.DownloadAsync(Package("zip"), new SyncProgress(_ => cancel.Cancel()), cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        await WaitUntilAsync(() => !Directory.EnumerateDirectories(Jdks).Any(folder => Path.GetFileName(folder).StartsWith(".stage-", StringComparison.Ordinal)));
        Assert.False(Directory.Exists(Path.Combine(Jdks, "temurin-21.0.5")));
        Assert.Empty(registry.All());
    }

    [Fact]
    public async Task AnEntryLeavingTheStagingFolderIsRefused()
    {
        fake.Archive = Zip(Version, extra: "../escaped.txt");
        JdkDownloadFailed failed = await Assert.ThrowsAsync<JdkDownloadFailed>(() => downloader.DownloadAsync(Package("zip"), cancel: TestContext.Current.CancellationToken));
        Assert.Contains("outside", failed.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Jdks, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
        Assert.DoesNotContain(Directory.EnumerateDirectories(Jdks), folder => Path.GetFileName(folder).StartsWith(".stage-", StringComparison.Ordinal));
        Assert.Empty(registry.All());
    }

    [Fact]
    public async Task TwoRequestsForOneJdkDownloadItOnce()
    {
        fake.Archive = Zip(Version);
        fake.Delay = TimeSpan.FromMilliseconds(300);
        Task<Jdk> first = downloader.DownloadAsync(Package("zip"), cancel: TestContext.Current.CancellationToken);
        Task<Jdk> second = downloader.DownloadAsync(Package("zip"), cancel: TestContext.Current.CancellationToken);
        Jdk[] both = await Task.WhenAll(first, second);
        Assert.Equal(both[0], both[1]);
        Assert.Equal(1, fake.ArchiveFetches);

        // Downloaded already: a later request only registers it.
        Jdk again = await downloader.DownloadAsync(Package("zip"), cancel: TestContext.Current.CancellationToken);
        Assert.Equal(both[0].Home, again.Home);
        Assert.Equal(1, fake.ArchiveFetches);
    }

    [Fact]
    public async Task ALinkOnAHostNoVendorPublishesFromIsNotFetched()
    {
        fake.Archive = Zip(Version);
        fake.ArchiveHost = "downloads.example.net";
        JdkDownloadFailed failed = await Assert.ThrowsAsync<JdkDownloadFailed>(() => downloader.DownloadAsync(Package("zip"), cancel: TestContext.Current.CancellationToken));
        Assert.Contains("downloads.example.net", failed.Message, StringComparison.Ordinal);
        Assert.Equal(0, fake.ArchiveFetches);
    }

    [Fact]
    public void AZipEntryWithoutModesIsRunnableWhereAJdkNeedsIt()
    {
        Assert.True(JdkDownloader.ModeOf("jdk-21/bin/java", 0).HasFlag(UnixFileMode.UserExecute));
        Assert.True(JdkDownloader.ModeOf("jdk-21/lib/jspawnhelper", 0).HasFlag(UnixFileMode.OtherExecute));
        Assert.False(JdkDownloader.ModeOf("jdk-21/release", 0).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute,
            JdkDownloader.ModeOf("jdk-21/lib/libjvm.so", Convert.ToInt32("750", 8) << 16));
    }

    private JdkPackage Package(string type) =>
        new(fake.Id, "temurin", Version, 21, $"OpenJDK21U-jdk.{type}", type, fake.Archive.Length, true);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }

    private static byte[] Zip(string version, string? extra = null)
    {
        using MemoryStream bytes = new();
        using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string text) in Files($"jdk-{version}/", version))
            {
                using StreamWriter writer = new(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
            if (extra is not null)
            {
                using StreamWriter writer = new(zip.CreateEntry(extra).Open());
                writer.Write("out");
            }
        }
        return bytes.ToArray();
    }

    private static byte[] TarGz(string version, bool mac)
    {
        using MemoryStream bytes = new();
        using (GZipStream gzip = new(bytes, CompressionLevel.Fastest, leaveOpen: true))
        using (TarWriter tar = new(gzip))
        {
            string prefix = mac ? $"jdk-{version}/Contents/Home/" : $"jdk-{version}/";
            List<(string, string)> files = [.. Files(prefix, version)];
            if (mac)
            {
                files.Add(($"jdk-{version}/Contents/Info.plist", "<plist/>"));
            }
            foreach ((string name, string text) in files)
            {
                PaxTarEntry entry = new(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)),
                    Mode = name.Contains("/bin/", StringComparison.Ordinal) ? (UnixFileMode)Convert.ToInt32("755", 8) : (UnixFileMode)Convert.ToInt32("644", 8),
                };
                tar.WriteEntry(entry);
            }
        }
        return bytes.ToArray();
    }

    private static IEnumerable<(string Name, string Text)> Files(string prefix, string version) =>
    [
        (prefix + "bin/java", "#!/bin/sh\n"),
        (prefix + "bin/java.exe", "MZ"),
        (prefix + "release", $"JAVA_VERSION=\"{version.Split('+')[0]}\"\nIMPLEMENTOR=\"Eclipse Adoptium\"\n"),
        (prefix + "lib/src.zip", string.Empty),
    ];

    private sealed class SyncProgress(Action<JdkDownloadProgress> report) : IProgress<JdkDownloadProgress>
    {
        public void Report(JdkDownloadProgress value) => report(value);
    }

    /// <summary>The catalogue and a vendor's download host in one: packages, ids and the archive itself.</summary>
    private sealed class FakeCatalogue : HttpMessageHandler
    {
        public string Id { get; } = "abc123";

        public byte[] Archive { get; set; } = [];

        public string? Checksum { get; set; }

        public string ArchiveHost { get; set; } = "github.com";

        public bool CutOff { get; set; }

        public bool Stall { get; set; }

        public TimeSpan Delay { get; set; }

        public int ArchiveFetches;

        public List<Uri> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            lock (Requests)
            {
                Requests.Add(uri);
            }
            string path = uri.AbsolutePath;
            if (uri.Host == "catalogue.test")
            {
                string json = path switch
                {
                    _ when path.EndsWith("/packages", StringComparison.Ordinal) =>
                        $$"""{"result":[{"id":"{{Id}}","archive_type":"tar.gz","distribution":"temurin","major_version":21,"java_version":"{{Version}}","filename":"OpenJDK21U.tar.gz","size":{{Archive.Length}},"free_use_in_production":true,"directly_downloadable":true},{"id":"other","archive_type":"msi","distribution":"temurin","major_version":21,"java_version":"{{Version}}"}],"message":""}""",
                    _ when path.EndsWith("/major_versions", StringComparison.Ordinal) =>
                        """{"result":[{"major_version":25},{"major_version":21},{"major_version":17}]}""",
                    _ when path.EndsWith("/distributions", StringComparison.Ordinal) =>
                        """{"result":[{"name":"Temurin","api_parameter":"temurin"},{"name":"Corretto","api_parameter":"corretto"}]}""",
                    _ when path.Contains("/ids/", StringComparison.Ordinal) =>
                        $$"""{"result":[{"direct_download_uri":"https://{{ArchiveHost}}/vendor/{{Id}}.bin","checksum":"{{Checksum ?? Convert.ToHexStringLower(SHA256.HashData(Archive))}}","checksum_type":"sha256"}],"message":""}""",
                    _ => "{}",
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }
            Interlocked.Increment(ref ArchiveFetches);
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }
            byte[] body = CutOff ? Archive[..(Archive.Length / 2)] : Archive;
            HttpContent content = Stall ? new StreamContent(new StallingStream(body)) : new ByteArrayContent(body);
            content.Headers.ContentLength = Archive.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    /// <summary>Gives its first bytes, then waits until the read is cancelled.</summary>
    private sealed class StallingStream(byte[] bytes) : Stream
    {
        private bool given;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position { get; set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!given)
            {
                given = true;
                int count = Math.Min(16, buffer.Length);
                bytes.AsMemory(0, count).CopyTo(buffer);
                return count;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
