using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>The JVM project host's protocol, proved against the staged host before any evaluator: its first line,
/// partial answers and a result, an error, cancel, a crash mid-request and a hang, and the golden examples.</summary>
public sealed class ProjectHostTests
{
    private static readonly ProjectHostOptions Testing = new()
    {
        PingInterval = TimeSpan.FromMilliseconds(200),
        MissedPings = 3,
        Environment = new Dictionary<string, string> { ["WAYFARER_HOST_TEST"] = "1" },
    };

    [Fact]
    public async Task FirstLineNamesTheProtocolAndTheToolingJvm()
    {
        ToolingJvm jvm = ToolingJvms.Find();
        await using JvmProjectHost host = await JvmProjectHost.StartAsync(jvm, Testing, TestContext.Current.CancellationToken);
        Assert.Equal(HostProtocol.Version, host.Ready.Protocol);
        Assert.True(PathNames.Same(Path.GetFullPath(jvm.Home), Path.GetFullPath(host.Ready.Java.Home)), $"{jvm.Home} vs {host.Ready.Java.Home}");
        Assert.Equal(jvm.Feature, host.Ready.Java.Feature);
        Assert.Equal(host.ProcessId, host.Ready.Pid);
    }

    [Fact]
    public async Task PartialAnswersArriveBeforeTheResult()
    {
        await using JvmProjectHost host = await StartAsync();
        List<int> partials = [];
        JsonElement result = await host.RequestAsync("test.partials", new { count = 3 }, partial => partials.Add(partial.GetInt32()), TestContext.Current.CancellationToken);
        Assert.Equal([0, 1, 2], partials);
        Assert.Equal(3, result.GetInt32());
    }

