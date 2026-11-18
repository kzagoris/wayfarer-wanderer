using System.Text.Json;
using System.Text.RegularExpressions;
using WayfarerWanderer.Jvm;
using Passenger.Core;

namespace WayfarerWanderer.Tests;

[Collection(JvmTrustCollection.Name)]
public sealed class NewProjectTests
{
    public static TheoryData<NewProjectKind, NewProjectLanguage, NewProjectBuild> Templates()
    {
        TheoryData<NewProjectKind, NewProjectLanguage, NewProjectBuild> data = new();
        foreach (NewProjectKind kind in Enum.GetValues<NewProjectKind>())
        {
            foreach (NewProjectLanguage language in Enum.GetValues<NewProjectLanguage>())
            {
                foreach (NewProjectBuild build in Enum.GetValues<NewProjectBuild>())
                {
                    data.Add(kind, language, build);
                }
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task GeneratesCompleteStarterAndNamedLaunch(
        NewProjectKind kind,
        NewProjectLanguage language,
        NewProjectBuild build)
    {
        using TestFolder folder = new();
        ProjectGenerationOptions options = Options(folder.Path, kind, language, build);
        ProjectGenerationResult result = await ProjectGenerator.GenerateAsync(
            options,
            TestContext.Current.CancellationToken);

        string destination = result.Destination;
        string extension = language == NewProjectLanguage.Java ? "java" : "kt";
        string entry = kind == NewProjectKind.SpringBoot ? "Application" : "App";
        string sourceRoot = language == NewProjectLanguage.Java ? "src/main/java" : "src/main/kotlin";
        string testRoot = language == NewProjectLanguage.Java ? "src/test/java" : "src/test/kotlin";
        Assert.True(File.Exists(Path.Combine(destination, sourceRoot, "com", "example", "wanderer", $"{entry}.{extension}")));
        Assert.True(File.Exists(Path.Combine(destination, testRoot, "com", "example", "wanderer",
            kind == NewProjectKind.SpringBoot ? $"ApplicationTest.{extension}" : $"AppTest.{extension}")));
        Assert.True(File.Exists(Path.Combine(destination, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(destination, "README.md")));
        Assert.True(File.Exists(Path.Combine(destination, "THIRD_PARTY_NOTICES.md")));
        Assert.False(Directory.Exists(Path.Combine(destination, ".git")));

        string descriptor = File.ReadAllText(Path.Combine(destination,
            build == NewProjectBuild.Maven ? "pom.xml" : "build.gradle.kts"));
        Assert.Contains(language == NewProjectLanguage.Java ? JdkMajor().ToString() : "2.4.20", descriptor);
        if (build == NewProjectBuild.Gradle)
        {
            Assert.True(File.Exists(Path.Combine(destination, "settings.gradle.kts")));
            Assert.True(File.Exists(Path.Combine(destination, "gradle", "wrapper", "gradle-wrapper.jar")));
            Assert.True(File.Exists(Path.Combine(destination, "gradle", "wrapper", "gradle-wrapper.properties")));
            Assert.True(File.Exists(Path.Combine(destination, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew")));
            Assert.Equal("PK"u8.ToArray(),
                File.ReadAllBytes(Path.Combine(destination, "gradle", "wrapper", "gradle-wrapper.jar")).Take(2));
        }
        else
        {
            Assert.True(File.Exists(Path.Combine(destination, ".mvn", "wrapper", "maven-wrapper.properties")));
            Assert.True(File.Exists(Path.Combine(destination, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw")));
            Assert.Contains("3.9.11", File.ReadAllText(Path.Combine(destination, ".mvn", "wrapper", "maven-wrapper.properties")));
        }
        if (kind == NewProjectKind.SpringBoot)
        {
            Assert.Contains("3.5.7", descriptor);
            Assert.Contains("@GetMapping", File.ReadAllText(Path.Combine(destination, sourceRoot,
                "com", "example", "wanderer", $"Application.{extension}")));
        }
        if (kind == NewProjectKind.SpringBoot && language == NewProjectLanguage.Kotlin)
        {
            Assert.Contains("spring", descriptor);
        }
        Assert.Contains("retry", File.ReadAllText(Path.Combine(destination, "README.md")), StringComparison.OrdinalIgnoreCase);

        using JsonDocument launch = JsonDocument.Parse(File.ReadAllText(result.LaunchConfigurationPath));
        JsonElement launchElement = launch.RootElement;
        Assert.Equal("Run starter", launchElement.GetProperty("name").GetString());
        Assert.Equal("com.example.wanderer." + entry + (language == NewProjectLanguage.Kotlin ? "Kt" : ""),
            launchElement.GetProperty("mainClass").GetString());
        LaunchConfigurationStore store = new(Path.Combine(destination, ".wayfarer", "launches"));
        Assert.Single(await store.ListAsync(TestContext.Current.CancellationToken));
        using JsonDocument provenance = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(destination, ".wayfarer", "template.json")));
        Assert.NotEmpty(provenance.RootElement.GetProperty("wrappers").EnumerateObject());
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task GeneratedStarterBuildsAndRunsItsTest(
        NewProjectKind kind,
        NewProjectLanguage language,
        NewProjectBuild build)
    {
        using TestFolder folder = new();
        ProjectGenerationResult generated = await ProjectGenerator.GenerateAsync(
            Options(folder.Path, kind, language, build),
            TestContext.Current.CancellationToken);
        WorkspaceTrust.Trust(generated.Destination);
        try
        {
            WorkspaceSnapshot snapshot = await new WorkspaceImporter().ImportAsync(
                generated.Destination,
                1,
                TestContext.Current.CancellationToken);
            JvmModule module = Assert.Single(snapshot.Modules);
            JvmBuildRequest request = new(generated.Destination, module.Id, "test", []);
            ProcessResult result = await new JvmBuild().RunAsync(
                snapshot,
                request,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(
                result.ExitCode == 0,
                "The generated wrapper build failed:\n" + string.Join(Environment.NewLine, result.Output.Select(line => line.Line)));
            Assert.Contains(result.Output, line => line.Line.Contains("BUILD SUCCESS", StringComparison.OrdinalIgnoreCase) ||
                                                 line.Line.Contains("BUILD SUCCESSFUL", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            WorkspaceTrust.TakeBack(generated.Destination);
        }
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task GeneratedStarterLaunchesWithoutEditingBuildFiles(
        NewProjectKind kind,
        NewProjectLanguage language,
        NewProjectBuild build)
    {
        using TestFolder folder = new();
        ProjectGenerationResult generated = await ProjectGenerator.GenerateAsync(
            Options(folder.Path, kind, language, build),
            TestContext.Current.CancellationToken);
        WorkspaceTrust.Trust(generated.Destination);
        try
        {
            WorkspaceSnapshot snapshot = await new WorkspaceImporter().ImportAsync(
                generated.Destination,
                1,
                TestContext.Current.CancellationToken);
            JvmModule module = Assert.Single(snapshot.Modules);
            ProcessResult built = await new JvmBuild().RunAsync(
                snapshot,
                new JvmBuildRequest(generated.Destination, module.Id, "classes",
                    build == NewProjectBuild.Gradle ? ["--no-daemon"] : []),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(
                built.ExitCode == 0,
                "The generated wrapper build failed:\n" + string.Join(Environment.NewLine, built.Output.Select(line => line.Line)));
            JvmLaunchConfiguration configuration = new()
            {
                Name = "Acceptance",
                Module = module.Id,
                MainClass = module.MainClasses[0].ClassName,
                WorkingDirectory = module.Root,
                JavaHome = JdkHome(),
                BuildArguments = build == NewProjectBuild.Gradle ? ["--no-daemon"] : [],
                Arguments = kind == NewProjectKind.SpringBoot ? ["--server.port=0"] : []
            };
            using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            TaskCompletionSource<int> port = new(TaskCreationOptions.RunContinuationsAsynchronously);
            List<string> lines = [];
            Task<ProcessResult> running = new JvmLaunch().RunAsync(
                snapshot,
                configuration,
                line =>
                {
                    lock (lines)
                    {
                        lines.Add(line.Line);
                    }
                    Match match = Regex.Match(line.Line, @"Tomcat started on port (\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int startedPort))
                    {
                        port.TrySetResult(startedPort);
                    }
                },
                stopping.Token);
            if (kind == NewProjectKind.Application)
            {
                ProcessResult result = await running;
                Assert.True(
                    result.ExitCode == 0,
                    "The generated launch failed:\n" + string.Join(Environment.NewLine, result.Output.Select(line => line.Line)));
                Assert.Contains(lines, line => line.Contains("Hello from Wanderer Starter!", StringComparison.Ordinal));
            }
            else
            {
                try
                {
                    int actualPort = await port.Task.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);
                    using HttpClient client = new();
                    string response = await client.GetStringAsync($"http://127.0.0.1:{actualPort}/", TestContext.Current.CancellationToken);
                    Assert.Equal("Hello from Wanderer Starter!", response);
                }
                catch (TimeoutException failure)
                {
                    throw new TimeoutException(
                        "The generated service did not become ready:\n" + string.Join(Environment.NewLine, lines),
                        failure);
                }
                finally
                {
                    await stopping.CancelAsync();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
                }
            }
        }
        finally
        {
            WorkspaceTrust.TakeBack(generated.Destination);
        }
    }

    [Theory]
    [InlineData("GroupId", "Upper")]
    [InlineData("GroupId", "bad..group")]
    [InlineData("ArtifactId", "-bad")]
    [InlineData("ArtifactId", "with space")]
    [InlineData("PackageName", "example..demo")]
    [InlineData("PackageName", "example.class")]
    [InlineData("PackageName", "123.example")]
    public void RejectsInvalidCoordinates(string property, string invalid)
    {
        using TestFolder folder = new();
        ProjectGenerationOptions options = Options(folder.Path) with
        {
            GroupId = property == "GroupId" ? invalid : "com.example",
            ArtifactId = property == "ArtifactId" ? invalid : "wanderer",
            PackageName = property == "PackageName" ? invalid : "com.example.wanderer"
        };
        Assert.Throws<ProjectGenerationException>(() => ProjectGenerator.Validate(options));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Fact]
    public void RejectsJreAndOldJdkWithoutStartingAnything()
    {
        using TestFolder folder = new();
        string jdk = Path.Combine(folder.Path, "not-a-jdk");
        Directory.CreateDirectory(jdk);
        ProjectGenerationException missing = Assert.Throws<ProjectGenerationException>(
            () => ProjectGenerator.Validate(Options(folder.Path) with { JdkHome = jdk }));
        Assert.Contains("complete JDK", missing.Message);

        Directory.CreateDirectory(Path.Combine(jdk, "bin"));
        File.WriteAllText(Path.Combine(jdk, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), "");
        File.WriteAllText(Path.Combine(jdk, "bin", OperatingSystem.IsWindows() ? "javac.exe" : "javac"), "");
        File.WriteAllText(Path.Combine(jdk, "release"), "JAVA_VERSION=\"17.0.10\"");
        ProjectGenerationException old = Assert.Throws<ProjectGenerationException>(
            () => ProjectGenerator.Validate(Options(folder.Path) with { JdkHome = jdk }));
        Assert.Contains("21 or newer", old.Message);
    }

    [Fact]
    public async Task CancellationPreservesEmptyDestinationAndRemovesOwnedStaging()
    {
        using TestFolder folder = new();
        string destination = Path.Combine(folder.Path, "cancelled");
        Directory.CreateDirectory(destination);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        ProjectGenerationOptions options = Options(folder.Path) with
        {
            Destination = destination,
            BeforePublish = _ => cancellation.Cancel()
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProjectGenerator.GenerateAsync(options, cancellation.Token));
        Assert.True(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(folder.Path),
            path => path.Contains(".staging", StringComparison.Ordinal) || path.Contains(".lock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteFailureRemovesOnlyStagingAndPreservesDestination()
    {
        using TestFolder folder = new();
        string destination = Path.Combine(folder.Path, "write-failure");
        Directory.CreateDirectory(destination);
        ProjectGenerationOptions options = Options(folder.Path) with
        {
            Destination = destination,
            BeforePublish = _ => throw new IOException("simulated write failure")
        };

        await Assert.ThrowsAsync<ProjectGenerationException>(() =>
            ProjectGenerator.GenerateAsync(options, TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(folder.Path),
            path => path.Contains(".staging", StringComparison.Ordinal) || path.Contains(".lock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonemptyDestinationIsNeverChanged()
    {
        using TestFolder folder = new();
        string destination = Path.Combine(folder.Path, "occupied");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "original");

        ProjectGenerationException failure = await Assert.ThrowsAsync<ProjectGenerationException>(() =>
            ProjectGenerator.GenerateAsync(Options(folder.Path) with { Destination = destination },
                TestContext.Current.CancellationToken));

        Assert.Contains("not empty", failure.Message);
        Assert.Equal("original", File.ReadAllText(Path.Combine(destination, "keep.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task EntryArrivingDuringNewDestinationPublicationWins()
    {
        using TestFolder folder = new();
        string destination = Path.Combine(folder.Path, "raced");
        ProjectGenerationOptions options = Options(folder.Path) with
        {
            Destination = destination,
            BeforePublish = _ =>
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "concurrent.txt"), "other writer");
            }
        };

        await Assert.ThrowsAsync<ProjectGenerationException>(() =>
            ProjectGenerator.GenerateAsync(options, TestContext.Current.CancellationToken));
        Assert.Equal("other writer", File.ReadAllText(Path.Combine(destination, "concurrent.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task EntryArrivingInEmptyDestinationBeforePublicationWins()
    {
        using TestFolder folder = new();
        string destination = Path.Combine(folder.Path, "empty-then-raced");
        Directory.CreateDirectory(destination);
        ProjectGenerationOptions options = Options(folder.Path) with
        {
            Destination = destination,
            BeforePublish = _ => File.WriteAllText(Path.Combine(destination, "concurrent.txt"), "other writer")
        };

        await Assert.ThrowsAsync<ProjectGenerationException>(() =>
            ProjectGenerator.GenerateAsync(options, TestContext.Current.CancellationToken));
        Assert.Equal("other writer", File.ReadAllText(Path.Combine(destination, "concurrent.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task SupportsSpacesUnicodeAndProjectNameEscaping()
    {
        using TestFolder folder = new();
        string destination = Path.Combine(folder.Path, "Τρόπος ζωής and spaces");
        ProjectGenerationResult result = await ProjectGenerator.GenerateAsync(Options(folder.Path) with
        {
            ProjectName = "Καλημέρα \"$κόσμε\"",
            Destination = destination,
            PackageName = "ελπίδα.demo"
        }, TestContext.Current.CancellationToken);

        string source = File.ReadAllText(Path.Combine(result.Destination, "src", "main", "kotlin", "ελπίδα", "demo", "App.kt"));
        Assert.Contains("Καλημέρα \\\"\\$κόσμε\\\"", source);
        Assert.True(File.Exists(Path.Combine(destination, "build.gradle.kts")));
    }

    [Fact]
    public async Task GenerationDefersGitRepositoryToolExecutionUntilExplicitTrust()
    {
        using TestFolder folder = new();
        string repository = PathNames.Canonical(Path.Combine(folder.Path, "repository"));
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        string destination = Path.Combine(repository, "new-project");
        using IDisposable ask = WorkspaceTrust.Ask(PathNames.Canonical(repository));
        Assert.False(WorkspaceTrust.Trusts(destination));

        ProjectGenerationResult result = await ProjectGenerator.GenerateAsync(
            Options(folder.Path) with { Destination = destination },
            TestContext.Current.CancellationToken);
        Assert.True(result.RequiresWorkspaceTrust);
        Assert.Throws<ProjectGenerationException>(() => ProjectGenerator.CheckWorkspaceTrust(destination));

        ask.Dispose();
        WorkspaceTrust.Trust(repository);
        ProjectGenerationResult trustedResult = await ProjectGenerator.GenerateAsync(
            Options(folder.Path) with { Destination = destination + "-next" },
            TestContext.Current.CancellationToken);
        Assert.False(trustedResult.RequiresWorkspaceTrust);
        ProjectGenerator.CheckWorkspaceTrust(destination);
        WorkspaceTrust.TakeBack(repository);
    }

    [Fact]
    public async Task WindowsLongDestinationPublishesAndCleans()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using TestFolder folder = new();
        string destination = Path.Combine(folder.Path, string.Join(Path.DirectorySeparatorChar,
            Enumerable.Repeat("long-folder-name", 15)));

        ProjectGenerationResult result = await ProjectGenerator.GenerateAsync(
            Options(folder.Path) with { Destination = destination },
            TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(result.Destination));
        Assert.True(File.Exists(Path.Combine(result.Destination, "build.gradle.kts")));
    }

    private static ProjectGenerationOptions Options(
        string parent,
        NewProjectKind kind = NewProjectKind.Application,
        NewProjectLanguage language = NewProjectLanguage.Kotlin,
        NewProjectBuild build = NewProjectBuild.Gradle) => new()
    {
        ProjectName = "Wanderer Starter",
        Destination = Path.Combine(parent, $"{kind}-{language}-{build}".ToLowerInvariant()),
        GroupId = "com.example",
        ArtifactId = "wanderer-starter",
        PackageName = "com.example.wanderer",
        JdkHome = JdkHome(),
        Kind = kind,
        Language = language,
        Build = build,
        LaunchName = "Run starter"
    };

    private static string JdkHome()
    {
        string? home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string javac = Path.Combine(folder, OperatingSystem.IsWindows() ? "javac.exe" : "javac");
            if (File.Exists(javac))
            {
                return Path.GetDirectoryName(Path.GetDirectoryName(javac)!)!;
            }
        }
        throw new InvalidOperationException("The New Project tests require a local JDK 21 or newer in JAVA_HOME or on PATH.");
    }

    private static int JdkMajor()
    {
        string? java = File.ReadLines(Path.Combine(JdkHome(), "release"))
            .FirstOrDefault(line => line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal));
        return int.Parse(java![14..].Trim().Trim('"').Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class TestFolder : IDisposable
    {
        public string Path { get; }

        public TestFolder() => Path = Directory.CreateTempSubdirectory("wayfarer-new-project-").FullName;

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
