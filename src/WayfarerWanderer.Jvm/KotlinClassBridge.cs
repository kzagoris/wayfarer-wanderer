using System.Text;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

/// <summary>
/// Maven modules reach JDT LS through m2e, which compiles no Kotlin, so Java cannot see Kotlin declarations.
/// Gradle needs nothing: Buildship already puts Kotlin's class output on the classpath.
/// The bridge compiles the Kotlin modules with the project's wrapper, copies only the Kotlin classes into a
/// folder it owns, and attaches that folder to each module through the JDT LS extension. Each refresh uses a
/// new folder, so JDT LS reindexes it. The project tree gains nothing beyond the wrapper's normal output.
/// </summary>
public sealed class KotlinClassBridge : IDisposable
{
    public const string Command = "wayfarer.kotlinClassFolders";

    private static readonly byte[] KotlinMetadata = Encoding.ASCII.GetBytes("Lkotlin/Metadata;");
    private readonly JvmBuild build;
    private readonly string state = Directory.CreateTempSubdirectory("wayfarer-kotlin-classes-").FullName;
    private readonly SemaphoreSlim gate = new(1);
    private long generation;

    public KotlinClassBridge(JvmBuild? build = null) => this.build = build ?? new JvmBuild();

    public static bool Applies(WorkspaceSnapshot snapshot) =>
        snapshot.BuildSystem == JvmBuildSystem.Maven && KotlinModules(snapshot).Length > 0;

    private static JvmModule[] KotlinModules(WorkspaceSnapshot snapshot) =>
        [.. snapshot.Modules.Where(module => module.SourceRoots.Any(root => root.Kind == JvmSourceKind.MainKotlin))];

    /// <summary>Compiles the Kotlin modules and attaches their classes to JDT LS. Returns the attached module roots.</summary>
    public async Task<IReadOnlyList<string>> RefreshAsync(JvmLanguageClient java, WorkspaceSnapshot snapshot,
        Action<ProcessOutput>? output = null, CancellationToken cancel = default)
    {
        if (!Applies(snapshot)) return [];
        if (java.BundleProblem is { } problem) throw new InvalidOperationException(problem);
        await gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            JvmModule[] modules = KotlinModules(snapshot);
            ProcessResult result = await build.RunAsync(snapshot,
                new(snapshot.Root, string.Join(',', modules.Select(module => module.Id)), "compile", []), output, cancel).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException("Kotlin classes for Java were not refreshed; the wrapper build failed." + Environment.NewLine +
                    string.Join(Environment.NewLine, result.Output.TakeLast(12).Select(line => line.Line)));

            long next = Interlocked.Increment(ref generation);
            string folder = Path.Combine(state, next.ToString(System.Globalization.CultureInfo.InvariantCulture));
            List<string> arguments = [];
            for (int index = 0; index < modules.Length; index++)
            {
                // Maven's default output directory; the copy keeps only classes the Kotlin compiler wrote.
                string classes = Path.Combine(modules[index].Root, "target", "classes");
                string copy = Path.Combine(folder, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(copy);
                if (Directory.Exists(classes))
                {
                    foreach (string file in Directory.EnumerateFiles(classes, "*.class", SearchOption.AllDirectories))
                    {
                        if (!IsKotlin(await File.ReadAllBytesAsync(file, cancel).ConfigureAwait(false))) continue;
                        string target = Path.Combine(copy, Path.GetRelativePath(classes, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target);
                    }
                }
                arguments.Add(Path.GetFullPath(modules[index].Root));
                arguments.Add(copy);
            }

            // JDT LS may still be importing; wait until every module project exists.
            await java.WaitUntilReadyAsync(cancel).ConfigureAwait(false);
            IReadOnlyList<string> attached = [];
            for (int attempt = 0; attempt < 30; attempt++)
            {
                var reply = await java.RequestAsync("workspace/executeCommand", new { command = Command, arguments }, cancel).ConfigureAwait(false);
                attached = [.. reply.EnumerateArray().Select(root => root.GetString()!)];
                if (attached.Count == modules.Length) break;
                await Task.Delay(TimeSpan.FromSeconds(1), cancel).ConfigureAwait(false);
            }
            if (attached.Count != modules.Length)
                throw new InvalidOperationException($"JDT LS imported {attached.Count} of {modules.Length} Kotlin modules; Java may not see Kotlin declarations.");

            foreach (string old in Directory.EnumerateDirectories(state).Where(path => path != folder))
                try { Directory.Delete(old, true); } catch (IOException) { }
            return attached;
        }
        finally { gate.Release(); }
    }

    internal static bool IsKotlin(ReadOnlySpan<byte> classFile) => classFile.IndexOf(KotlinMetadata) >= 0;

    // The gate is left to the collector: a refresh that is being cancelled may still release it.
    public void Dispose()
    {
        try { Directory.Delete(state, true); } catch (IOException) { }
    }
}
