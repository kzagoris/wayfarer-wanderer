using System.Text.Json;
using Passenger.Core;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>
/// Importing a workspace through the JVM project host: nothing starts before trust, the model of each fixture matches
/// what its build tool resolves (the fixture's expected-model.json), and the session keeps generations apart.
/// </summary>
[Collection(JvmTrustCollection.Name)]
public sealed class WorkspaceImportTests
{
    public static TheoryData<string> Fixtures => ["maven", "gradle", "maven-layouts", "gradle-layouts", "idea/course", "idea/multi"];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task NothingStartsBeforeTrust(string fixture)
    {
        string root = Copy(fixture);
        WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root) ?? root);
        MarkWrappers(root);
        int starts = 0;
        EventHandler<ChildStart> started = (_, _) => Interlocked.Increment(ref starts);
        ChildProcesses.Started += started;
        int hostsBefore = ProjectHosts.Current.Count;
        try
        {
            WorkspaceImportSession session = new(new ProjectHostImporter());
            await Assert.ThrowsAsync<UntrustedFolderException>(() => session.RefreshAsync(root, TestContext.Current.CancellationToken));
            Assert.Null(session.State.Current);
            Assert.Equal(0, starts);
            Assert.Equal(hostsBefore, ProjectHosts.Current.Count);
            Assert.Empty(Directory.EnumerateFiles(root, "wrapper-started.marker", SearchOption.AllDirectories));
        }
        finally
        {
            ChildProcesses.Started -= started;
            Delete(root);
        }
    }

    [Theory]
    [InlineData("maven-layouts")]
    [InlineData("gradle-layouts")]
    public async Task TheModelIsWhatTheBuildToolResolves(string fixture)
    {
        string root = Copy(fixture);
        WorkspaceTrust.Trust(root);
        try
        {
            ProjectHostImporter importer = new();
            List<ImportProgress> progress = [];
            importer.Progress += progress.Add;
            WorkspaceSnapshot snapshot = await importer.ImportAsync(root, 7, TestContext.Current.CancellationToken);
            Assert.Equal(7, snapshot.Generation);
            ExpectedModel.Compare(Path.Combine(FixtureFolder(fixture), "expected-model.json"), root, snapshot);
            Assert.Equal(snapshot.Modules.Count, progress.Count);
            Assert.True(snapshot.ImportToolchain().LanguageVersion >= 17, "the import JDK is recorded");
            Assert.NotEmpty(snapshot.BuildInputs);
            Assert.All(snapshot.Modules, module => Assert.Null(module.Problem));
        }
        finally
        {
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Delete(root);
        }
    }

    [Theory]
    [InlineData("maven", JvmBuildSystem.Maven, "example.Main")]
    [InlineData("gradle", JvmBuildSystem.Gradle, "example.Service")]
    public async Task ImportsFixtureModulesAfterTrust(string fixture, JvmBuildSystem buildSystem, string main)
    {
        string root = Copy(fixture);
        WorkspaceTrust.Trust(root);
        try
        {
            WorkspaceSnapshot snapshot = await new ProjectHostImporter().ImportAsync(root, 12, TestContext.Current.CancellationToken);
            Assert.Equal(12, snapshot.Generation);
            Assert.Equal(buildSystem, snapshot.BuildSystem);
            Assert.All(snapshot.Modules, module => Assert.True(Directory.Exists(module.Root)));
            Assert.Contains(snapshot.Modules, module => module.SourceRoots.Count > 0);
            Assert.Contains(snapshot.Modules, module => module.Tasks.Contains("test", StringComparer.Ordinal));
            Assert.Contains(snapshot.MainClasses, candidate => candidate.ClassName == main);
            // The classpaths the reader left empty: every module with sources now has one.
            Assert.All(snapshot.Modules.Where(module => module.SourceSet("main") is not null), module => Assert.NotEmpty(module.Classpath.Runtime));
        }
        finally
        {
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Delete(root);
        }
    }

    [Fact]
    public async Task AMissingWrapperNamesWhatToDo()
    {
        string root = Copy("maven-layouts");
        File.Delete(Path.Combine(root, "mvnw"));
        File.Delete(Path.Combine(root, "mvnw.cmd"));
        WorkspaceTrust.Trust(root);
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            // No wrapper and, with PATH emptied for the host, no Maven installation either.
            ProjectHosts.Options = ProjectHostOptions.Default with { Environment = new Dictionary<string, string> { ["PATH"] = string.Empty } };
            await ProjectHosts.EndServingAsync(root, "a new host with an empty PATH");
            await EndAllHostsAsync();
            ProjectHostFailed failed = await Assert.ThrowsAsync<ProjectHostFailed>(
                () => new ProjectHostImporter().ImportAsync(root, 1, TestContext.Current.CancellationToken));
            Assert.Contains("has no Maven wrapper", failed.Reason, StringComparison.Ordinal);
            Assert.Contains("Settings", failed.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("Passenger", failed.Reason, StringComparison.Ordinal);
        }
        finally
        {
            ProjectHosts.Options = ProjectHostOptions.Default;
            await EndAllHostsAsync();
            Environment.SetEnvironmentVariable("PATH", path);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Delete(root);
        }
    }

    [Fact]
    public async Task AnAbsentToolingJvmFailsTheImportWithItsRecovery()
    {
        string root = Copy("maven-layouts");
        WorkspaceTrust.Trust(root);
        Func<ToolingJvm> before = ProjectHosts.FindJvm;
        try
        {
            ProjectHosts.FindJvm = () => ToolingJvms.Find(minimum: 999);
            await EndAllHostsAsync();
            WorkspaceImportSession session = new(new ProjectHostImporter());
            ProjectHostFailed failed = await Assert.ThrowsAsync<ProjectHostFailed>(() => session.RefreshAsync(root, TestContext.Current.CancellationToken));
            Assert.Contains("needs Java 999 or later", failed.Reason, StringComparison.Ordinal);
            Assert.Contains("Settings", failed.Reason, StringComparison.Ordinal);
            Assert.Null(session.State.Current);
        }
        finally
        {
            ProjectHosts.FindJvm = before;
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Delete(root);
        }
    }

    [Fact]
    public async Task AFailedImportKeepsTheLastGoodSnapshotStaleAndARepairRestoresIt()
    {
        string root = Copy("maven-layouts");
        WorkspaceTrust.Trust(root);
        try
        {
            WorkspaceImportSession session = new(new ProjectHostImporter());
            WorkspaceSnapshot good = (await session.RefreshAsync(root, TestContext.Current.CancellationToken))!;
            string pom = Path.Combine(root, "core", "pom.xml");
            string text = await File.ReadAllTextAsync(pom, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(pom, text.Replace("</project>", "<broken>", StringComparison.Ordinal), TestContext.Current.CancellationToken);
            ProjectHostFailed failed = await Assert.ThrowsAsync<ProjectHostFailed>(() => session.RefreshAsync(root, TestContext.Current.CancellationToken));
            Assert.Contains("pom.xml", failed.Reason, StringComparison.Ordinal);
            Assert.Same(good, session.State.Current);
            Assert.True(session.State.IsStale);
            await File.WriteAllTextAsync(pom, text, TestContext.Current.CancellationToken);
            WorkspaceSnapshot repaired = (await session.RefreshAsync(root, TestContext.Current.CancellationToken))!;
            Assert.Same(repaired, session.State.Current);
            Assert.False(session.State.IsStale);
        }
        finally
        {
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Delete(root);
        }
    }

    [Fact]
    public async Task AModuleWhoseDependencyDoesNotResolveCarriesItsProblem()
    {
        string root = Copy("maven-layouts");
        WorkspaceTrust.Trust(root);
        try
        {
            string pom = Path.Combine(root, "app", "pom.xml");
            string text = await File.ReadAllTextAsync(pom, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(pom, text.Replace("<dependencies>",
                "<dependencies><dependency><groupId>org.example.absent</groupId><artifactId>nowhere</artifactId><version>0.0.1</version></dependency>",
                StringComparison.Ordinal), TestContext.Current.CancellationToken);
            WorkspaceSnapshot snapshot = await new ProjectHostImporter().ImportAsync(root, 1, TestContext.Current.CancellationToken);
            JvmModule app = snapshot.Module("app")!;
            Assert.Contains("org.example.absent:nowhere", app.Problem, StringComparison.Ordinal);
            Assert.Null(snapshot.Module("core")!.Problem);
            Assert.Contains(app.Classpath.Runtime, path => path.Contains("commons-lang3", StringComparison.Ordinal));
        }
        finally
        {
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Delete(root);
        }
    }

    [Fact]
    public async Task AToolchainNoJdkMeetsIsAMissingRequirementWhileTheRestStands()
    {
        // Above any JDK on this machine, so the test cannot pass by accident where every JDK is installed.
        int absent = Math.Max(Environment.Version.Major, JdkRegistry.Current.All().Select(jdk => jdk.Feature).DefaultIfEmpty(0).Max()) + 40;
        string root = Copy("gradle-layouts");
        WorkspaceTrust.Trust(root);
        try
        {
            string build = Path.Combine(root, "app", "build.gradle.kts");
            await File.WriteAllTextAsync(build, (await File.ReadAllTextAsync(build, TestContext.Current.CancellationToken))
                .Replace("JavaLanguageVersion.of(22)", $"JavaLanguageVersion.of({absent})", StringComparison.Ordinal), TestContext.Current.CancellationToken);
            // The fixture turns off toolchain download (gradle.properties), so nothing is fetched.

            WorkspaceSnapshot snapshot = await new ProjectHostImporter().ImportAsync(root, 1, TestContext.Current.CancellationToken);
            JvmModule app = snapshot.Module("app")!;
            Assert.True(app.CompileToolchain.Missing);
            Assert.True(app.RuntimeToolchain.Missing);
            Assert.Equal(new JdkRequirement(null, absent, null, JdkRequirement.GradleToolchain), app.RuntimeToolchain.Requirement);
            Assert.Contains($"no JDK {absent}", app.Problem, StringComparison.Ordinal);
            JvmModule core = snapshot.Module("core")!;
            Assert.False(core.CompileToolchain.Missing);
            Assert.Null(core.Problem);
            Assert.NotNull(app.ImportToolchain.Home);
            Assert.NotEmpty(app.SourceSets);
        }
        finally
        {
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Delete(root);
        }
    }

    [Fact]
    public async Task AToolchainOnlyTheJdkTableKnowsResolvesInGradle()
    {
        // Gradle's own detection is off, so only the paths Wayfarer passes can find the toolchain; it must not be the JDK
        // the daemon runs on, which Gradle always knows.
        int?[] daemon = [ToolingJvms.At(Environment.GetEnvironmentVariable("JAVA_HOME") ?? string.Empty)?.Feature, ToolingJvms.Find().Feature];
        Jdk? only = JdkRegistry.Current.All().Where(jdk => jdk.Feature >= 8 && !daemon.Contains(jdk.Feature)).OrderByDescending(jdk => jdk.Feature).FirstOrDefault();
        Assert.SkipWhen(only is null, "No JDK here other than the one Gradle runs on.");
        string root = Copy("gradle-layouts");
        WorkspaceTrust.Trust(root);
        try
        {
            await File.AppendAllTextAsync(Path.Combine(root, "gradle.properties"), "\norg.gradle.java.installations.auto-detect=false\n", TestContext.Current.CancellationToken);
            string build = Path.Combine(root, "app", "build.gradle.kts");
            await File.WriteAllTextAsync(build, (await File.ReadAllTextAsync(build, TestContext.Current.CancellationToken))
                .Replace("JavaLanguageVersion.of(22)", $"JavaLanguageVersion.of({only!.Feature})", StringComparison.Ordinal), TestContext.Current.CancellationToken);

            WorkspaceSnapshot snapshot = await new ProjectHostImporter().ImportAsync(root, 1, TestContext.Current.CancellationToken);
            JvmModule app = snapshot.Module("app")!;
            Assert.Null(app.Problem);
            Assert.False(app.CompileToolchain.Missing);
            Assert.Equal(only.Feature, ToolingJvms.At(app.CompileToolchain.Home!)!.Feature);
        }
        finally
        {
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Delete(root);
        }
    }

    [Fact]
    public async Task AnIntelliJProjectOfSeveralModulesImportsAndCompilesInDependencyOrder()
    {
        string root = Copy("idea/multi");
        WorkspaceTrust.Trust(root);
        try
        {
            // The project names a JDK of this machine that compiles release 17.
            Jdk? jdk = JdkRegistry.Current.All().Where(candidate => candidate.Feature >= 17).OrderBy(candidate => candidate.Feature).FirstOrDefault();
            Assert.SkipWhen(jdk is null, "No JDK 17 or later here to compile with.");
            string misc = Path.Combine(root, ".idea", "misc.xml");
            await File.WriteAllTextAsync(misc, (await File.ReadAllTextAsync(misc, TestContext.Current.CancellationToken))
                .Replace("project-jdk-name=\"17\"", $"project-jdk-name=\"{jdk!.Name}\"", StringComparison.Ordinal), TestContext.Current.CancellationToken);
            WorkspaceSnapshot snapshot = await new ProjectHostImporter().ImportAsync(root, 1, TestContext.Current.CancellationToken);
            Assert.Equal(["app", "core"], snapshot.Modules.Select(module => module.Id));
            JvmModule app = snapshot.Module("app")!;
            string coreClasses = Path.Combine(root, "out", "production", "core");
            Assert.Equal([coreClasses], app.Classpath.Compile);
            Assert.Contains(coreClasses, app.Classpath.Runtime);
            Assert.Equal(["core", "app"], IdeaJvmBuildAdapter.CompileOrder(snapshot, app).Select(module => module.Id));
            Assert.Empty(IdeaLanguageSettings.Libraries(snapshot));
            Assert.Equal(["app/src", "core/src"], IdeaLanguageSettings.SourcePaths(snapshot));
            Assert.NotNull(app.CompileToolchain.Home);

            ProcessResult built = await new JvmBuild().RunAsync(snapshot, new JvmBuildRequest(root, "app", "classes", []), cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(built.ExitCode == 0, string.Join(Environment.NewLine, built.Output.Select(line => line.Line)));
            Assert.True(File.Exists(Path.Combine(coreClasses, "example", "core", "Greeting.class")));
            Assert.True(File.Exists(Path.Combine(root, "out", "production", "app", "example", "app", "Main.class")));
        }
        finally
        {
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Delete(root);
        }
    }

    [Fact]
    public async Task AnIntelliJModuleNamingAnAbsentJdkIsAMissingRequirement()
    {
        string root = Copy("idea/course");
        WorkspaceTrust.Trust(root);
        try
        {
            // The release the name carries is the feature asked for when the name is not in the table, even over a
            // lower language level (JDK_25 in misc.xml): one above any JDK here.
            int absent = Math.Max(Environment.Version.Major, JdkRegistry.Current.All().Select(jdk => jdk.Feature).DefaultIfEmpty(0).Max()) + 40;
            string iml = Path.Combine(root, "Course.iml");
            await File.WriteAllTextAsync(iml, (await File.ReadAllTextAsync(iml, TestContext.Current.CancellationToken))
                .Replace("<orderEntry type=\"inheritedJdk\" />", $"<orderEntry type=\"jdk\" jdkName=\"nowhere-jdk-{absent}\" jdkType=\"JavaSDK\" />", StringComparison.Ordinal),
                TestContext.Current.CancellationToken);

            WorkspaceSnapshot snapshot = await new ProjectHostImporter().ImportAsync(root, 1, TestContext.Current.CancellationToken);
            JvmModule module = Assert.Single(snapshot.Modules);
            Assert.True(module.CompileToolchain.Missing);
            Assert.Equal(25, module.CompileToolchain.LanguageVersion);
            Assert.Equal(new JdkRequirement($"nowhere-jdk-{absent}", absent, null, JdkRequirement.Idea), module.CompileToolchain.Requirement);
        }
        finally
        {
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Delete(root);
        }
    }

    [Fact]
    public async Task OneHostServesTwoWorkspacesAndEndsWithTheLast()
    {
        string first = Copy("maven-layouts");
        string second = Copy("maven-layouts");
        WorkspaceTrust.Trust(first);
        WorkspaceTrust.Trust(second);
        try
        {
            await EndAllHostsAsync();
            JvmProjectHost one = await ProjectHosts.ForAsync(first, TestContext.Current.CancellationToken);
            JvmProjectHost two = await ProjectHosts.ForAsync(second, TestContext.Current.CancellationToken);
            Assert.Same(one, two);
            await ProjectHosts.ReleaseAsync(first);
            Assert.False(one.HasExited);
            await ProjectHosts.ReleaseAsync(second);
            Assert.True(one.HasExited);
        }
        finally
        {
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(first));
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(second));
            Delete(first);
            Delete(second);
        }
    }

    [Fact]
    public async Task TakingTrustBackEndsTheHostServingTheFolder()
    {
        string root = Copy("maven-layouts");
        WorkspaceTrust.Trust(root);
        try
        {
            await EndAllHostsAsync();
            JvmProjectHost host = await ProjectHosts.ForAsync(root, TestContext.Current.CancellationToken);
            await ProjectHosts.EndServingAsync(root, "trust was taken back");
            Assert.True(host.HasExited || await ExitsAsync(host));
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            await Assert.ThrowsAsync<UntrustedFolderException>(() => ProjectHosts.ForAsync(root, TestContext.Current.CancellationToken));
        }
        finally
        {
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Delete(root);
        }
    }

    [Fact]
    public void DropsOlderImportRepliesAndKeepsGoodSnapshotStale()
    {
        WorkspaceSnapshotState state = new();
        long first = state.BeginImport();
        WorkspaceSnapshot initial = Snapshot("first") with { Generation = first };
        Assert.True(state.Accept(initial));
        long second = state.BeginImport();
        Assert.True(state.IsStale);
        Assert.False(state.Accept(initial));
        Assert.Same(initial, state.Current);
        Assert.True(state.Accept(Snapshot("current") with { Generation = second }));
        Assert.False(state.IsStale);
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
    public async Task ASwitchWithAReplyInFlightDropsTheOldWorkspacesModel()
    {
        string old = Copy("maven-layouts");
        string next = Copy("gradle-layouts");
        WorkspaceTrust.Trust(old);
        WorkspaceTrust.Trust(next);
        try
        {
            WorkspaceImportSession session = new(new ProjectHostImporter());
            Task<WorkspaceSnapshot?> inFlight = session.RefreshAsync(old, TestContext.Current.CancellationToken);
            Task<WorkspaceSnapshot?> switched = session.RefreshAsync(next, TestContext.Current.CancellationToken);
            WorkspaceSnapshot? dropped = await inFlight;
            WorkspaceSnapshot? current = await switched;
            Assert.Null(dropped);
            Assert.NotNull(current);
            Assert.True(PathNames.Same(Path.GetFullPath(next), current.Root));
            Assert.Same(current, session.State.Current);
        }
        finally
        {
            await ProjectHosts.ReleaseAsync(old);
            await ProjectHosts.ReleaseAsync(next);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(old));
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(next));
            Delete(old);
            Delete(next);
        }
    }

    [Fact]
    public async Task AKeptModelWhoseInputsStandIsCurrentAndNothingImports()
    {
        string root = Directory.CreateTempSubdirectory("wayfarer-warm-").FullName;
        WorkspaceTrust.Trust(root);
        CountingImporter importer = new();
        WorkspaceImportSession session = new(importer, KeepModel(root));
        try
        {
            WorkspaceSnapshot? opened = await session.OpenAsync(root, TestContext.Current.CancellationToken);
            Assert.NotNull(opened);
            Assert.Same(opened, session.State.Current);
            Assert.False(session.State.IsStale);
            Assert.Equal(0, importer.Calls);
            Assert.Equal(Path.Combine(root, "pom.xml"), opened.BuildInputs[0].Path);
        }
        finally
        {
            session.Dispose();
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Delete(root);
        }
    }

    [Fact]
    public async Task AKeptModelWhoseInputChangedIsStaleWhileItImports()
    {
        string root = Directory.CreateTempSubdirectory("wayfarer-warm-").FullName;
        WorkspaceTrust.Trust(root);
        string cache = KeepModel(root);
        await File.AppendAllTextAsync(Path.Combine(root, "pom.xml"), "<!-- changed -->", TestContext.Current.CancellationToken);
        GatedImporter importer = new();
        WorkspaceImportSession session = new(importer, cache);
        try
        {
            Task<WorkspaceSnapshot?> opening = session.OpenAsync(root, TestContext.Current.CancellationToken);
            await importer.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(session.State.Current);
            Assert.True(session.State.IsStale);
            Assert.True(session.IsImporting);
            importer.Release.TrySetResult();
            WorkspaceSnapshot? imported = await opening;
            Assert.Same(imported, session.State.Current);
            Assert.False(session.State.IsStale);
        }
        finally
        {
            session.Dispose();
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Delete(root);
        }
    }

    [Fact]
    public async Task NoKeptModelIsReadBeforeTrust()
    {
        string root = Directory.CreateTempSubdirectory("wayfarer-warm-").FullName;
        WorkspaceTrust.Trust(root);
        string cache = KeepModel(root);
        WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root) ?? root);
        CountingImporter importer = new();
        WorkspaceImportSession session = new(importer, cache);
        try
        {
            Assert.Null(WorkspaceModelCache.Read(cache, root));
            await Assert.ThrowsAsync<UntrustedFolderException>(() => session.OpenAsync(root, TestContext.Current.CancellationToken));
            Assert.Null(session.State.Current);
            Assert.Equal(0, importer.Calls);
        }
        finally
        {
            session.Dispose();
            Delete(root);
        }
    }

    [Fact]
    public async Task AChangedBuildInputAsksForAReimportOnceAfterTheDebounce()
    {
        string root = Directory.CreateTempSubdirectory("wayfarer-watch-").FullName;
        WorkspaceTrust.Trust(root);
        WorkspaceImportSession session = new(new CountingImporter(), KeepModel(root), TimeSpan.FromMilliseconds(100));
        int changes = 0;
        TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.InputsChanged += () =>
        {
            Interlocked.Increment(ref changes);
            changed.TrySetResult();
        };
        try
        {
            await session.OpenAsync(root, TestContext.Current.CancellationToken);
            string pom = Path.Combine(root, "pom.xml");
            // Writing the same bytes back with the same stamp is no change.
            DateTime stamp = File.GetLastWriteTimeUtc(pom);
            await File.WriteAllTextAsync(pom, await File.ReadAllTextAsync(pom, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(pom, stamp);
            await Task.Delay(400, TestContext.Current.CancellationToken);
            Assert.Equal(0, Volatile.Read(ref changes));
            for (int i = 0; i < 3; i++)
            {
                await File.AppendAllTextAsync(pom, $"<!-- {i} -->", TestContext.Current.CancellationToken);
            }
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Task.Delay(400, TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref changes));
        }
        finally
        {
            session.Dispose();
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Delete(root);
        }
    }

    [Fact]
    public async Task WorkWaitsForTheImportRunningForTheFolder()
    {
        string root = Directory.CreateTempSubdirectory("wayfarer-idle-").FullName;
        WorkspaceTrust.Trust(root);
        GatedImporter importer = new();
        WorkspaceImportSession session = new(importer);
        try
        {
            Task<WorkspaceSnapshot?> import = session.RefreshAsync(root, TestContext.Current.CancellationToken);
            await importer.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            Task idle = session.WhenIdleAsync(TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(idle.IsCompleted);
            using CancellationTokenSource cancel = new();
            Task cancelled = session.WhenIdleAsync(cancel.Token);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            importer.Release.TrySetResult();
            await import;
            await idle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(session.IsImporting);
        }
        finally
        {
            session.Dispose();
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Delete(root);
        }
    }

    /// <summary>Writes a pom.xml into <paramref name="root"/> and keeps a model of it, evaluated from that file, in a cache
    /// file beside the folder; returns the cache file.</summary>
    private static string KeepModel(string root)
    {
        string pom = Path.Combine(root, "pom.xml");
        File.WriteAllText(pom, "<project/>");
        FileInfo info = new(pom);
        HostBuildInput input = new(pom, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(), info.Length);
        HostWorkspaceResult result = new(3, root, "maven", pom, null, [], [input, new HostBuildInput(Path.Combine(root, ".mvn", "maven.config"), 0, -1)], null);
        string cache = Path.Combine(root, ".state", WorkspaceModelCache.FileName);
        WorkspaceModelCache.Write(cache, result, new HostJvm(root, "21", 21, "test"));
        return cache;
    }

    private sealed class CountingImporter : IWorkspaceImporter
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public Task<WorkspaceSnapshot> ImportAsync(string workspaceRoot, long generation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Snapshot(workspaceRoot) with { Generation = generation });
        }
    }

    private sealed class GatedImporter : IWorkspaceImporter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkspaceSnapshot> ImportAsync(string workspaceRoot, long generation, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return Snapshot(workspaceRoot) with { Generation = generation };
        }
    }

    private static async Task<bool> ExitsAsync(JvmProjectHost host)
    {
        for (int i = 0; i < 100 && !host.HasExited; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        return host.HasExited;
    }

    private static async Task EndAllHostsAsync()
    {
        foreach (JvmProjectHost host in ProjectHosts.Current)
        {
            foreach (string folder in host.ServedFolders)
            {
                await ProjectHosts.ReleaseAsync(folder);
            }
            host.End("a test wants a host of its own");
        }
    }

    internal static WorkspaceSnapshot Snapshot(string root) =>
        new(1, root, JvmBuildSystem.Maven, Path.Combine(root, "pom.xml"), new JvmToolchain(null, 21, null), []);

    internal static string FixtureFolder(string fixture) =>
        Path.Combine(Repo.Root, "tests", "fixtures", "jvm", fixture);

    /// <summary>A fixture copied to a folder of its own, without what a build of it left (build, target, .gradle, out).</summary>
    internal static string Copy(string fixture)
    {
        string source = FixtureFolder(fixture);
        string destination = Directory.CreateTempSubdirectory("wayfarer-import-").FullName;
        string[] skipped = ["build", "target", ".gradle", ".kotlin", "out"];
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => skipped.Contains(part, StringComparer.Ordinal)))
            {
                continue;
            }
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return destination;
    }

    /// <summary>Puts a marker write at the top of each wrapper script, catching any start outside ChildProcesses.</summary>
    private static void MarkWrappers(string root)
    {
        foreach (string script in new[] { "mvnw", "gradlew" }.Select(name => Path.Combine(root, name)).Where(File.Exists))
        {
            string text = File.ReadAllText(script);
            int firstLine = text.IndexOf('\n', StringComparison.Ordinal) + 1;
            File.WriteAllText(script, text[..firstLine] + "touch \"$(dirname \"$0\")/wrapper-started.marker\"\n" + text[firstLine..]);
        }
        foreach (string script in new[] { "mvnw.cmd", "gradlew.bat" }.Select(name => Path.Combine(root, name)).Where(File.Exists))
        {
            File.WriteAllText(script, "@echo started> \"%~dp0wrapper-started.marker\"\r\n" + File.ReadAllText(script));
        }
    }

    private static void Delete(string root)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
                return;
            }
            catch (Exception locked) when ((locked is IOException or UnauthorizedAccessException) && attempt < 20)
            {
                // A daemon or a host may still hold a file a moment after it was let go.
                Thread.Sleep(250);
            }
        }
    }

    private sealed class ControlledImporter : IWorkspaceImporter
    {
        private int calls;

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkspaceSnapshot> ImportAsync(string workspaceRoot, long generation, CancellationToken cancellationToken)
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

/// <summary>Compares a snapshot with a fixture's expected-model.json: module ids, names and roots, compile and runtime
/// JDKs, source sets with their roots in order, ordered classpaths as library coordinates or source sets, tasks,
/// configured mains and active profiles.</summary>
internal static class ExpectedModel
{
    public static void Compare(string expectedFile, string root, WorkspaceSnapshot snapshot)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(expectedFile));
        JsonElement expected = document.RootElement;
        Assert.Equal(expected.GetProperty("buildSystem").GetString(), snapshot.BuildSystem.ToString().ToLowerInvariant());
        List<string> expectedIds = [.. expected.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).Order(StringComparer.Ordinal)];
        Assert.Equal(expectedIds, [.. snapshot.Modules.Select(m => m.Id).Order(StringComparer.Ordinal)]);
        foreach (JsonElement module in expected.GetProperty("modules").EnumerateArray())
        {
            string id = module.GetProperty("id").GetString()!;
            JvmModule actual = snapshot.Modules.Single(m => m.Id == id);
            string where = $"module {id}";
            Assert.True(module.GetProperty("name").GetString() == actual.Name, $"{where}: name {actual.Name}");
            Assert.True(module.GetProperty("root").GetString() == Relative(root, actual.Root), $"{where}: root {actual.Root}");
            if (module.TryGetProperty("compileJdk", out JsonElement compile))
            {
                Assert.True(compile.GetInt32() == actual.CompileToolchain.LanguageVersion, $"{where}: compile JDK {actual.CompileToolchain.LanguageVersion}");
            }
            if (module.TryGetProperty("runtimeJdk", out JsonElement runtime))
            {
                Assert.True(runtime.GetInt32() == actual.RuntimeToolchain.LanguageVersion, $"{where}: runtime JDK {actual.RuntimeToolchain.LanguageVersion}");
            }
            if (module.TryGetProperty("activeProfiles", out JsonElement profiles))
            {
                Assert.Equal(Strings(profiles), actual.ActiveProfiles);
            }
            Assert.Equal(Strings(module.GetProperty("tasks")).Order(StringComparer.Ordinal), actual.Tasks.Order(StringComparer.Ordinal));
            List<string> configured = [.. module.GetProperty("mains").EnumerateArray()
                .Where(m => m.GetProperty("origin").GetString() == "configured")
                .Select(m => m.GetProperty("className").GetString()!)];
            Assert.Equal(configured, [.. actual.MainClasses.Where(m => m.Configured).Select(m => m.ClassName)]);
            List<JsonElement> sets = [.. module.GetProperty("sourceSets").EnumerateArray()];
            Assert.True(sets.Count == actual.SourceSets.Count, $"{where}: source sets {string.Join(", ", actual.SourceSets.Select(s => s.Id))}");
            foreach (JsonElement set in sets)
            {
                string setId = set.GetProperty("id").GetString()!;
                JvmSourceSet? actualSet = actual.SourceSets.FirstOrDefault(s => s.Id == setId);
                Assert.True(actualSet is not null, $"{where}: no source set {setId} among {string.Join(", ", actual.SourceSets.Select(s => s.Id))}");
                Assert.Equal(set.GetProperty("name").GetString(), actualSet.Name);
                Assert.True(set.GetProperty("test").GetBoolean() == actualSet.IsTest, $"{setId}: test {actualSet.IsTest}");
                Assert.Equal(
                    [.. set.GetProperty("roots").EnumerateArray().Select(r => r.GetProperty("path").GetString() + " " + Kind(r))],
                    [.. actualSet.Roots.Select(r => Relative(root, r.Path) + " " + Kind(r.Kind))]);
                Assert.Equal(Strings(set.GetProperty("compileClasspath")), Entries(actualSet.CompileClasspath));
                Assert.Equal(Strings(set.GetProperty("runtimeClasspath")), Entries(actualSet.RuntimeClasspath));
            }
        }
    }

    private static string Kind(JsonElement root) =>
        root.TryGetProperty("generated", out JsonElement generated) && generated.GetBoolean() ? "generated" : root.GetProperty("kind").GetString()!;

    private static string Kind(JvmSourceKind kind) => kind switch
    {
        JvmSourceKind.Generated => "generated",
        JvmSourceKind.MainKotlin or JvmSourceKind.TestKotlin => "kotlin",
        JvmSourceKind.MainResources or JvmSourceKind.TestResources => "resources",
        _ => "java",
    };

    /// <summary>The entries as the expected model writes them: a source set's several output folders count once.</summary>
    private static List<string> Entries(IReadOnlyList<JvmClasspathEntry> entries)
    {
        List<string> said = [];
        foreach (JvmClasspathEntry entry in entries)
        {
            string word = entry.ModuleRef is { } module ? "src:" + module : "lib:" + entry.Coordinates;
            if (entry.ModuleRef is null || said.Count == 0 || said[^1] != word)
            {
                said.Add(word);
            }
        }
        return said;
    }

    private static List<string> Strings(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetString()!)];

    private static string Relative(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.Length == 0 ? "." : relative;
    }
}

internal static class SnapshotTestExtensions
{
    /// <summary>The import toolchain the snapshot's modules record (one per workspace).</summary>
    public static JvmToolchain ImportToolchain(this WorkspaceSnapshot snapshot) =>
        snapshot.Modules.FirstOrDefault()?.ImportToolchain ?? new JvmToolchain(null, null, null);
}
