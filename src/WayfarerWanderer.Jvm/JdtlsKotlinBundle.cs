using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

/// <summary>Builds the small JDT LS extension in <c>Jdtls/</c> against the user's installed server, so no Eclipse
/// binary is redistributed. The jar is cached per server build and source revision.</summary>
internal static class JdtlsKotlinBundle
{
    private static readonly SemaphoreSlim Gate = new(1);

    public static string Cache =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wayfarer", "jdtls-bundles");

    public static async Task<string> EnsureAsync(string jdtHome, CancellationToken cancel)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "Jdtls");
        string plugins = Path.Combine(jdtHome, "plugins");
        string core = Directory.GetFiles(plugins, "org.eclipse.jdt.ls.core_*.jar").Single();
        string[] inputs = [.. Directory.GetFiles(source).Order(StringComparer.Ordinal)];
        StringBuilder identity = new(Path.GetFileName(core));
        foreach (string input in inputs) identity.Append('\0').Append(await File.ReadAllTextAsync(input, cancel).ConfigureAwait(false));
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())))[..16];
        string jar = Path.Combine(Cache, key, "wayfarer-kotlin.jar");
        if (File.Exists(jar)) return jar;

        await Gate.WaitAsync(cancel).ConfigureAwait(false);
        string work = Directory.CreateTempSubdirectory("wayfarer-jdtls-").FullName;
        try
        {
            if (File.Exists(jar)) return jar;
            string classes = Path.Combine(work, "classes");
            string javac = Javac();
            ProcessResult compiled = await new ChildProcessRunner().RunAsync(ChildProcesses.Command(javac,
                ["--release", "21", "-nowarn", "-d", classes, "-cp", Path.Combine(plugins, "*"),
                 .. inputs.Where(path => path.EndsWith(".java", StringComparison.Ordinal))], work), cancellationToken: cancel).ConfigureAwait(false);
            if (compiled.ExitCode != 0)
                throw new InvalidOperationException("Could not build the JDT LS Kotlin class extension: " +
                    string.Join(Environment.NewLine, compiled.Output.TakeLast(8).Select(line => line.Line)));

            string staged = Path.Combine(work, "wayfarer-kotlin.jar");
            using (ZipArchive archive = ZipFile.Open(staged, ZipArchiveMode.Create))
            {
                // The manifest leads the archive, as the OSGi framework expects.
                archive.CreateEntryFromFile(Path.Combine(source, "MANIFEST.MF"), "META-INF/MANIFEST.MF");
                archive.CreateEntryFromFile(Path.Combine(source, "plugin.xml"), "plugin.xml");
                foreach (string file in Directory.EnumerateFiles(classes, "*.class", SearchOption.AllDirectories))
                    archive.CreateEntryFromFile(file, Path.GetRelativePath(classes, file).Replace('\\', '/'));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(jar)!);
            File.Move(staged, jar, overwrite: true);
            return jar;
        }
        finally
        {
            Gate.Release();
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    private static string Javac()
    {
        string name = OperatingSystem.IsWindows() ? "javac.exe" : "javac";
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home && File.Exists(Path.Combine(home, "bin", name)))
            return Path.Combine(home, "bin", name);
        return name;
    }
}
