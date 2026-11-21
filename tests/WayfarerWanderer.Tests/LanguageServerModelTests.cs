using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Passenger.Core;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>
/// The snapshot and both language servers agree on what a module sees: a dependency added to the build reaches the
/// snapshot, JDT LS's effective classpath and the Kotlin server's resolution, and leaves all three when removed. JDT LS
/// is compared on its own model (libraries, the modules on the classpath, source roots and language level); the Kotlin
/// server exposes no model query, so it is compared by what it resolves.
/// </summary>
[Collection(JvmTrustCollection.Name)]
public sealed class LanguageServerModelTests
{
    private const string Library = "commons-text-1.12.0.jar";
    private const string ProbeLine = "fun probeWords() = org.apache.commons.text.WordUtils.capitalize(\"a\")";
    private const string ProbePrefix = "fun probeWords() = org.apache.commons.text.";

    [Theory(SkipUnless = nameof(KotlinClassBridgeTests.RunLanguageAcceptance), SkipType = typeof(KotlinClassBridgeTests),
        Skip = "Set WAYFARER_LANGUAGE_ACCEPTANCE=1 and install the language servers to compare their model with the snapshot.")]
    // The dependency goes on the Kotlin module; service, a Java module, sees it through that module.
    [InlineData("maven", "java")]
    [InlineData("gradle", "java")]
    [InlineData("maven", "kotlin")]
    [InlineData("gradle", "kotlin")]
    public async Task ADependencyAddedAndRemovedReachesTheSnapshotAndTheServer(string fixture, string language)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        (string kotlinModule, string buildFile, string kotlinFile) = fixture == "maven"
            ? ("library", "library/pom.xml", "library/src/main/kotlin/example/Greeting.kt")
            : ("app", "app/build.gradle.kts", "app/src/main/kotlin/example/Main.kt");
        string root = WorkspaceImportTests.Copy(fixture);
        WorkspaceTrust.Trust(root);
        string build = Path.Combine(root, buildFile);
        string original = await File.ReadAllTextAsync(build, cancel);
        string kotlinSource = Path.Combine(root, kotlinFile);
        await File.AppendAllTextAsync(kotlinSource, Environment.NewLine + ProbeLine + Environment.NewLine, cancel);
        using WorkspaceImportSession session = new(new ProjectHostImporter());
        JvmLanguageClient? server = null;
        try
        {
            WorkspaceSnapshot before = (await session.RefreshAsync(root, cancel))!;
            Assert.DoesNotContain(before.Module("service")!.Classpath.Runtime, path => path.EndsWith(Library, StringComparison.Ordinal));
            server = await JvmLanguageClient.StartAsync(root, language, cancel, before);
            if (language == "java")
            {
                await server.WaitUntilReadyAsync(cancel);
                await AgreesAsync(server, before, "service", cancel);
            }
            else
            {
                await server.NotifyAsync("textDocument/didOpen", new
                {
                    textDocument = new { uri = new Uri(kotlinSource).AbsoluteUri, languageId = "kotlin", version = 1, text = await File.ReadAllTextAsync(kotlinSource, cancel) }
                }, cancel);
                await KotlinResolvesAsync(server, kotlinSource, expected: false, cancel);
            }

            await File.WriteAllTextAsync(build, AddDependency(fixture, original), cancel);
            WorkspaceSnapshot added = (await session.RefreshAsync(root, cancel))!;
            Assert.Contains(added.Module(Id(added, kotlinModule))!.Classpath.Compile, path => path.EndsWith(Library, StringComparison.Ordinal));
            Assert.Contains(added.Module("service")!.Classpath.Runtime, path => path.EndsWith(Library, StringComparison.Ordinal));
            await server.ReloadModelAsync(added, cancel);
            await (language == "java" ? AgreesAsync(server, added, "service", cancel) : KotlinResolvesAsync(server, kotlinSource, expected: true, cancel));

            await File.WriteAllTextAsync(build, original, cancel);
            WorkspaceSnapshot removed = (await session.RefreshAsync(root, cancel))!;
            Assert.DoesNotContain(removed.Module("service")!.Classpath.Runtime, path => path.EndsWith(Library, StringComparison.Ordinal));
            await server.ReloadModelAsync(removed, cancel);
            await (language == "java" ? AgreesAsync(server, removed, "service", cancel) : KotlinResolvesAsync(server, kotlinSource, expected: false, cancel));
        }
        finally
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
        }
    }

    private static string Id(WorkspaceSnapshot snapshot, string name) => snapshot.Modules.Single(module => module.Name == name).Id;

    private static string AddDependency(string fixture, string text) => fixture == "maven"
        ? text.Replace("<dependencies>",
            "<dependencies><dependency><groupId>org.apache.commons</groupId><artifactId>commons-text</artifactId><version>1.12.0</version></dependency>",
            StringComparison.Ordinal)
        : text.Replace("dependencies { implementation(project(\":library\")) }",
            "dependencies { implementation(project(\":library\")); implementation(\"org.apache.commons:commons-text:1.12.0\") }",
            StringComparison.Ordinal);

    /// <summary>Waits for JDT LS to settle on the snapshot's model of <paramref name="module"/>, failing with both sides.</summary>
    private static async Task AgreesAsync(JvmLanguageClient java, WorkspaceSnapshot snapshot, string module, CancellationToken cancel)
    {
        JvmModule expected = snapshot.Module(module)!;
        ModuleView wanted = FromSnapshot(snapshot, expected);
        ModuleView seen = await FromJdtAsync(java, snapshot, expected, cancel);
        Stopwatch clock = Stopwatch.StartNew();
        while (!wanted.Equals(seen) && clock.Elapsed < TimeSpan.FromMinutes(3))
        {
            await Task.Delay(1000, cancel);
            seen = await FromJdtAsync(java, snapshot, expected, cancel);
        }
        Assert.True(wanted.Equals(seen), $"JDT LS and the snapshot differ on {module}:\n{wanted.Difference(seen)}");
    }

    /// <summary>JDT LS keeps one classpath per project for compiling and running alike, so it is compared with the union
    /// of the snapshot's compile and runtime classpaths: Gradle's compile-only API jars (annotations a library compiles
    /// against) are on the first and rightly not the second.</summary>
    private static ModuleView FromSnapshot(WorkspaceSnapshot snapshot, JvmModule module) => new(
        Jars([.. module.Classpath.Compile, .. module.Classpath.Runtime]),
        Jars([.. module.Classpath.Compile, .. module.Classpath.Runtime, .. module.Classpath.Test,
            .. module.SourceSet("test") is { } test ? test.CompileClasspath.Select(entry => entry.Path) : []]),
        Modules(snapshot.Root, module, module.Classpath.Runtime),
        [.. module.SourceRoots.Select(root => root.Path).Where(Directory.Exists).Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase)],
        module.CompileToolchain.LanguageVersion);

    private static async Task<ModuleView> FromJdtAsync(JvmLanguageClient java, WorkspaceSnapshot snapshot, JvmModule module, CancellationToken cancel)
    {
        string uri = new Uri(module.Root).AbsoluteUri;
        List<string> runtime = await ClasspathAsync(java, uri, "runtime", cancel);
        List<string> test = await ClasspathAsync(java, uri, "test", cancel);
        JsonElement sources = await java.RequestAsync("workspace/executeCommand",
            new { command = "java.project.listSourcePaths", arguments = Array.Empty<object>() }, cancel);
        List<string> roots = [.. sources.GetProperty("data").EnumerateArray()
            .Where(entry => entry.GetProperty("projectName").GetString() == module.Name)
            .Select(entry => entry.GetProperty("path").GetString()!)
            .Where(Directory.Exists).Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase)];
        JsonElement settings = await java.RequestAsync("workspace/executeCommand", new
        {
            command = "java.project.getSettings",
            arguments = new object[] { uri, new[] { "org.eclipse.jdt.core.compiler.compliance" } },
        }, cancel);
        int? level = settings.TryGetProperty("org.eclipse.jdt.core.compiler.compliance", out JsonElement compliance)
            && int.TryParse(compliance.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : null;
        return new(Jars(runtime), Jars(test), Modules(snapshot.Root, module, runtime), [.. roots], level);
    }

    private static async Task<List<string>> ClasspathAsync(JvmLanguageClient java, string uri, string scope, CancellationToken cancel)
    {
        try
        {
            JsonElement result = await java.RequestAsync("workspace/executeCommand", new
            {
                command = "java.project.getClasspaths",
                arguments = new object[] { uri, JsonSerializer.Serialize(new { scope }) },
            }, cancel);
            return [.. result.GetProperty("classpaths").EnumerateArray().Select(entry => entry.GetString()!)];
        }
        catch (InvalidOperationException)
        {
            // The project is being reimported and has no classpath for the moment.
            return [];
        }
    }

    /// <summary>The library jars, as full paths: the same file wherever it sits in the order.</summary>
    private static string[] Jars(IEnumerable<string> entries) =>
        [.. entries.Where(path => path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The other modules whose output is on the classpath, by folder: JDT LS writes them as its own output
    /// folders (<c>bin/main</c> under Buildship, or project-relative paths), the snapshot as the build's.</summary>
    private static string[] Modules(string root, JvmModule module, IEnumerable<string> entries) =>
        [.. entries.Where(path => !path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            .Select(path => path.StartsWith('\\') || path.StartsWith('/') && !Path.IsPathFullyQualified(path)
                ? path.TrimStart('\\', '/').Split('\\', '/')[0]
                : PathNames.IsUnder(Path.GetFullPath(path), root) ? Path.GetRelativePath(root, path).Split('\\', '/')[0] : null)
            .OfType<string>()
            .Where(name => name != Path.GetFileName(module.Root) && name != "..")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static async Task KotlinResolvesAsync(JvmLanguageClient kotlin, string source, bool expected, CancellationToken cancel)
    {
        string[] lines = await File.ReadAllLinesAsync(source, cancel);
        int line = Array.FindIndex(lines, text => text.StartsWith(ProbeLine, StringComparison.Ordinal));
        var request = new { textDocument = new { uri = new Uri(source).AbsoluteUri }, position = new { line, character = ProbePrefix.Length } };
        Stopwatch clock = Stopwatch.StartNew();
        int agreeing = 0;
        while (clock.Elapsed < TimeSpan.FromMinutes(5))
        {
            bool resolves = JvmMeasurementTests.Labels(await kotlin.RequestAsync("textDocument/completion", request, cancel))
                .Any(label => label.StartsWith("WordUtils", StringComparison.Ordinal));
            // Absence is only believed once it holds for a few seconds after the reload.
            agreeing = resolves == expected ? agreeing + 1 : 0;
            if (agreeing >= (expected ? 1 : 5))
            {
                return;
            }
            await Task.Delay(1000, cancel);
        }
        Assert.Fail(expected ? "The Kotlin server never resolved org.apache.commons.text.WordUtils after the dependency was added."
            : "The Kotlin server still resolved org.apache.commons.text.WordUtils after the dependency was removed.");
    }

    private sealed record ModuleView(string[] Runtime, string[] Test, string[] Modules, string[] Roots, int? Level)
    {
        public bool Equals(ModuleView? other) => other is not null && Runtime.SequenceEqual(other.Runtime, StringComparer.OrdinalIgnoreCase)
            && Test.SequenceEqual(other.Test, StringComparer.OrdinalIgnoreCase) && Modules.SequenceEqual(other.Modules)
            && Roots.SequenceEqual(other.Roots, StringComparer.OrdinalIgnoreCase) && Level == other.Level;

        public override int GetHashCode() => Level.GetHashCode();

        public string Difference(ModuleView seen) => string.Join("\n",
            Side("runtime jars", Runtime, seen.Runtime), Side("test jars", Test, seen.Test), Side("modules", Modules, seen.Modules),
            Side("source roots", Roots, seen.Roots), $"language level: snapshot {Level}, JDT LS {seen.Level}");

        private static string Side(string what, string[] snapshot, string[] jdt) =>
            $"{what}: only in the snapshot [{string.Join(", ", snapshot.Except(jdt, StringComparer.OrdinalIgnoreCase).Select(Path.GetFileName))}], " +
            $"only in JDT LS [{string.Join(", ", jdt.Except(snapshot, StringComparer.OrdinalIgnoreCase).Select(Path.GetFileName))}]";
    }
}
