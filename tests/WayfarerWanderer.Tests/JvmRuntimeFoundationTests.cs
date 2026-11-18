using System.Diagnostics;
using Passenger.Core;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class JvmTrustCollection
{
    public const string Name = "JVM trust";
}

[Collection(JvmTrustCollection.Name)]
public sealed class JvmRuntimeFoundationTests
{
    [Fact]
    public async Task UntrustedImportAndBuildStartNoChildren()
    {
        string root = TestDirectory();
        WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root) ?? root);
        File.WriteAllText(Path.Combine(root, "pom.xml"), "<project xmlns=\"http://maven.apache.org/POM/4.0.0\"><modelVersion>4.0.0</modelVersion><groupId>example</groupId><artifactId>app</artifactId><version>1</version></project>");
        int starts = 0;
        EventHandler<ChildStart> started = (_, _) => Interlocked.Increment(ref starts);
        ChildProcesses.Started += started;
        try
        {
            WorkspaceImporter importer = new();
            await Assert.ThrowsAsync<UntrustedFolderException>(
                async () => await importer.ImportAsync(root, 1, TestContext.Current.CancellationToken));
            WorkspaceSnapshot snapshot = Snapshot(root);
            JvmBuild build = new();
            JvmBuildRequest request = new(root, "app", "test", []);
            await Assert.ThrowsAsync<UntrustedFolderException>(
                async () => await build.RunAsync(snapshot, request, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(0, starts);
        }
        finally
        {
            ChildProcesses.Started -= started;
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("maven", JvmBuildSystem.Maven, 3)]
    [InlineData("gradle", JvmBuildSystem.Gradle, 3)]
    public async Task ImportsFixtureModulesAfterTrust(string fixture, JvmBuildSystem buildSystem, int moduleCount)
    {
        string root = TestDirectory();
        CopyTree(Fixture("jvm", fixture), root);
        WorkspaceTrust.Trust(root);
        try
        {
            WorkspaceImporter importer = new();
            WorkspaceSnapshot snapshot = await importer.ImportAsync(root, 12, TestContext.Current.CancellationToken);
            Assert.Equal(12, snapshot.Generation);
            Assert.Equal(buildSystem, snapshot.BuildSystem);
            Assert.Equal(moduleCount, snapshot.Modules.Count);
            Assert.All(snapshot.Modules, module => Assert.True(Directory.Exists(module.Root)));
            Assert.Contains(snapshot.Modules, module => module.SourceRoots.Count > 0);
            Assert.Contains(snapshot.Modules, module => module.Tasks.Contains("test", StringComparer.Ordinal));
            if (buildSystem == JvmBuildSystem.Maven)
            {
                Assert.Contains(snapshot.MainClasses, main => main.ClassName == "example.Main");
            }
            else
            {
                Assert.Contains(snapshot.MainClasses, main => main.ClassName == "example.Service");
            }
        }
        finally
        {
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DropsOlderImportRepliesAndKeepsGoodSnapshotStale()
    {
        WorkspaceSnapshotState state = new();
        long first = state.BeginImport();
        WorkspaceSnapshot initial = Snapshot(TestDirectory()) with { Generation = first };
        Assert.True(state.Accept(initial));
        long second = state.BeginImport();
        Assert.True(state.IsStale);
        Assert.False(state.Accept(initial));
        Assert.Same(initial, state.Current);
        Assert.True(state.Accept(Snapshot("current") with { Generation = second }));
        Assert.False(state.IsStale);
        Directory.Delete(initial.Root, recursive: true);
    }

    [Fact]
    public async Task CancelsSupersededImportAndPublishesOnlyLatestSnapshot()
    {
        ControlledImporter importer = new();
        WorkspaceImportSession session = new(importer);
        Task<WorkspaceSnapshot?> first = session.RefreshAsync("old", TestContext.Current.CancellationToken);
        await importer.FirstStarted.Task;
        Task<WorkspaceSnapshot?> second = session.RefreshAsync("new", TestContext.Current.CancellationToken);
        WorkspaceSnapshot? old = await first;
        WorkspaceSnapshot? latest = await second;
        Assert.Null(old);
        Assert.Same(latest, session.State.Current);
        Assert.NotNull(latest);
        Assert.Equal("new", latest.Root);
    }

    [Fact]
    public void BuildsExactGradleAndMavenSelectorsIncludingKotlinNames()
    {
        TestSelector method = new(JvmBuildSystem.Gradle, "library", "demo.SampleSpec", "works with spaces");
        Assert.Equal(TestSelectorScope.Method, method.Scope);
        Assert.Equal(["--tests", "demo.SampleSpec.works with spaces"], method.Arguments());
        TestSelector kotlin = new(JvmBuildSystem.Maven, "library", "demo.SampleSpec", "`a name`");
        Assert.Equal(["-Dtest=demo.SampleSpec.`a name`"], kotlin.Arguments());
        Assert.Empty(new TestSelector(JvmBuildSystem.Gradle, "library", string.Empty).Arguments());
    }

    [Fact]
    public void InvocationIsolatesNewReportsAndPreservesOldPasses()
    {
        string root = TestDirectory();
        TestSelector selector = new(JvmBuildSystem.Gradle, "app", "example.AppTest");
        TestInvocation first = new(root, root, selector);
        File.WriteAllText(
            Path.Combine(first.ReportDirectory, "TEST-app.xml"),
            "<testsuite><testcase classname=\"example.AppTest\" name=\"passes\"/></testsuite>");
        Assert.Single(first.Complete(0).Cases);
        TestInvocation next = new(root, root, selector);
        TestRunResult failed = next.Complete(1);
        Assert.Empty(failed.Cases);
        Assert.NotEqual(first.Id, failed.InvocationId);
        Assert.True(File.Exists(Path.Combine(first.ReportDirectory, "TEST-app.xml")));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task PersistsNamedLaunchConfigurationWithArgumentsAndEnvironment()
    {
        string root = TestDirectory();
        LaunchConfigurationStore store = new(root);
        JvmLaunchConfiguration expected = new()
        {
            Name = "Boot local",
            Module = "service",
            MainClass = "example.Service",
            WorkingDirectory = "service",
            JavaHome = "jdk",
            Profile = "local",
            Arguments = ["--server.port=0"],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["MODE"] = "test" },
        };
        await store.SaveAsync(expected, TestContext.Current.CancellationToken);
        JvmLaunchConfiguration? actual = await store.LoadAsync(expected.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(actual);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Module, actual.Module);
        Assert.Equal(expected.MainClass, actual.MainClass);
        Assert.Equal(expected.Arguments, actual.Arguments);
        Assert.Equal(expected.Environment, actual.Environment);
        Assert.Single(await store.ListAsync(TestContext.Current.CancellationToken));
        await store.DeleteAsync(expected.Id, TestContext.Current.CancellationToken);
        Assert.Empty(await store.ListAsync(TestContext.Current.CancellationToken));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void LaunchUsesResolvedModuleJdkClasspathProfileAndArguments()
    {
        string root = TestDirectory();
        string javaHome = Path.Combine(root, "jdk");
        JvmToolchain toolchain = new(javaHome, 21, null);
        JvmModule module = new(
            "service",
            "service",
            root,
            [],
            new JvmClasspath(["app.jar", "library.jar"], [], ["app.jar", "library.jar"]),
            toolchain,
            toolchain,
            toolchain,
            ["run"],
            [new JvmMainClass("example.Service", string.Empty)]);
        WorkspaceSnapshot snapshot = new(1, root, JvmBuildSystem.Gradle, "settings.gradle.kts", toolchain, [module]);
        JvmLaunchConfiguration configuration = new()
        {
            Name = "Service",
            Module = "service",
            MainClass = "example.Service",
            Profile = "local",
            Arguments = ["--port=0"],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["MODE"] = "test" },
        };
        ProcessStartInfo start = new JvmLaunch(adapter: new DirectJvmLaunchAdapter()).Command(snapshot, configuration);
        Assert.Equal(Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), start.FileName);
        Assert.Equal(
            ["-Dspring.profiles.active=local", "-cp", "app.jar" + Path.PathSeparator + "library.jar", "example.Service", "--port=0"],
            start.ArgumentList);
        Assert.Equal("test", start.Environment["MODE"]);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task RunnerStreamsOutputThroughChildProcesses()
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        ProcessStartInfo start = ChildProcesses.Command(dotnet, ["--version"], Directory.GetCurrentDirectory());
        List<ProcessOutput> output = [];
        ChildProcessRunner runner = new();
        ProcessResult result = await runner.RunAsync(start, output.Add, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(output);
        Assert.Contains(output, line => line.Stream == ProcessOutputStream.StandardOutput);
    }

    [Fact]
    public async Task CancellationKillsRunnerProcessTree()
    {
        ProcessStartInfo start = OperatingSystem.IsWindows()
            ? ChildProcesses.Command(
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                ["/d", "/s", "/c", "ping 127.0.0.1 -n 30 >nul"],
                Directory.GetCurrentDirectory())
            : ChildProcesses.Command("/bin/sh", ["-c", "sleep 30"], Directory.GetCurrentDirectory());
        int startedId = 0;
        EventHandler<ChildStart> started = (_, child) => startedId = child.ProcessId;
        ChildProcesses.Started += started;
        try
        {
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));
            ChildProcessRunner runner = new();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await runner.RunAsync(start, cancellationToken: cancellation.Token));
            Assert.True(startedId > 0);
            Assert.False(ChildProcesses.Leaves(startedId));
        }
        finally
        {
            ChildProcesses.Started -= started;
        }
    }

    private static WorkspaceSnapshot Snapshot(string root)
    {
        JvmToolchain toolchain = new(null, null, null);
        JvmModule module = new("app", "app", root, [], new JvmClasspath("", "", ""), toolchain, toolchain, toolchain, ["test"], []);
        return new WorkspaceSnapshot(1, root, JvmBuildSystem.Maven, Path.Combine(root, "pom.xml"), toolchain, [module]);
    }

    private static string Fixture(params string[] path)
    {
        string[] roots = [Directory.GetCurrentDirectory(), AppContext.BaseDirectory];
        foreach (string start in roots)
        {
            DirectoryInfo? at = new(start);
            while (at is not null)
            {
                string candidate = Path.Combine([at.FullName, "tests", "fixtures", .. path]);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                at = at.Parent;
            }
        }
        throw new DirectoryNotFoundException($"JVM fixture '{string.Join('/', path)}' was not found.");
    }

    private static string TestDirectory()
    {
        return Directory.CreateTempSubdirectory("wayfarer-jvm-runtime-").FullName;
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private sealed class ControlledImporter : IWorkspaceImporter
    {
        private int calls;

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkspaceSnapshot> ImportAsync(
            string workspaceRoot,
            long generation,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                FirstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return Snapshot(workspaceRoot) with { Generation = generation };
        }
    }
}
