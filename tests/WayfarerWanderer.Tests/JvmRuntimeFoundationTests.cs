using System.Diagnostics;
using System.Runtime.InteropServices;
using Passenger.Core;
using WayfarerWanderer.App.Shell.Explorer;
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
    public async Task BuildAndRunUseTheProjectJdkOverTheEnvironments()
    {
        string root = TestDirectory();
        string jdk = Directory.CreateDirectory(Path.Combine(root, "chosen-jdk")).FullName;
        File.WriteAllText(Path.Combine(root, "pom.xml"), "<project xmlns=\"http://maven.apache.org/POM/4.0.0\"><modelVersion>4.0.0</modelVersion><groupId>example</groupId><artifactId>app</artifactId><version>1</version></project>");
        Directory.CreateDirectory(Path.Combine(root, ".wayfarer"));
        File.WriteAllText(ProjectJdk.FileOf(root), ProjectJdk.Serialize(jdk));
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(root, "mvnw.cmd"), "@echo off\r\necho JAVA_HOME=%JAVA_HOME%\r\n");
        }
        else
        {
            string wrapper = Path.Combine(root, "mvnw");
            File.WriteAllText(wrapper, "#!/bin/sh\necho \"JAVA_HOME=$JAVA_HOME\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        WorkspaceTrust.Trust(root);
        try
        {
            WorkspaceSnapshot snapshot = await new WorkspaceImporter().ImportAsync(root, 1, TestContext.Current.CancellationToken);
            Assert.Equal(jdk, snapshot.ToolingToolchain.Home);
            Assert.All(snapshot.Modules, module => Assert.Equal(jdk, module.RuntimeToolchain.Home));

            ProcessResult built = await new JvmBuild().RunAsync(
                snapshot,
                new JvmBuildRequest(root, ".", "compile", []),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, built.ExitCode);
            Assert.Contains(built.Output, line => line.Line == "JAVA_HOME=" + jdk);
            ProcessStartInfo run = new WrapperJvmLaunchAdapter().Command(
                snapshot,
                new JvmLaunchConfiguration { Name = "Run", Module = ".", MainClass = "example.Main" });
            Assert.Equal(jdk, run.Environment["JAVA_HOME"]);

            File.Delete(ProjectJdk.FileOf(root));
            WorkspaceSnapshot unrecorded = await new WorkspaceImporter().ImportAsync(root, 2, TestContext.Current.CancellationToken);
            string? environment = Environment.GetEnvironmentVariable("JAVA_HOME");
            Assert.Equal(string.IsNullOrEmpty(environment) ? null : environment, unrecorded.ToolingToolchain.Home);
        }
        finally
        {
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root) ?? root);
        }
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
            if (OperatingSystem.IsWindows())
            {
                Assert.False(ChildProcesses.Leaves(startedId));
            }
            else
            {
                // Leaves stays true off Windows until Passenger's 10 s prune finds the group empty, so ask the system.
                Assert.True(
                    await GroupEndsAsync(startedId, TimeSpan.FromSeconds(5)),
                    $"Process group {startedId} still had a process 5 s after cancellation: the runner left part of its tree running.");
            }
        }
        finally
        {
            ChildProcesses.Started -= started;
        }
    }

    [Fact]
    public async Task ImportsAnIdeaCourseModuleAndCompilesItWithJavac()
    {
        (string root, WorkspaceSnapshot snapshot) = await ImportFixture("jvm", "idea", "course");
        try
        {
            JvmModule module = Assert.Single(snapshot.Modules);
            string sources = Path.Combine(root, "src");
            string output = Path.Combine(root, "out", "production", "Course");
            Assert.Equal(JvmBuildSystem.Idea, snapshot.BuildSystem);
            Assert.Equal(4, snapshot.Generation);
            Assert.EndsWith("Course.iml", snapshot.BuildFile, StringComparison.Ordinal);
            Assert.Equal(25, snapshot.ToolingToolchain.LanguageVersion);
            Assert.Equal("openjdk-25", snapshot.ToolingToolchain.Vendor);
            Assert.Equal(IdeaJdks.Home("openjdk-25"), snapshot.ToolingToolchain.Home);
            Assert.Equal(sources, Assert.Single(module.SourceRoots).Path);
            Assert.Equal(JvmSourceKind.MainJava, module.SourceRoots[0].Kind);
            Assert.Equal("Main", Assert.Single(module.MainClasses).ClassName);
            Assert.Equal(["classes"], module.Tasks);
            Assert.Equal(output, module.Classpath.Runtime[0]);
            Assert.Empty(module.Classpath.Compile);
            Assert.Equal(Path.Combine(root, "out"), snapshot.OutputRoot);
            Assert.Equal(["src"], IdeaLanguageSettings.SourcePaths(snapshot));
            Assert.Equal("out/production/Course", IdeaLanguageSettings.OutputPath(snapshot));
            Assert.Empty(IdeaLanguageSettings.Libraries(snapshot));
            Assert.Null(IdeaLanguageSettings.Value(snapshot, "java.other"));

            WorkspaceSnapshot ready = WithJdk(snapshot);
            ProcessStartInfo compile = new IdeaJvmBuildAdapter().Command(ready, new JvmBuildRequest(root, "Course", "classes", []));
            string javac = OperatingSystem.IsWindows() ? "javac.exe" : "javac";
            Assert.Equal(Path.Combine(ready.ToolingToolchain.Home!, "bin", javac), compile.FileName);
            Assert.Equal(
                ["--release", "25", "-d", output, Path.Combine(sources, "Helper.java"), Path.Combine(sources, "Main.java")],
                compile.ArgumentList);
            string json = System.Text.Json.JsonSerializer.Serialize(IdeaLanguageSettings.Runtimes(ready));
            Assert.Contains("JavaSE-25", json, StringComparison.Ordinal);
            Assert.Contains(System.Text.Json.JsonSerializer.Serialize(ready.ToolingToolchain.Home), json, StringComparison.Ordinal);

            InvalidDataException tests = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new JvmBuild(new RecordingRunner()).RunAsync(ready, new JvmBuildRequest(root, "Course", "test", []), cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("no JUnit", tests.Message, StringComparison.Ordinal);

            ExplorerTree tree = new();
            tree.Load(root, snapshot);
            ExplorerNode source = tree.Rows.Single(row => row.Name == "src");
            Assert.Equal(JvmFolder.SourceRoot, source.JvmFolder);
            Assert.Equal("sources", source.Detail);
            Assert.True(tree.Rows.Single(row => row.Name == "out").IsDimmed);
            Assert.Equal("Java", FileIconSet.ForFile("Course.iml").Shape);
        }
        finally
        {
            Close(root);
        }
    }

    [Fact]
    public async Task ImportsIdeaLibrariesAndKeepsAMissingJar()
    {
        (string root, WorkspaceSnapshot snapshot) = await ImportFixture("jvm", "idea", "library");
        try
        {
            JvmModule module = Assert.Single(snapshot.Modules);
            string jar = Path.Combine(root, "lib", "demo.jar");
            string missing = Path.Combine(root, "lib", "missing.jar");
            Assert.Equal([jar], module.Classpath.Compile);
            Assert.Equal([Path.Combine(root, "out", "production", "Library"), jar], module.Classpath.Runtime);
            Assert.Equal([missing], module.AbsentLibraries);
        }
        finally
        {
            Close(root);
        }
    }

    [Fact]
    public async Task IdeaLaunchCompilesThenStartsJavaWithoutAWrapper()
    {
        (string root, WorkspaceSnapshot snapshot) = await ImportFixture("jvm", "idea", "course");
        try
        {
            snapshot = WithJdk(snapshot);
            RecordingRunner runner = new();
            JvmLaunchConfiguration configuration = new()
            {
                Module = "Course",
                MainClass = "Main",
                JavaHome = snapshot.ToolingToolchain.Home ?? string.Empty,
            };
            ProcessResult ran = await new JvmLaunch(runner).RunAsync(snapshot, configuration, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, ran.ExitCode);
            Assert.Equal(2, runner.Starts.Count);
            Assert.EndsWith(OperatingSystem.IsWindows() ? "javac.exe" : "javac", runner.Starts[0].FileName, StringComparison.Ordinal);
            Assert.Contains("--release", runner.Starts[0].ArgumentList);
            Assert.EndsWith(OperatingSystem.IsWindows() ? "java.exe" : "java", runner.Starts[1].FileName, StringComparison.Ordinal);
            string output = Path.Combine(root, "out", "production", "Course");
            Assert.Contains(runner.Starts[1].ArgumentList, argument => argument.StartsWith(output, StringComparison.Ordinal));

            RecordingRunner debug = new();
            IReadOnlyList<string> classpath = await JvmDebugClasspath.ResolveAsync(
                snapshot, configuration, TestContext.Current.CancellationToken, new JvmBuild(debug));
            Assert.Equal(output, classpath[0]);
            ProcessStartInfo debugCompile = Assert.Single(debug.Starts);
            Assert.EndsWith(OperatingSystem.IsWindows() ? "javac.exe" : "javac", debugCompile.FileName, StringComparison.Ordinal);
            Assert.DoesNotContain("gradle", debugCompile.FileName, StringComparison.Ordinal);
            Assert.DoesNotContain("mvn", debugCompile.FileName, StringComparison.Ordinal);
        }
        finally
        {
            Close(root);
        }
    }

    [Fact]
    public async Task MavenPomWinsOverAnIdeaModuleFile()
    {
        string root = TestDirectory();
        CopyTree(Fixture("jvm", "idea", "course"), root);
        File.WriteAllText(Path.Combine(root, "pom.xml"), "<project xmlns=\"http://maven.apache.org/POM/4.0.0\"><modelVersion>4.0.0</modelVersion><groupId>example</groupId><artifactId>app</artifactId><version>1</version></project>");
        WorkspaceTrust.Trust(root);
        try
        {
            WorkspaceSnapshot snapshot = await new WorkspaceImporter().ImportAsync(root, 1, TestContext.Current.CancellationToken);
            Assert.Equal(JvmBuildSystem.Maven, snapshot.BuildSystem);
        }
        finally
        {
            Close(root);
        }
    }

    [Fact]
    public async Task UntrustedIdeaImportAndBuildStartNoChildren()
    {
        string root = TestDirectory();
        CopyTree(Fixture("jvm", "idea", "course"), root);
        WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root) ?? root);
        int starts = 0;
        EventHandler<ChildStart> started = (_, _) => Interlocked.Increment(ref starts);
        ChildProcesses.Started += started;
        try
        {
            await Assert.ThrowsAsync<UntrustedFolderException>(() =>
                new WorkspaceImporter().ImportAsync(root, 1, TestContext.Current.CancellationToken));
            WorkspaceSnapshot snapshot = Snapshot(root) with { BuildSystem = JvmBuildSystem.Idea };
            await Assert.ThrowsAsync<UntrustedFolderException>(() =>
                new JvmBuild().RunAsync(snapshot, new JvmBuildRequest(root, "Course", "classes", []), cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(0, starts);
        }
        finally
        {
            ChildProcesses.Started -= started;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AFolderOfIdeaProjectsNamesTheModuleFolder()
    {
        string root = TestDirectory();
        Directory.CreateDirectory(Path.Combine(root, "Course"));
        File.WriteAllText(Path.Combine(root, "Course", "Course.iml"), "<module type=\"JAVA_MODULE\" version=\"4\"></module>");
        WorkspaceTrust.Trust(root);
        try
        {
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new WorkspaceImporter().ImportAsync(root, 1, TestContext.Current.CancellationToken));
            Assert.Contains(".iml", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Close(root);
        }
    }

    [Fact]
    public void IdeaJdkTableResolvesTheNamedHome()
    {
        string root = TestDirectory();
        try
        {
            string jdk = Directory.CreateDirectory(Path.Combine(root, "fake-jdk")).FullName;
            string options = Path.Combine(root, "config", "IntelliJIdeaTest", "options");
            Directory.CreateDirectory(options);
            File.WriteAllText(Path.Combine(options, "jdk.table.xml"), "<application><component name=\"ProjectJdkTable\"><jdk version=\"2\"><name value=\"openjdk-25\" /><homePath value=\"" + jdk + "\" /></jdk></component></application>");
            Assert.Equal(jdk, IdeaJdks.Home("openjdk-25", Path.Combine(root, "config")));
            string missing = IdeaJdks.Missing("openjdk-25");
            Assert.Contains("openjdk-25", missing, StringComparison.Ordinal);
            Assert.Contains("JAVA_HOME", missing, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(string Root, WorkspaceSnapshot Snapshot)> ImportFixture(params string[] path)
    {
        string root = TestDirectory();
        CopyTree(Fixture(path), root);
        WorkspaceTrust.Trust(root);
        try
        {
            return (root, await new WorkspaceImporter().ImportAsync(root, 4, TestContext.Current.CancellationToken));
        }
        catch
        {
            Close(root);
            throw;
        }
    }

    private static WorkspaceSnapshot WithJdk(WorkspaceSnapshot snapshot)
    {
        if (snapshot.ToolingToolchain.Home is { } home && Directory.Exists(home))
        {
            return snapshot;
        }
        string jdk = Directory.CreateDirectory(Path.Combine(snapshot.Root, "jdk")).FullName;
        JvmToolchain toolchain = snapshot.ToolingToolchain with { Home = jdk };
        JvmModule module = snapshot.Modules[0] with
        {
            ImportToolchain = toolchain,
            CompileToolchain = toolchain,
            RuntimeToolchain = toolchain,
        };
        return snapshot with { ToolingToolchain = toolchain, Modules = [module] };
    }

    private static void Close(string root)
    {
        WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root) ?? root);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingRunner : IChildProcessRunner
    {
        public List<ProcessStartInfo> Starts { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessStartInfo start, Action<ProcessOutput>? output = null, CancellationToken cancellationToken = default)
        {
            Starts.Add(start);
            return Task.FromResult(new ProcessResult(0, [], TimeSpan.Zero));
        }
    }

    private static async Task<bool> GroupEndsAsync(int group, TimeSpan limit)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (waited.Elapsed < limit)
        {
            if (kill(-group, 0) != 0 && Marshal.GetLastPInvokeError() == NoSuchProcess)
            {
                return true;
            }
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        return false;
    }

    private const int NoSuchProcess = 3;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

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
