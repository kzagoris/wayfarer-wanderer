using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Passenger.Core;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>Records milestone 2 budgets. The numbers go to artifacts/test-results; the test only fails when a measurement cannot be taken.</summary>
[Collection(JvmTrustCollection.Name)]
public sealed class JvmMeasurementTests
{
    public static bool RunMeasurements => Environment.GetEnvironmentVariable("WAYFARER_MEASURE") == "1";

    private static readonly string[] ServerProcessNames = ["java", "javaw", "intellij-server"];

    [Theory(SkipUnless = nameof(RunMeasurements), Skip = "Set WAYFARER_MEASURE=1 and install both language servers to record import, completion and memory.")]
    // Each request completes a member declared in the other language: Java from Kotlin, Kotlin from Java.
    [InlineData("gradle", "service/src/main/java/example/Service.java", 17, 24, "message", "app/src/main/kotlin/example/Main.kt", 3, 21, "message")]
    [InlineData("maven", "service/src/main/java/example/Service.java", 17, 26, "message", "library/src/main/kotlin/example/Greeting.kt", 2, 35, "value")]
    public async Task MeasureImportCompletionAndMemory(string buildTool, string javaFile, int javaLine, int javaColumn, string javaExpected,
        string kotlinFile, int kotlinLine, int kotlinColumn, string kotlinExpected)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string root = Directory.CreateTempSubdirectory("wayfarer measure ").FullName;
        try
        {
            JvmFixtures.Copy(buildTool, root);
            WorkspaceTrust.Trust(root);
            var importer = new ProjectHostImporter();
            var clock = Stopwatch.StartNew();
            WorkspaceSnapshot snapshot = await importer.ImportAsync(root, 1, cancel);
            TimeSpan cold = clock.Elapsed;
            clock.Restart();
            await importer.ImportAsync(root, 2, cancel);
            TimeSpan warm = clock.Elapsed;

            HashSet<int> existing = [.. Process.GetProcesses().Select(p => p.Id)];
            using var sampling = new CancellationTokenSource();
            var memory = SampleAsync(existing, sampling.Token);
            Dictionary<string, object?> completion = [];
            List<string> failures = [];
            List<JvmLanguageClient> clients = [];
            using var kotlinClasses = new KotlinClassBridge();
            long? kotlinClassesMs = null;
            try
            {
                // Both servers run together, so the memory peak covers the pair.
                foreach (var (language, file, line, column, expected) in new[]
                {
                    ("java", javaFile, javaLine, javaColumn, javaExpected),
                    ("kotlin", kotlinFile, kotlinLine, kotlinColumn, kotlinExpected),
                })
                {
                    try
                    {
                        JvmLanguageClient client = await JvmLanguageClient.StartAsync(root, language, cancel);
                        clients.Add(client);
                        if (language == "java" && KotlinClassBridge.Applies(snapshot))
                        {
                            // As in the product: Maven needs Kotlin's classes before Java can see Kotlin declarations.
                            var refresh = Stopwatch.StartNew();
                            await kotlinClasses.RefreshAsync(client, snapshot, cancel: cancel);
                            kotlinClassesMs = (long)refresh.Elapsed.TotalMilliseconds;
                        }
                        completion[language] = await CompleteAsync(client, language, Path.Combine(root, file), line, column, expected, cancel);
                    }
                    catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException)
                    {
                        completion[language] = new { error = error.Message };
                        failures.Add($"{language}: {error.Message}");
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancel);
            }
            finally
            {
                foreach (var client in clients) await client.DisposeAsync();
            }
            await sampling.CancelAsync();
            (long peak, string[] processes) = await memory;

            var record = new Dictionary<string, object?>
            {
                ["buildTool"] = buildTool,
                ["recordedAt"] = DateTimeOffset.Now.ToString("O"),
                ["os"] = RuntimeInformation.OSDescription,
                ["rid"] = RuntimeInformation.RuntimeIdentifier,
                ["processors"] = Environment.ProcessorCount,
                // The static import reads build files only, so it leaves JDK homes unresolved; record the JDK the tools ran on.
                ["environmentJdk"] = EnvironmentJdk(),
                ["toolingJdk"] = snapshot.ToolingToolchain,
                ["moduleJdks"] = snapshot.Modules.Select(m => new { m.Name, Runtime = m.RuntimeToolchain }).ToArray(),
                ["languageServers"] = ServerVersions(),
                ["cacheState"] = "Fresh project copy; user Gradle/Maven caches and any running Gradle daemon kept warm.",
                ["coldImportMs"] = (long)cold.TotalMilliseconds,
                ["warmReopenMs"] = (long)warm.TotalMilliseconds,
                ["kotlinClassesForJavaMs"] = kotlinClassesMs,
                ["completion"] = completion,
                ["peakServerWorkingSetMiB"] = peak / (1024 * 1024),
                ["serverProcesses"] = processes,
            };
            string results = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/test-results"));
            Directory.CreateDirectory(results);
            string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(results, $"jvm-measurements-{buildTool}.json"), json, cancel);
            TestContext.Current.SendDiagnosticMessage(json);
            Assert.True(peak > 0, "No language-server process was sampled.");
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            WorkspaceTrust.TakeBack(root);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The project host's own costs: starting it, a cold import (on Gradle with a daemon of its own, so it starts cold
    /// without stopping anyone's daemon), a warm import on the daemon it left, a warm reopen from the kept model, and the
    /// host's peak working set. Writes <c>artifacts/test-results/jvm-host-measurements-&lt;tool&gt;.json</c>.
    /// </summary>
    [Theory(SkipUnless = nameof(RunMeasurements), Skip = "Set WAYFARER_MEASURE=1 to record the project host's import times and memory.")]
    [InlineData("gradle")]
    [InlineData("maven")]
    public async Task MeasureHostImportAndMemory(string buildTool)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string root = Directory.CreateTempSubdirectory("wayfarer measure host ").FullName;
        string cache = Path.Combine(Directory.CreateTempSubdirectory("wayfarer measure state ").FullName, WorkspaceModelCache.FileName);
        try
        {
            JvmFixtures.Copy(buildTool, root);
            if (buildTool == "gradle")
            {
                // A JVM argument no running daemon has makes Gradle start one of its own; it exits a minute after.
                await File.AppendAllTextAsync(Path.Combine(root, "gradle.properties"),
                    $"\norg.gradle.jvmargs=-Dwayfarer.measure={Guid.NewGuid():N}\norg.gradle.daemon.idletimeout=60000\n", cancel);
            }
            WorkspaceTrust.Trust(root);
            foreach (JvmProjectHost running in ProjectHosts.Current)
            {
                foreach (string folder in running.ServedFolders) await ProjectHosts.ReleaseAsync(folder);
                running.End("a measurement wants a host of its own");
            }

            Stopwatch clock = Stopwatch.StartNew();
            JvmProjectHost host = await ProjectHosts.ForAsync(root, cancel);
            TimeSpan hostStart = clock.Elapsed;
            using CancellationTokenSource sampling = new();
            Task<long> memory = SampleHostAsync(host.ProcessId, sampling.Token);

            ProjectHostImporter importer = new() { CacheFile = cache };
            clock.Restart();
            WorkspaceSnapshot snapshot = await importer.ImportAsync(root, 1, cancel);
            TimeSpan coldImport = clock.Elapsed;
            clock.Restart();
            await importer.ImportAsync(root, 2, cancel);
            TimeSpan warmImport = clock.Elapsed;
            await sampling.CancelAsync();
            long peak = await memory;

            // A new host (a new Tooling API connection and Maven class loader) on the daemon the first one left warm.
            await ProjectHosts.ReleaseAsync(root);
            host.End("the measurement starts a new host on the warm daemon");
            host = await ProjectHosts.ForAsync(root, cancel);
            clock.Restart();
            await new ProjectHostImporter().ImportAsync(root, 3, cancel);
            TimeSpan newHostWarmDaemon = clock.Elapsed;

            // A reopen: a new session finds the kept model and every input it was read from unchanged.
            using WorkspaceImportSession reopened = new(new ProjectHostImporter { CacheFile = cache }, cache);
            clock.Restart();
            WorkspaceSnapshot? kept = await reopened.OpenAsync(root, cancel);
            TimeSpan warmReopen = clock.Elapsed;
            Assert.NotNull(kept);
            Assert.False(reopened.State.IsStale);

            var record = new Dictionary<string, object?>
            {
                ["buildTool"] = buildTool,
                ["recordedAt"] = DateTimeOffset.Now.ToString("O"),
                ["os"] = RuntimeInformation.OSDescription,
                ["rid"] = RuntimeInformation.RuntimeIdentifier,
                ["processors"] = Environment.ProcessorCount,
                ["toolingJvm"] = new { host.Jvm.Home, host.Jvm.Version },
                ["importJdk"] = snapshot.Modules.FirstOrDefault()?.ImportToolchain,
                ["modules"] = snapshot.Modules.Count,
                ["cacheState"] = buildTool == "gradle"
                    ? "Fresh project copy; user Gradle caches warm; the first import starts a Gradle daemon of its own, which the later imports reuse."
                    : "Fresh project copy; user Maven repository and wrapper distribution warm.",
                ["hostStartMs"] = (long)hostStart.TotalMilliseconds,
                ["coldImportMs"] = (long)coldImport.TotalMilliseconds,
                ["warmImportMs"] = (long)warmImport.TotalMilliseconds,
                ["newHostWarmDaemonImportMs"] = (long)newHostWarmDaemon.TotalMilliseconds,
                ["warmReopenMs"] = (long)warmReopen.TotalMilliseconds,
                ["peakHostWorkingSetMiB"] = peak / (1024 * 1024),
            };
            string results = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/test-results"));
            Directory.CreateDirectory(results);
            string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(results, $"jvm-host-measurements-{buildTool}.json"), json, cancel);
            TestContext.Current.SendDiagnosticMessage(json);
            Assert.True(peak > 0, "The host process was never sampled.");
        }
        finally
        {
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(root);
            Directory.Delete(root, recursive: true);
            Directory.Delete(Path.GetDirectoryName(cache)!, recursive: true);
        }
    }

    private static async Task<long> SampleHostAsync(int processId, CancellationToken stop)
    {
        long peak = 0;
        while (true)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
            }
            catch (ArgumentException) { return peak; }
            catch (InvalidOperationException) { return peak; }
            try { await Task.Delay(100, stop); }
            catch (OperationCanceledException) { return peak; }
        }
    }

    internal static async Task<object> CompleteAsync(JvmLanguageClient client, string language, string path,
        int line, int column, string expected, CancellationToken cancel)
    {
        string uri = new Uri(path).AbsoluteUri;
        await client.NotifyAsync("textDocument/didOpen",
            new { textDocument = new { uri, languageId = language, version = 1, text = await File.ReadAllTextAsync(path, cancel) } }, cancel);
        var request = new { textDocument = new { uri }, position = new { line, character = column } };
        var clock = Stopwatch.StartNew();
        int attempts = 0;
        while (true)
        {
            attempts++;
            if (Labels(await client.RequestAsync("textDocument/completion", request, cancel)).Any(l => l.StartsWith(expected, StringComparison.Ordinal)))
                break;
            if (clock.Elapsed > TimeSpan.FromMinutes(5)) throw new TimeoutException($"{language} never completed '{expected}' in {path}.");
            await Task.Delay(500, cancel);
        }
        TimeSpan first = clock.Elapsed;
        List<long> warm = [];
        for (int i = 0; i < 5; i++)
        {
            clock.Restart();
            await client.RequestAsync("textDocument/completion", request, cancel);
            warm.Add((long)clock.Elapsed.TotalMilliseconds);
        }
        warm.Sort();
        return new { firstUsefulMs = (long)first.TotalMilliseconds, attempts, warmMedianMs = warm[warm.Count / 2], warmMs = warm };
    }

    internal static IEnumerable<string> Labels(JsonElement result)
    {
        JsonElement items = result.ValueKind == JsonValueKind.Object ? result.GetProperty("items") : result;
        return items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(item => item.GetProperty("label").GetString() ?? "")
            : [];
    }

    private static async Task<(long Peak, string[] Processes)> SampleAsync(HashSet<int> existing, CancellationToken stop)
    {
        long peak = 0;
        HashSet<string> seen = [];
        while (true)
        {
            long total = 0;
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    if (existing.Contains(process.Id) || !ServerProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) continue;
                    try
                    {
                        total += process.WorkingSet64;
                        seen.Add($"{process.ProcessName} {process.Id}");
                    }
                    catch (InvalidOperationException) { }
                }
            }
            peak = Math.Max(peak, total);
            try { await Task.Delay(250, stop); }
            catch (OperationCanceledException) { return (peak, [.. seen]); }
        }
    }

    private static object EnvironmentJdk()
    {
        string? home = Environment.GetEnvironmentVariable("JAVA_HOME");
        string java = string.IsNullOrEmpty(home) ? "java" : Path.Combine(home, "bin", "java");
        try
        {
            using Process process = Process.Start(new ProcessStartInfo(java, "-version") { RedirectStandardError = true, RedirectStandardOutput = true })!;
            string version = process.StandardError.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
            process.WaitForExit();
            return new { javaHome = home, version };
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            return new { javaHome = home, version = $"unavailable: {error.Message}" };
        }
    }

    private static string[] ServerVersions()
    {
        string extensions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        string?[] configured = [Environment.GetEnvironmentVariable("WAYFARER_JDTLS_HOME"), Environment.GetEnvironmentVariable("WAYFARER_KOTLIN_LSP_HOME")];
        IEnumerable<string> installed = Directory.Exists(extensions)
            ? Directory.GetDirectories(extensions).Select(Path.GetFileName).OfType<string>()
                .Where(name => name.StartsWith("redhat.java-", StringComparison.Ordinal) || name.StartsWith("jetbrains.kotlin-server-", StringComparison.Ordinal))
            : [];
        string? managed = KotlinServerInstalls.Newest(KotlinServerInstalls.Folder);
        return [.. configured.OfType<string>(), .. managed is null ? [] : new[] { managed }, .. installed];
    }
}
