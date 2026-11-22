using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace WayfarerWanderer.Jvm;

/// <summary>The machine a JDK is downloaded for, in the foojay Disco API's words: <c>windows</c>, <c>macos</c> or
/// <c>linux</c>; <c>x64</c>, <c>aarch64</c>, …; and on Linux <c>glibc</c> or <c>musl</c>.</summary>
public sealed record JdkPlatform(string OperatingSystem, string Architecture, string? LibC)
{
    /// <summary>This machine: its own architecture, not the process's, so an x64 Wayfarer on arm64 gets an arm64 JDK.</summary>
    public static JdkPlatform Current()
    {
        string os = System.OperatingSystem.IsWindows() ? "windows" : System.OperatingSystem.IsMacOS() ? "macos" : "linux";
        string architecture = RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            System.Runtime.InteropServices.Architecture.S390x => "s390x",
            System.Runtime.InteropServices.Architecture.Ppc64le => "ppc64le",
            System.Runtime.InteropServices.Architecture.RiscV64 => "riscv64",
            _ => "x64",
        };
        string? libc = os == "linux" ? (Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*").Any() ? "musl" : "glibc") : null;
        return new JdkPlatform(os, architecture, libc);
    }

    /// <summary>The archive types this platform unpacks, preferred first.</summary>
    public IReadOnlyList<string> ArchiveTypes => OperatingSystem == "windows" ? ["zip"] : ["tar.gz", "zip"];
}

/// <summary>A JDK vendor as the catalogue names it: its API name (<c>temurin</c>) and the name shown.</summary>
public sealed record JdkDistribution(string Id, string Name);

/// <summary>A downloadable JDK package: the catalogue's id, the distribution, the Java version it holds, its archive
/// file, type and size, and whether its vendor allows free use in production.</summary>
public sealed record JdkPackage(string Id, string Distribution, string JavaVersion, int Feature, string FileName, string ArchiveType, long Size, bool FreeInProduction);

/// <summary>Where a package downloads from and the SHA-256 the catalogue gives for it.</summary>
public sealed record JdkPackageLink(Uri Uri, string? Sha256);

/// <summary>How far a download has come: bytes received of the whole (0 when unknown), and what it is doing.</summary>
public sealed record JdkDownloadProgress(long Received, long Total, string Stage);

