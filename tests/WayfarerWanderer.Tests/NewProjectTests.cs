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
                foreach (NewProjectBuild build in Enum.GetValues<NewProjectBuild>().Where(build =>
                             ProjectGenerator.Supports(kind, language, build)))
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
        if (build == NewProjectBuild.IntelliJ)
        {
            await AssertIntelliJModuleAsync(result, options);
            return;
        }
        string sourceRoot = language == NewProjectLanguage.Java ? "src/main/java" : "src/main/kotlin";
        string testRoot = language == NewProjectLanguage.Java ? "src/test/java" : "src/test/kotlin";
        Assert.True(File.Exists(Path.Combine(destination, sourceRoot, "com", "example", "wanderer", $"{entry}.{extension}")));
        Assert.True(File.Exists(Path.Combine(destination, testRoot, "com", "example", "wanderer",
            kind == NewProjectKind.SpringBoot ? $"ApplicationTest.{extension}" : $"AppTest.{extension}")));
        Assert.True(File.Exists(Path.Combine(destination, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(destination, "README.md")));
        Assert.True(File.Exists(Path.Combine(destination, "THIRD_PARTY_NOTICES.md")));
        Assert.False(Directory.Exists(Path.Combine(destination, ".git")));
        Assert.Equal(JdkRegistry.Current.Add(options.JdkHome).Home, ProjectJdk.Recorded(destination));
        AssertAsksForTheChosenJdk(destination, options);
        string[] gitignore = File.ReadAllLines(Path.Combine(destination, ".gitignore"));
        Assert.Contains(".wayfarer/*", gitignore);
        Assert.Contains("!.wayfarer/project.json", gitignore);

        string descriptor = File.ReadAllText(Path.Combine(destination,
            build == NewProjectBuild.Maven ? "pom.xml" : "build.gradle.kts"));
        Assert.Contains(language == NewProjectLanguage.Java ? JdkMajor(options.JdkHome)!.Value.ToString() : ProjectGenerator.KotlinVersion, descriptor);
        if (build == NewProjectBuild.Gradle)
        {
            Assert.True(File.Exists(Path.Combine(destination, "settings.gradle.kts")));
            Assert.False(File.Exists(Path.Combine(destination, "gradle.properties")));
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
            Assert.Contains(ProjectGenerator.SpringBootVersion, descriptor);
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
            WorkspaceSnapshot snapshot = await new ProjectHostImporter().ImportAsync(
                generated.Destination,
                1,
                TestContext.Current.CancellationToken);
            JvmModule module = Assert.Single(snapshot.Modules);
            // An IntelliJ module has no JUnit build: javac compiling it is its whole build.
            JvmBuildRequest request = new(generated.Destination, module.Id, build == NewProjectBuild.IntelliJ ? "classes" : "test", []);
            ProcessResult result = await new JvmBuild().RunAsync(
                snapshot,
                request,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(
                result.ExitCode == 0,
                "The generated wrapper build failed:\n" + string.Join(Environment.NewLine, result.Output.Select(line => line.Line)));
            if (build == NewProjectBuild.IntelliJ)
            {
                Assert.Equal(JvmBuildSystem.Idea, snapshot.BuildSystem);
                Assert.True(File.Exists(Path.Combine(generated.Destination, "out", "production", "wanderer-starter",
                    "com", "example", "wanderer", "App.class")));
                return;
            }
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
            WorkspaceSnapshot snapshot = await new ProjectHostImporter().ImportAsync(
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
                JavaHome = JdkHome(language),
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
    public void KotlinStarterRefusesJdkNewerThanKotlinTargets()
    {
        using TestFolder folder = new();
        string jdk = Path.Combine(folder.Path, "newer-jdk");
        Directory.CreateDirectory(Path.Combine(jdk, "bin"));
        File.WriteAllText(Path.Combine(jdk, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), "");
        File.WriteAllText(Path.Combine(jdk, "bin", OperatingSystem.IsWindows() ? "javac.exe" : "javac"), "");
        File.WriteAllText(Path.Combine(jdk, "release"), $"JAVA_VERSION=\"{ProjectGenerator.KotlinHighestJdk + 1}\"");
        string parent = Path.Combine(folder.Path, "projects");
        ProjectGenerationOptions kotlin = Options(parent, language: NewProjectLanguage.Kotlin, jdk: jdk);
        ProjectGenerationOptions java = Options(parent, language: NewProjectLanguage.Java, jdk: jdk);

        ProjectGenerationException refused = Assert.Throws<ProjectGenerationException>(() => ProjectGenerator.Validate(kotlin));
        Assert.Contains($"Choose JDK {ProjectGenerator.KotlinHighestJdk} or older", refused.Message);
        ProjectGenerator.Validate(java);
        Assert.False(Directory.Exists(parent));
    }

    [Fact]
    public void RejectsJreAndOldJdkWithoutStartingAnything()
    {
        using TestFolder folder = new();
        string jdk = Path.Combine(folder.Path, "not-a-jdk");
        Directory.CreateDirectory(jdk);
        ProjectGenerationException missing = Assert.Throws<ProjectGenerationException>(
            () => ProjectGenerator.Validate(Options(folder.Path, jdk: jdk)));
        Assert.Contains("complete JDK", missing.Message);

        Directory.CreateDirectory(Path.Combine(jdk, "bin"));
        File.WriteAllText(Path.Combine(jdk, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), "");
        File.WriteAllText(Path.Combine(jdk, "bin", OperatingSystem.IsWindows() ? "javac.exe" : "javac"), "");
        File.WriteAllText(Path.Combine(jdk, "release"), "JAVA_VERSION=\"17.0.10\"");
        ProjectGenerationException old = Assert.Throws<ProjectGenerationException>(
            () => ProjectGenerator.Validate(Options(folder.Path, jdk: jdk)));
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

    /// <summary>An IntelliJ module starter: its .iml and .idea beside src, no wrapper or build file, its own README and
    /// .gitignore in place of the common ones, and a launch of the module's App.</summary>
    private static async Task AssertIntelliJModuleAsync(ProjectGenerationResult result, ProjectGenerationOptions options)
    {
        string destination = result.Destination;
        bool compact = JdkMajor(options.JdkHome) >= ProjectGenerator.InstanceMainJdk;
        Assert.True(File.Exists(compact
            ? Path.Combine(destination, "src", "App.java")
            : Path.Combine(destination, "src", "com", "example", "wanderer", "App.java")));
        Assert.Equal("wanderer-starter.iml", Path.GetFileName(Directory.GetFiles(destination, "*.iml").SingleOrDefault()));
        Assert.Contains("JAVA_MODULE", File.ReadAllText(Path.Combine(destination, "wanderer-starter.iml")));
        Assert.Contains($"JDK_{JdkMajor(options.JdkHome)}", File.ReadAllText(Path.Combine(destination, ".idea", "misc.xml")));
        Assert.Contains("$PROJECT_DIR$/wanderer-starter.iml", File.ReadAllText(Path.Combine(destination, ".idea", "modules.xml")));
        foreach (string absent in (string[])["pom.xml", "build.gradle.kts", "mvnw", "mvnw.cmd", "gradlew", "gradlew.bat"])
        {
            Assert.False(File.Exists(Path.Combine(destination, absent)), absent);
        }
        string[] ignored = File.ReadAllLines(Path.Combine(destination, ".gitignore"));
        Assert.Contains(".wayfarer/*", ignored);
        Assert.Contains("!.wayfarer/project.json", ignored);
        Assert.DoesNotContain(".idea/", ignored);
        Assert.DoesNotContain("*.iml", ignored);
        string readme = File.ReadAllText(Path.Combine(destination, "README.md"));
        Assert.Contains("IntelliJ", readme);
        Assert.DoesNotContain("mvnw", readme);
        Assert.True(File.Exists(Path.Combine(destination, "THIRD_PARTY_NOTICES.md")));
        Assert.Equal(JdkRegistry.Current.Add(options.JdkHome).Home, ProjectJdk.Recorded(destination));
        AssertAsksForTheChosenJdk(destination, options);
        Jdk chosen = JdkRegistry.Current.Add(options.JdkHome);
        Assert.Contains($"project-jdk-name=\"{chosen.Name}\"", File.ReadAllText(Path.Combine(destination, ".idea", "misc.xml")));

        using JsonDocument launch = JsonDocument.Parse(File.ReadAllText(result.LaunchConfigurationPath));
        Assert.Equal("wanderer-starter", launch.RootElement.GetProperty("module").GetString());
        Assert.Equal(compact ? "App" : "com.example.wanderer.App", launch.RootElement.GetProperty("mainClass").GetString());
        LaunchConfigurationStore store = new(Path.Combine(destination, ".wayfarer", "launches"));
        Assert.Single(await store.ListAsync(TestContext.Current.CancellationToken));
        using JsonDocument provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, ".wayfarer", "template.json")));
        Assert.Empty(provenance.RootElement.GetProperty("wrappers").EnumerateObject());
    }

    /// <summary>The project file asks for the chosen JDK by requirement (schema 2), under its name in the JDK table.</summary>
    private static void AssertAsksForTheChosenJdk(string destination, ProjectGenerationOptions options)
    {
        Jdk chosen = JdkRegistry.Current.Add(options.JdkHome);
        using JsonDocument project = JsonDocument.Parse(File.ReadAllText(ProjectJdk.FileOf(destination)));
        Assert.Equal(2, project.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(new JdkRequirement(chosen.Name, chosen.Feature, JdkRegistry.Distribution(chosen.Vendor), JdkRequirement.Project), ProjectJdk.Requirement(destination));
        Assert.Equal(JsonValueKind.Object, project.RootElement.GetProperty("jdk").ValueKind);
    }

    /// <summary>On JDK 25 or newer an IntelliJ module's App is a JEP 512 compact source file, src/App.java with only an
    /// instance main printing with IO and launched as App; on an older JDK it is the classic static main in the chosen
    /// package. Either form builds, is found as the module's main and runs.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IntelliJModuleAppTakesTheChosenJdksMainForm(bool modern)
    {
        bool Wanted(string home) => JdkMajor(home) is int major && major >= ProjectGenerator.MinimumJdk &&
                                    (major >= ProjectGenerator.InstanceMainJdk) == modern;
        string? jdk = PreferredJdks().Concat(InstalledJdks()).FirstOrDefault(Wanted);
        if (jdk is null)
        {
            Assert.Skip(modern
                ? $"Needs a local JDK {ProjectGenerator.InstanceMainJdk} or newer."
                : $"Needs a local JDK {ProjectGenerator.MinimumJdk} to {ProjectGenerator.InstanceMainJdk - 1}.");
        }
        using TestFolder folder = new();
        ProjectGenerationResult generated = await ProjectGenerator.GenerateAsync(
            Options(folder.Path, NewProjectKind.Application, NewProjectLanguage.Java, NewProjectBuild.IntelliJ, jdk),
            TestContext.Current.CancellationToken);
        string src = Path.Combine(generated.Destination, "src");
        string[] sources = Directory.GetFiles(src, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(src, file).Replace(Path.DirectorySeparatorChar, '/')).ToArray();
        string expectedMain = modern ? "App" : "com.example.wanderer.App";
        if (modern)
        {
            Assert.Equal(["App.java"], sources);
            string app = File.ReadAllText(Path.Combine(src, "App.java")).ReplaceLineEndings("\n");
            Assert.Equal("void main() {\n    IO.println(\"Hello from Wanderer Starter!\");\n}\n", app);
            Assert.Contains("compact source file", File.ReadAllText(Path.Combine(generated.Destination, "README.md")));
        }
        else
        {
            Assert.Equal(["com/example/wanderer/App.java"], sources);
            string app = File.ReadAllText(Path.Combine(src, "com", "example", "wanderer", "App.java"));
            Assert.StartsWith("package com.example.wanderer;", app);
            Assert.Contains("public static void main(String[] args)", app);
            Assert.Contains("System.out.println(\"Hello from Wanderer Starter!\");", app);
            Assert.Contains("com.example.wanderer.App", File.ReadAllText(Path.Combine(generated.Destination, "README.md")));
        }
        using (JsonDocument launch = JsonDocument.Parse(File.ReadAllText(generated.LaunchConfigurationPath)))
        {
            Assert.Equal(expectedMain, launch.RootElement.GetProperty("mainClass").GetString());
        }

        WorkspaceTrust.Trust(generated.Destination);
        try
        {
            WorkspaceSnapshot snapshot = await new ProjectHostImporter().ImportAsync(
                generated.Destination, 1, TestContext.Current.CancellationToken);
            JvmModule module = Assert.Single(snapshot.Modules);
            ProcessResult built = await new JvmBuild().RunAsync(
                snapshot,
                new JvmBuildRequest(generated.Destination, module.Id, "classes", []),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(built.ExitCode == 0,
                "The generated module failed to build:\n" + string.Join(Environment.NewLine, built.Output.Select(line => line.Line)));
            Assert.Equal(expectedMain, module.MainClasses[0].ClassName);
            List<string> lines = [];
            ProcessResult ran = await new JvmLaunch().RunAsync(
                snapshot,
                new JvmLaunchConfiguration
                {
                    Name = "Acceptance",
                    Module = module.Id,
                    MainClass = module.MainClasses[0].ClassName,
                    WorkingDirectory = module.Root,
                    JavaHome = jdk
                },
                line =>
                {
                    lock (lines)
                    {
                        lines.Add(line.Line);
                    }
                },
                TestContext.Current.CancellationToken);
            Assert.True(ran.ExitCode == 0,
                "The generated launch failed:\n" + string.Join(Environment.NewLine, ran.Output.Select(line => line.Line)));
            Assert.Contains(lines, line => line.Contains("Hello from Wanderer Starter!", StringComparison.Ordinal));
        }
        finally
        {
            WorkspaceTrust.TakeBack(generated.Destination);
        }
    }

    [Theory]
    [InlineData(NewProjectKind.Application, NewProjectLanguage.Kotlin)]
    [InlineData(NewProjectKind.SpringBoot, NewProjectLanguage.Java)]
    [InlineData(NewProjectKind.SpringBoot, NewProjectLanguage.Kotlin)]
    public void IntelliJModuleIsPlainJavaApplicationOnly(NewProjectKind kind, NewProjectLanguage language)
    {
        using TestFolder folder = new();
        Assert.False(ProjectGenerator.Supports(kind, language, NewProjectBuild.IntelliJ));
        ProjectGenerationException refused = Assert.Throws<ProjectGenerationException>(() =>
            ProjectGenerator.Validate(Options(folder.Path, kind, language, NewProjectBuild.IntelliJ)));
        Assert.Contains("Maven or Gradle", refused.Message);
    }

    private static ProjectGenerationOptions Options(
        string parent,
        NewProjectKind kind = NewProjectKind.Application,
        NewProjectLanguage language = NewProjectLanguage.Kotlin,
        NewProjectBuild build = NewProjectBuild.Gradle,
        string? jdk = null) => new()
    {
        ProjectName = "Wanderer Starter",
        Destination = Path.Combine(parent, $"{kind}-{language}-{build}".ToLowerInvariant()),
        GroupId = "com.example",
        ArtifactId = "wanderer-starter",
        PackageName = "com.example.wanderer",
        JdkHome = jdk ?? JdkHome(language),
        Kind = kind,
        Language = language,
        Build = build,
        LaunchName = "Run starter"
    };

    /// <summary>A local JDK a starter in <paramref name="language"/> accepts: JAVA_HOME's, then PATH's, then the newest
    /// in range among the usual install folders. Without one the test skips, saying which JDK it needs.</summary>
    internal static string JdkHome(NewProjectLanguage language = NewProjectLanguage.Java)
    {
        int? highest = ProjectGenerator.HighestJdk(language);
        bool Accepted(string home) => JdkMajor(home) is int major && major >= ProjectGenerator.MinimumJdk &&
                                      (highest is null || major <= highest);
        string? found = PreferredJdks().FirstOrDefault(Accepted) ??
                        InstalledJdks().Where(Accepted).OrderByDescending(home => JdkMajor(home)).FirstOrDefault();
        if (found is null)
        {
            string range = highest is int top ? $"{ProjectGenerator.MinimumJdk} to {top}" : $"{ProjectGenerator.MinimumJdk} or newer";
            Assert.Skip($"A {language} starter needs a local JDK {range}; none was found in JAVA_HOME, on PATH or in the usual JDK install folders.");
        }
        return found;
    }

    /// <summary>A local JDK on which a starter's App is a compact source file (JDK 25 or newer): JAVA_HOME's or PATH's
    /// if new enough, else the newest installed. Without one the test skips.</summary>
    internal static string CompactJdkHome()
    {
        bool Compact(string home) => JdkMajor(home) >= ProjectGenerator.InstanceMainJdk;
        string? found = PreferredJdks().FirstOrDefault(Compact) ??
                        InstalledJdks().Where(Compact).OrderByDescending(home => JdkMajor(home)).FirstOrDefault();
        if (found is null)
        {
            Assert.Skip($"Needs a local JDK {ProjectGenerator.InstanceMainJdk} or newer; none was found in JAVA_HOME, on PATH or in the usual JDK install folders.");
        }
        return found;
    }

    private static IEnumerable<string> PreferredJdks()
    {
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome)
        {
            yield return javaHome;
        }
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string javac = Path.Combine(folder, OperatingSystem.IsWindows() ? "javac.exe" : "javac");
            if (File.Exists(javac))
            {
                // A distribution's /usr/bin/javac (Arch's archlinux-java, Debian's alternatives) links into the JDK's own bin.
                string real = File.ResolveLinkTarget(javac, returnFinalTarget: true)?.FullName ?? javac;
                yield return Path.GetDirectoryName(Path.GetDirectoryName(real)!)!;
            }
        }
    }

    private static IEnumerable<string> InstalledJdks()
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<string> roots = [Path.Combine(user, ".jdks")];
        if (OperatingSystem.IsWindows())
        {
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            roots.AddRange(new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "Amazon Corretto", "BellSoft" }
                .Select(vendor => Path.Combine(programs, vendor)));
        }
        else
        {
            roots.AddRange([
                "/usr/lib/jvm", "/usr/lib64/jvm", "/Library/Java/JavaVirtualMachines",
                Path.Combine(user, "Library", "Java", "JavaVirtualMachines"),
                Path.Combine(user, ".local", "share", "mise", "installs", "java"),
                Path.Combine(user, ".sdkman", "candidates", "java"),
                Path.Combine(user, ".asdf", "installs", "java")
            ]);
        }
        foreach (string root in roots.Where(Directory.Exists))
        {
            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                yield return folder;
                yield return Path.Combine(folder, "Contents", "Home");
            }
        }
    }

    private static int? JdkMajor(string home)
    {
        string release = Path.Combine(home, "release");
        if (!File.Exists(release) ||
            !File.Exists(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "javac.exe" : "javac")))
        {
            return null;
        }
        string? java = File.ReadLines(release).FirstOrDefault(line => line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal));
        return java is not null &&
               int.TryParse(java[13..].Trim().Trim('"').Split('.')[0].Split('-', 2)[0],
                   System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int major)
            ? major
            : null;
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