    [Fact]
    public async Task AnErrorFailsTheRequestAndTheHostGoesOn()
    {
        await using JvmProjectHost host = await StartAsync();
        ProjectHostFailed failed = await Assert.ThrowsAsync<ProjectHostFailed>(
            () => host.RequestAsync("test.fail", new { reason = "no build here" }, cancel: TestContext.Current.CancellationToken));
        Assert.Equal("no build here", failed.Reason);
        Assert.False(failed.HostEnded);
        JsonElement after = await host.RequestAsync("test.partials", new { count = 0 }, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(0, after.GetInt32());
    }

    [Fact]
    public async Task CancelEndsTheRequestInTheHost()
    {
        await using JvmProjectHost host = await StartAsync();
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JsonElement> request = host.RequestAsync("test.wait", null, _ => waiting.TrySetResult(), cancel.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        // The host is still there, and answers the next request.
        JsonElement after = await host.RequestAsync("test.partials", new { count = 1 }, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(1, after.GetInt32());
        Assert.False(host.HasExited);
    }

    [Fact]
    public async Task ACrashMidRequestFailsItWithTheHostsLastWords()
    {
        await using JvmProjectHost host = await StartAsync();
        ProjectHostFailed failed = await Assert.ThrowsAsync<ProjectHostFailed>(
            () => host.RequestAsync("test.crash", null, cancel: TestContext.Current.CancellationToken));
        Assert.True(failed.HostEnded);
        Assert.Contains("wayfarer host test crash", failed.Reason, StringComparison.Ordinal);
        Assert.True(host.HasExited);
        await Assert.ThrowsAsync<ProjectHostFailed>(() => host.RequestAsync("test.partials", new { count = 1 }, cancel: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AHostThatStopsAnsweringIsEnded()
    {
        await using JvmProjectHost host = await StartAsync();
        Stopwatch waited = Stopwatch.StartNew();
        ProjectHostFailed failed = await Assert.ThrowsAsync<ProjectHostFailed>(
            () => host.RequestAsync("test.hang", null, cancel: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.True(failed.HostEnded);
        Assert.Contains("stopped answering", failed.Reason, StringComparison.Ordinal);
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), $"took {waited.Elapsed}");
        await WaitUntilAsync(() => host.HasExited);
    }

    [Fact]
    public async Task AHostWithNothingToDoIsNotEndedForSilence()
    {
        await using JvmProjectHost host = await StartAsync();
        await Task.Delay(Testing.PingInterval * (Testing.MissedPings + 3), TestContext.Current.CancellationToken);
        Assert.False(host.HasExited);
        JsonElement after = await host.RequestAsync("test.partials", new { count = 0 }, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(0, after.GetInt32());
    }

    [Fact]
    public async Task StartsFromAnotherWorkingDirectory()
    {
        string elsewhere = Directory.CreateTempSubdirectory("wayfarer-host-cwd-").FullName;
        string before = Environment.CurrentDirectory;
        Environment.CurrentDirectory = elsewhere;
        try
        {
            string? jar = JvmProjectHost.Locate();
            Assert.NotNull(jar);
            Assert.True(File.Exists(jar));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(jar)!, "lib", "gradle-tooling-api-9.7.1.jar")));
            await using JvmProjectHost host = await StartAsync();
            Assert.Equal(HostProtocol.Version, host.Ready.Protocol);
        }
        finally
        {
            Environment.CurrentDirectory = before;
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public async Task AJvmTooOldIsRefusedWithItsReason()
    {
        ToolingJvm? old = ToolingJvms.All().FirstOrDefault(jvm => jvm.Feature < ToolingJvms.Minimum);
        Assert.SkipWhen(old is null, "no JVM older than Java 21 is installed to refuse");
        ProjectHostFailed failed = await Assert.ThrowsAsync<ProjectHostFailed>(
            () => JvmProjectHost.StartAsync(old!, Testing, TestContext.Current.CancellationToken));
        Assert.Contains($"is Java {old!.Feature}", failed.Reason, StringComparison.Ordinal);
        Assert.Contains("needs Java 21 or later", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentToolingJvmSaysWhatWasFoundAndWhatToDo()
    {
        ToolingJvmMissingException missing = Assert.Throws<ToolingJvmMissingException>(() => ToolingJvms.Find(minimum: 999));
        Assert.Contains("needs Java 999 or later", missing.Message, StringComparison.Ordinal);
        Assert.Contains("Settings", missing.Message, StringComparison.Ordinal);
        Assert.NotEmpty(missing.Found);
    }

    [Fact]
    public void GoldenExamplesReadIntoTheClientRecords()
    {
        HostReady ready = Value("ready.json", "result").Deserialize<HostReady>(HostProtocol.Json)!;
        Assert.Equal(1, ready.Protocol);
        Assert.Equal(24, ready.Java.Feature);

        HostTargetEvaluated partial = Value("partial-target.json", "partial").Deserialize<HostTargetEvaluated>(HostProtocol.Json)!;
        JvmModule app = HostModel.ToModule(partial.Target, new JvmToolchain("/jdks/temurin-24", 24, null));
        Assert.Equal("app", app.Id);
        Assert.Equal(22, app.CompileToolchain.LanguageVersion);
        Assert.Equal(["/work/layouts/core/build/classes/java/main", "/m2/org/apache/commons/commons-lang3/3.17.0/commons-lang3-3.17.0.jar"], app.Classpath.Compile);
        Assert.Equal("core:main", app.SourceSet("main")!.CompileClasspath[0].ModuleRef);
        Assert.Equal("org.apache.commons:commons-lang3:3.17.0", app.SourceSet("main")!.CompileClasspath[1].Coordinates);
        Assert.Contains(app.SourceRoots, root => root.Kind == JvmSourceKind.MainResources);
        Assert.True(Assert.Single(app.MainClasses).Configured);

        HostWorkspaceResult result = Value("result-import.json", "result").Deserialize<HostWorkspaceResult>(HostProtocol.Json)!;
        WorkspaceSnapshot snapshot = HostModel.ToSnapshot(result, ready.Java);
        Assert.Equal(12, snapshot.Generation);
        Assert.Equal(JvmBuildSystem.Gradle, snapshot.BuildSystem);
        Assert.Equal(24, snapshot.Modules[0].ImportToolchain.LanguageVersion);
        Assert.Equal(8, snapshot.Modules[0].CompileToolchain.LanguageVersion);
        Assert.Equal("Could not resolve org.example:missing:1.0", snapshot.Modules[0].Problem);
        Assert.Equal(new JdkRequirement(null, 8, null, JdkRequirement.GradleToolchain), snapshot.Modules[0].CompileToolchain.Requirement);
        Assert.False(snapshot.Modules[0].CompileToolchain.Missing);
        Assert.True(snapshot.Modules[0].RuntimeToolchain.Missing);
        Assert.Equal(new JdkRequirement(null, 11, "temurin", JdkRequirement.GradleToolchain), snapshot.Modules[0].RuntimeToolchain.Requirement);
        Assert.Null(app.CompileToolchain.Requirement);
        Assert.False(snapshot.Modules[0].ImportToolchain.Missing);
        Assert.Contains(snapshot.Modules[0].SourceRoots, root => root.Kind == JvmSourceKind.Generated);
        Assert.Equal(3, snapshot.BuildInputs.Count);
        Assert.Equal(-1, snapshot.BuildInputs[2].Length);

        Assert.Equal("cancelled", HostReply.Read(File.ReadAllText(Example("error-cancelled.json")))!.Error);
        Assert.Contains("needs Java 21 or later", HostReply.Read(File.ReadAllText(Example("refused.json")))!.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestsAreWrittenAsTheExamplesSay()
    {
        HostRequest request = new(7, HostProtocol.Import, new HostImportParams("/work/layouts", 12, null,
            new HostRequirement("temurin-24", 24, "temurin", JdkRequirement.Project), ["-slow"], null,
            [new HostJdk("temurin-24", "/jdks/temurin-24.0.2", "24.0.2", 24, "Eclipse Adoptium")],
            new Dictionary<string, string> { ["SHARED_LIBS"] = "/libs" }, [new HostLibrary("Shared", ["jar://$SHARED_LIBS$/shared.jar!/"], null, null)]));
        JsonElement written = JsonSerializer.SerializeToElement(request, HostProtocol.Json);
        using JsonDocument expected = JsonDocument.Parse(File.ReadAllText(Example("request-import.json")));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, written), written.GetRawText());
        JsonElement cancel = JsonSerializer.SerializeToElement(new HostRequest(8, HostProtocol.Cancel, new HostCancelParams(7)), HostProtocol.Json);
        using JsonDocument expectedCancel = JsonDocument.Parse(File.ReadAllText(Example("request-cancel.json")));
        Assert.True(JsonElement.DeepEquals(expectedCancel.RootElement, cancel), cancel.GetRawText());
    }

    [Fact]
    public void AMissingJdkRequirementSurvivesAWarmReopen()
    {
        string root = Directory.CreateTempSubdirectory("wayfarer-cache-jdk-").FullName;
        WorkspaceTrust.Trust(root);
        try
        {
            HostWorkspaceResult result = Value("result-import.json", "result").Deserialize<HostWorkspaceResult>(HostProtocol.Json)! with { Root = root };
            string cache = Path.Combine(root, ".state", WorkspaceModelCache.FileName);
            WorkspaceModelCache.Write(cache, result, new HostJvm(root, "21", 21, null));

            JvmModule module = Assert.Single(WorkspaceModelCache.Read(cache, root)!.Snapshot.Modules);
            Assert.True(module.RuntimeToolchain.Missing);
            Assert.Equal(new JdkRequirement(null, 11, "temurin", JdkRequirement.GradleToolchain), module.RuntimeToolchain.Requirement);
        }
        finally
        {
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root) ?? root);
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<JvmProjectHost> StartAsync() =>
        JvmProjectHost.StartAsync(ToolingJvms.Find(), Testing, TestContext.Current.CancellationToken);

    private static JsonElement Value(string file, string kind)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Example(file)));
        return document.RootElement.GetProperty(kind).Clone();
    }

    internal static string Example(string file) =>
        Path.Combine(RepositoryRoot(), "jvm", "project-host", "protocol", file);

    internal static string RepositoryRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "WayfarerWanderer.slnx")))
            {
                return folder.FullName;
            }
        }
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "the condition did not hold within 10 seconds");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