/// <summary>A download did not give a JDK: the reason, said to the person as it is.</summary>
public sealed class JdkDownloadFailed(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The foojay Disco API (<c>api.foojay.io/disco/v3.0</c>), the catalogue Gradle's foojay resolver also uses: the
/// vendors, the feature releases, and each vendor's packages for a platform with a direct link and a checksum.
/// <c>WAYFARER_JDK_CATALOGUE</c> points it at a company mirror.
/// </summary>
public sealed class JdkCatalogue(HttpClient http, Uri? baseUri = null)
{
    public static Uri DefaultUri { get; } = new("https://api.foojay.io/disco/v3.0/");

    private readonly Uri root = AsFolder(baseUri ?? (Environment.GetEnvironmentVariable("WAYFARER_JDK_CATALOGUE") is { Length: > 0 } mirror ? new Uri(mirror) : DefaultUri));

    /// <summary>The vendors the catalogue lists, in its order.</summary>
    public async Task<IReadOnlyList<JdkDistribution>> DistributionsAsync(CancellationToken cancel = default)
    {
        using JsonDocument json = await GetAsync("distributions?include_versions=false&include_synonyms=false", cancel).ConfigureAwait(false);
        return [.. Results(json)
            .Where(d => !(d.TryGetProperty("maintained", out JsonElement maintained) && maintained.ValueKind == JsonValueKind.False))
            .Where(d => !(d.TryGetProperty("available", out JsonElement available) && available.ValueKind == JsonValueKind.False))
            .Select(d => new JdkDistribution(String(d, "api_parameter") ?? string.Empty, String(d, "name") ?? String(d, "api_parameter") ?? string.Empty))
            .Where(d => d.Id.Length > 0)];
    }

    /// <summary>Whether Download JDK leaves a vendor out: GraalVM's builds (<c>graalvm</c>, <c>graalvm_community</c>,
    /// <c>gluon_graalvm</c>, the old <c>graalvm_ce*</c>), Mandrel and Liberica Native make native images, and
    /// JetBrains' is the runtime IDEs ship with, none of them a JDK to build and run a project on.</summary>
    public static bool IsSpecialised(string id) =>
        id.Contains("graalvm", StringComparison.OrdinalIgnoreCase) || id is "mandrel" or "liberica_native" or "jetbrains";

    /// <summary>The vendors Download JDK offers: <paramref name="all"/> without the <see cref="IsSpecialised"/> ones,
    /// except <paramref name="wanted"/>, a catalogue id, when a project asks for it.</summary>
    public static IReadOnlyList<JdkDistribution> Offered(IEnumerable<JdkDistribution> all, string? wanted = null) =>
        [.. all.Where(d => !IsSpecialised(d.Id) || string.Equals(d.Id, wanted, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The feature releases still maintained and generally available, newest first.</summary>
    public async Task<IReadOnlyList<int>> FeaturesAsync(CancellationToken cancel = default)
    {
        using JsonDocument json = await GetAsync("major_versions?ea=false&ga=true&maintained=true", cancel).ConfigureAwait(false);
        return [.. Results(json).Select(v => v.TryGetProperty("major_version", out JsonElement major) && major.TryGetInt32(out int n) ? n : 0)
            .Where(n => n > 0).Distinct().OrderDescending()];
    }

    /// <summary>The newest packages of a vendor's feature release for a platform, the platform's preferred archive type
    /// first; empty when the vendor has no build for it.</summary>
    public async Task<IReadOnlyList<JdkPackage>> PackagesAsync(int feature, string distribution, JdkPlatform platform, CancellationToken cancel = default)
    {
        string query = $"packages?version={feature}&distribution={Uri.EscapeDataString(distribution)}&operating_system={platform.OperatingSystem}"
            + $"&architecture={platform.Architecture}&package_type=jdk&release_status=ga&latest=available&javafx_bundled=false"
            + string.Concat(platform.ArchiveTypes.Select(type => "&archive_type=" + type))
            + (platform.LibC is { } libc ? "&lib_c_type=" + libc : string.Empty);
        using JsonDocument json = await GetAsync(query, cancel).ConfigureAwait(false);
        List<JdkPackage> packages = [];
        foreach (JsonElement p in Results(json))
        {
            if (String(p, "id") is not { } id || String(p, "archive_type") is not { } type || !platform.ArchiveTypes.Contains(type))
            {
                continue;
            }
            if (p.TryGetProperty("directly_downloadable", out JsonElement direct) && direct.ValueKind == JsonValueKind.False)
            {
                continue;
            }
            string version = String(p, "java_version") ?? feature.ToString(CultureInfo.InvariantCulture);
            int major = p.TryGetProperty("major_version", out JsonElement m) && m.TryGetInt32(out int n) ? n : feature;
            long size = p.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long bytes) ? bytes : 0;
            bool free = p.TryGetProperty("free_use_in_production", out JsonElement f) && f.ValueKind == JsonValueKind.True;
            packages.Add(new JdkPackage(id, String(p, "distribution") ?? distribution, version, major, String(p, "filename") ?? id, type, size, free));
        }
        return [.. packages.OrderBy(p => platform.ArchiveTypes.ToList().IndexOf(p.ArchiveType)).ThenByDescending(p => p.JavaVersion, Comparer<string>.Create(JdkResolution.CompareVersions))];
    }

    /// <summary>The direct link and checksum of a package.</summary>
    public async Task<JdkPackageLink> LinkAsync(string id, CancellationToken cancel = default)
    {
        using JsonDocument json = await GetAsync("ids/" + Uri.EscapeDataString(id), cancel).ConfigureAwait(false);
        JsonElement info = Results(json).FirstOrDefault();
        if (info.ValueKind != JsonValueKind.Object || String(info, "direct_download_uri") is not { Length: > 0 } uri)
        {
            throw new JdkDownloadFailed($"The JDK catalogue gave no download link for package {id}.");
        }
        string? checksum = String(info, "checksum_type") is { } kind && !kind.Equals("sha256", StringComparison.OrdinalIgnoreCase) ? null : String(info, "checksum");
        return new JdkPackageLink(new Uri(uri), checksum is { Length: 64 } ? checksum.ToLowerInvariant() : null);
    }

    private async Task<JsonDocument> GetAsync(string relative, CancellationToken cancel)
    {
        Uri uri = new(root, relative);
        try
        {
            using HttpResponseMessage response = await http.GetAsync(uri, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new JdkDownloadFailed($"The JDK catalogue at {root.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            await using Stream body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(body, cancellationToken: cancel).ConfigureAwait(false);
        }
        catch (Exception unreachable) when (unreachable is HttpRequestException or JsonException || (unreachable is TaskCanceledException && !cancel.IsCancellationRequested))
        {
            throw new JdkDownloadFailed($"The JDK catalogue at {root.Host} could not be reached: {unreachable.Message}", unreachable);
        }
    }

    private static IEnumerable<JsonElement> Results(JsonDocument json) =>
        json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray()
            : json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray() : [];

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri AsFolder(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}

/// <summary>
/// Downloads a JDK the person asked for and registers it, as IntelliJ's Download JDK does: the archive goes to
/// <c>&lt;state&gt;/downloads/&lt;id&gt;.part</c>, is checked against its SHA-256, unpacked into a staging folder beside
/// the target, and the JDK's home moved to <c>~/.jdks/&lt;distribution&gt;-&lt;version&gt;</c>, IntelliJ's folder too.
/// Only https links on hosts vendors publish from are taken: the checksum comes from the same catalogue as the link,
/// so it proves the archive arrived whole, not who published it. A request for a JDK already downloading, from this
/// process or another (a lock file beside the <c>.part</c>), waits for that one and takes its JDK.
/// </summary>
public sealed class JdkDownloader
{
    /// <summary>The hosts JDK archives are taken from: the vendors' own and the release hosts they publish on.</summary>
    public static IReadOnlySet<string> Hosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
        "corretto.aws", "d3pxv6yz143wms.cloudfront.net", "cdn.azul.com", "download.bell-sw.com",
        "aka.ms", "download.visualstudio.microsoft.com", "download.oracle.com", "download.java.net",
        "cache-redirector.jetbrains.com", "builds.openlogic.com", "download.eclipse.org", "cdn.semeru.ibm.com",
    };

    private static readonly ConcurrentDictionary<string, Task<Jdk>> Running = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient http;
    private readonly JdkCatalogue catalogue;
    private readonly JdkRegistry registry;
    private readonly string downloads;

    /// <param name="jdksFolder">Where JDKs are put; <see cref="DefaultFolder"/> when null.</param>
    /// <param name="stateFolder">Where partial downloads are kept; beside the registry's file when null.</param>
    public JdkDownloader(HttpClient http, JdkCatalogue catalogue, JdkRegistry registry, string? jdksFolder = null, string? stateFolder = null)
    {
        this.http = http;
        this.catalogue = catalogue;
        this.registry = registry;
        Folder = jdksFolder ?? DefaultFolder;
        downloads = Path.Combine(stateFolder ?? BuildJdks.StateFolder(registry), "downloads");
    }

    /// <summary><c>~/.jdks</c>, where IntelliJ puts the JDKs it downloads, which the scanner already looks in.</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks");

    public string Folder { get; }

    /// <summary>The folder a package's JDK goes in: <c>&lt;distribution&gt;-&lt;version&gt;</c>, the version without
    /// its build number.</summary>
    public string TargetOf(JdkPackage package)
    {
        string version = package.JavaVersion.Split('+')[0];
        string name = string.Concat($"{package.Distribution}-{version}".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Folder, name);
    }

    /// <summary>Downloads, checks, unpacks and registers <paramref name="package"/>, and returns its JDK.</summary>
    public Task<Jdk> DownloadAsync(JdkPackage package, IProgress<JdkDownloadProgress>? progress = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        string target = TargetOf(package);
        // A second request for the same JDK joins the first; its cancel ends only its own wait, while the first
        // request's cancel ends the download.
        Task<Jdk> download = Running.GetOrAdd(target, _ => Task.Run(() => DownloadCoreAsync(package, target, progress, cancel), CancellationToken.None));
        _ = download.ContinueWith(_ => Running.TryRemove(new KeyValuePair<string, Task<Jdk>>(target, download)), TaskScheduler.Default);
        return download.WaitAsync(cancel);
    }

    private async Task<Jdk> DownloadCoreAsync(JdkPackage package, string target, IProgress<JdkDownloadProgress>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(Folder);
        string part = Path.Combine(downloads, Safe(package.Id) + ".part");
        await using FileStream held = await LockAsync(Path.Combine(downloads, Safe(package.Id) + ".lock"), cancel).ConfigureAwait(false);
        if (HomeIn(target) is { } already)
        {
            // Downloaded by another window, or earlier: only registered.
            return registry.Add(already, origin: JdkOrigin.Downloaded);
        }
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new JdkDownloadFailed($"{target} already exists and holds no JDK. Move it away, or choose another location.");
        }
        CheckSpace(package.Size);
        JdkPackageLink link = await catalogue.LinkAsync(package.Id, cancel).ConfigureAwait(false);
        if (link.Uri.Scheme != Uri.UriSchemeHttps || !Hosts.Contains(link.Uri.Host))
        {
            throw new JdkDownloadFailed($"The catalogue's link for {package.FileName} is on {link.Uri.Host}, which is not a host JDK vendors publish from. Nothing was downloaded.");
        }
        string staging = Path.Combine(Folder, ".stage-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await FetchAsync(link, part, package.Size, progress, cancel).ConfigureAwait(false);
            progress?.Report(new JdkDownloadProgress(package.Size, package.Size, "Unpacking"));
            Directory.CreateDirectory(staging);
            Unpack(part, package.ArchiveType, staging, cancel);
            (string top, string relative) = FindHome(staging)
                ?? throw new JdkDownloadFailed($"{package.FileName} holds no JDK: no folder in it has bin/java and a release file.");
            Directory.Move(top, target);
            string home = relative.Length == 0 ? target : Path.Combine(target, relative);
            Jdk jdk = registry.Add(home, origin: JdkOrigin.Downloaded);
            TryDelete(part);
            return jdk;
        }
        catch (Exception failed) when (failed is not JdkDownloadFailed and not OperationCanceledException)
        {
            throw new JdkDownloadFailed($"{package.FileName} could not be downloaded: {failed.Message}", failed);
        }
        finally
        {
            TryDeleteFolder(staging);
        }
    }

    private async Task FetchAsync(JdkPackageLink link, string part, long size, IProgress<JdkDownloadProgress>? progress, CancellationToken cancel)
    {
        using HttpResponseMessage response = await http.GetAsync(link.Uri, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new JdkDownloadFailed($"{link.Uri.Host} answered {(int)response.StatusCode} {response.ReasonPhrase} for the JDK archive.");
        }
        long total = response.Content.Headers.ContentLength ?? size;
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (Stream body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
        await using (FileStream file = new(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            byte[] buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                sha.AppendData(buffer, 0, read);
                received += read;
                progress?.Report(new JdkDownloadProgress(received, total, "Downloading"));
            }
            if (total > 0 && received < total)
            {
                throw new JdkDownloadFailed($"The download ended after {received} of {total} bytes. Try again.");
            }
        }
        string actual = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (link.Sha256 is { } expected && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(part);
            throw new JdkDownloadFailed($"The downloaded archive's SHA-256 is {actual}, and the catalogue says {expected}. It was deleted.");
        }
    }

    /// <summary>Removes what cut-off downloads left: <c>.part</c> files no download holds, and staging folders.</summary>
    public static void CleanUp(string stateFolder, string? jdksFolder = null)
    {
        foreach (string part in Files(Path.Combine(stateFolder, "downloads"), "*.part"))
        {
            string lockFile = Path.ChangeExtension(part, ".lock");
            try
            {
                using FileStream free = new(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                File.Delete(part);
            }
            catch (Exception held) when (held is IOException or UnauthorizedAccessException)
            {
                // Another window's download is still writing it.
            }
        }
        foreach (string staging in JdkScanner.Children(jdksFolder ?? DefaultFolder).Where(f => Path.GetFileName(f).StartsWith(".stage-", StringComparison.Ordinal)))
        {
            TryDeleteFolder(staging);
        }
    }

    /// <summary>Unpacks a zip or tar.gz archive into <paramref name="staging"/>, keeping Unix modes and links; refuses
    /// an entry or link that would land outside it.</summary>
    public static void Unpack(string archive, string type, string staging, CancellationToken cancel = default)
    {
        string root = Path.GetFullPath(staging);
        if (type == "zip")
        {
            using ZipArchive zip = ZipFile.OpenRead(archive);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                cancel.ThrowIfCancellationRequested();
                string path = Inside(root, entry.FullName);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(path);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: true);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, ModeOf(entry.FullName, entry.ExternalAttributes));
                }
            }
            return;
        }
        if (type is not ("tar.gz" or "tgz"))
        {
            throw new JdkDownloadFailed($"Wayfarer does not unpack {type} archives.");
        }
        using FileStream file = File.OpenRead(archive);
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using TarReader tar = new(gzip);
        List<(string Path, string Target)> links = [];
        while (tar.GetNextEntry() is { } entry)
        {
            cancel.ThrowIfCancellationRequested();
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
            {
                continue;
            }
            string path = Inside(root, entry.Name);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(path);
                    break;
                case TarEntryType.SymbolicLink:
                    string target = Path.IsPathRooted(entry.LinkName) ? entry.LinkName : Path.Combine(Path.GetDirectoryName(path)!, entry.LinkName);
                    Inside(root, Path.GetRelativePath(root, Path.GetFullPath(target)));
                    links.Add((path, entry.LinkName));
                    break;
                case TarEntryType.HardLink:
                    links.Add((path, Inside(root, entry.LinkName)));
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.ExtractToFile(path, overwrite: true);
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(path, entry.Mode | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                    break;
            }
        }
        foreach ((string path, string target) in links)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (Path.IsPathFullyQualified(target) && File.Exists(target))
            {
                File.Copy(target, path, overwrite: true);
            }
            else if (OperatingSystem.IsWindows())
            {
                // A link inside a JDK made for Windows is rare; copy what it points at where it is a file.
                string resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, target));
                if (File.Exists(resolved))
                {
                    File.Copy(resolved, path, overwrite: true);
                }
            }
            else
            {
                File.CreateSymbolicLink(path, target);
            }
        }
    }

    /// <summary>The Unix mode a zip entry gets: the one its external attributes carry; where they carry none, executable
    /// for <c>bin/*</c> and <c>lib/jspawnhelper</c> and read-write otherwise, or the JDK will not run.</summary>
    public static UnixFileMode ModeOf(string name, int externalAttributes)
    {
        UnixFileMode carried = (UnixFileMode)((externalAttributes >> 16) & 0xFFF);
        if (carried != UnixFileMode.None)
        {
            return carried | UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        const UnixFileMode plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        string normal = name.Replace('\\', '/');
        bool executable = normal.Contains("/bin/", StringComparison.Ordinal) || normal.StartsWith("bin/", StringComparison.Ordinal)
            || normal.EndsWith("/lib/jspawnhelper", StringComparison.Ordinal);
        return executable ? plain | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute : plain;
    }

    /// <summary>The folder directly under <paramref name="staging"/> to move into place, and the home's path inside it
    /// (empty when it is the home; <c>Contents/Home</c> for a macOS bundle); null when no folder holds a JDK.</summary>
    public static (string Top, string Relative)? FindHome(string staging)
    {
        foreach (string candidate in Folders(staging, 4))
        {
            if (ToolingJvms.At(candidate) is null)
            {
                continue;
            }
            string relative = Path.GetRelativePath(staging, candidate);
            if (relative == ".")
            {
                return null;
            }
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            return (Path.Combine(staging, parts[0]), string.Join(Path.DirectorySeparatorChar, parts.Skip(1)));
        }
        return null;
    }

    private static IEnumerable<string> Folders(string root, int depth)
    {
        Queue<(string Path, int Depth)> queue = new([(root, 0)]);
        while (queue.Count > 0)
        {
            (string folder, int level) = queue.Dequeue();
            yield return folder;
            if (level < depth)
            {
                foreach (string child in JdkScanner.Children(folder).Order(StringComparer.Ordinal))
                {
                    queue.Enqueue((child, level + 1));
                }
            }
        }
    }

    private static string Inside(string root, string name)
    {
        bool rooted = Path.IsPathRooted(name) || name.StartsWith('/') || name.StartsWith('\\');
        string full = rooted ? name : Path.GetFullPath(Path.Combine(root, name));
        if (rooted || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && full != root)
        {
            throw new JdkDownloadFailed($"The archive's entry {name} would land outside the folder it is unpacked into. Nothing was installed.");
        }
        return full;
    }

    private string? HomeIn(string target) =>
        Directory.Exists(target) ? Folders(target, 3).FirstOrDefault(folder => ToolingJvms.At(folder) is not null) : null;

    private void CheckSpace(long size)
    {
        if (size <= 0)
        {
            return;
        }
        try
        {
            DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(Folder))!);
            if (drive.AvailableFreeSpace < size * 3)
            {
                throw new JdkDownloadFailed($"{Folder} has {drive.AvailableFreeSpace / (1024 * 1024)} MB free; this JDK needs about {size * 3 / (1024 * 1024)} MB to download and unpack.");
            }
        }
        catch (Exception unknown) when (unknown is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // The space cannot be read here (a network share): the download goes ahead.
        }
    }

    private static async Task<FileStream> LockAsync(string file, CancellationToken cancel)
    {
        while (true)
        {
            try
            {
                return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                // Another window is downloading this JDK: wait for it.
                await Task.Delay(250, cancel).ConfigureAwait(false);
            }
        }
    }

    private static IEnumerable<string> Files(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : [];
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Safe(string id) => string.Concat(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception busy) when (busy is IOException or UnauthorizedAccessException)
        {
            // Removed on the next start.
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception busy) when (busy is IOException or UnauthorizedAccessException)
        {
            // Removed on the next start.
        }
    }
}
