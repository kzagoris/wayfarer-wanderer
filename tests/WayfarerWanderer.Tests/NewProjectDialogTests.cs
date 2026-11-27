using Avalonia.Headless.XUnit;
using WayfarerWanderer.App;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>The New Project dialog: its families and their build systems, the names it derives, what it refuses, and an
/// IntelliJ module made from it.</summary>
[Collection(JvmTrustCollection.Name)]
public sealed class NewProjectDialogTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("wayfarer-new-project-dialog-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // A handle still letting go: the temporary folder is the system's to clear.
        }
    }

    [AvaloniaFact]
    public void ListsJvmFamiliesWithIntelliJFirstForJava()
    {
        NewProjectDialog dialog = new(folder, jdkHome: folder);
        Assert.Equal(["Java", "Kotlin"], dialog.Listed.Select(f => f.Name));
        Assert.Equal("Java", dialog.Family?.Name);
        Assert.Equal([NewProjectBuild.IntelliJ, NewProjectBuild.Maven, NewProjectBuild.Gradle], dialog.Builds);
        Assert.Equal(NewProjectBuild.IntelliJ, dialog.Build);
        Assert.False(dialog.AsksCoordinates);
        Assert.Contains("javac", dialog.BuildNote);
        Assert.Contains(".iml", dialog.Writes);

        dialog.Build = NewProjectBuild.Maven;
        Assert.True(dialog.AsksCoordinates);
        Assert.Contains("pom.xml", dialog.Writes);

        Assert.True(dialog.Choose("Kotlin"));
        Assert.Equal([NewProjectBuild.Gradle, NewProjectBuild.Maven], dialog.Builds);
        Assert.Equal(NewProjectLanguage.Kotlin, dialog.Language);
        Assert.True(dialog.Choose("Java"));
        Assert.Equal(NewProjectBuild.Maven, dialog.Build);
    }

    [AvaloniaFact]
    public void SearchFindsFamiliesByNameAndBuild()
    {
        NewProjectDialog dialog = new(folder, jdkHome: folder) { Query = "kotlin" };
        Assert.Equal(["Kotlin"], dialog.Listed.Select(f => f.Name));
        Assert.Equal([NewProjectLanguage.Kotlin], dialog.Languages);

        // Spring Boot is not offered (docs/todo.md).
        dialog.Query = "spring";
        Assert.Empty(dialog.Listed);

        dialog.Query = "intellij";
        Assert.Equal(["Java"], dialog.Listed.Select(f => f.Name));

        dialog.Query = "cobol";
        Assert.Empty(dialog.Listed);
        Assert.Null(dialog.Family);
    }

    [AvaloniaFact]
    public void NamesFollowTheProjectUntilTypedOver()
    {
        Directory.CreateDirectory(Path.Combine(folder, "untitled"));
        NewProjectDialog dialog = new(folder, jdkHome: folder);
        Assert.Equal("untitled1", dialog.ProjectName);
        Assert.Equal("untitled1", dialog.Artifact);
        Assert.Equal("com.example.untitled1", dialog.Package);

        dialog.ProjectName = "My Course 2";
        Assert.Equal("my-course-2", dialog.Artifact);
        Assert.Equal("com.example.mycourse2", dialog.Package);
        Assert.Equal($"The project will be created in {Path.Combine(folder, "My Course 2")}", dialog.Where);

        dialog.Package = "org.school.course";
        dialog.ProjectName = "Other";
        Assert.Equal("other", dialog.Artifact);
        Assert.Equal("org.school.course", dialog.Package);
    }

    [AvaloniaFact]
    public async Task CreateRefusesWithoutWriting()
    {
        string taken = Path.Combine(folder, "taken");
        Directory.CreateDirectory(taken);
        File.WriteAllText(Path.Combine(taken, "keep.txt"), "mine");
        NewProjectDialog dialog = new(folder, jdkHome: folder) { ProjectName = "taken" };

        await dialog.CreateAsync();
        Assert.Contains("not empty", dialog.Problem);
        Assert.Null(dialog.Created);
        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(taken).Select(Path.GetFileName));

        dialog.ProjectName = "fresh";
        await dialog.CreateAsync();
        Assert.Contains("JDK", dialog.Problem);
        Assert.False(Directory.Exists(Path.Combine(folder, "fresh")));
    }

    [AvaloniaFact]
    public async Task CreatesAnIntelliJModuleAndCloses()
    {
        string jdk = NewProjectTests.JdkHome(NewProjectLanguage.Java);
        NewProjectDialog dialog = new(folder, jdk) { ProjectName = "Course" };
        dialog.Show();

        await dialog.CreateAsync();

        Assert.Null(dialog.Problem);
        ProjectGenerationResult made = Assert.IsType<ProjectGenerationResult>(dialog.Created);
        Assert.Equal(Path.Combine(folder, "Course"), made.Destination);
        Assert.Equal(Path.Combine(made.Destination, "course.iml"), Directory.GetFiles(made.Destination, "*.iml").SingleOrDefault());
        Assert.True(File.Exists(Path.Combine(made.Destination, "src", "com", "example", "course", "App.java")));
        Assert.False(dialog.IsVisible);
    }

    [AvaloniaFact]
    public async Task CreatesAGitRepositoryWithTheFilesChosen()
    {
        string jdk = NewProjectTests.JdkHome(NewProjectLanguage.Java);
        NewProjectDialog dialog = new(folder, jdk, repository: new App.Shell.State.RepositoryChoices { Repository = true, EditorConfig = false })
        {
            ProjectName = "Shared",
        };
        dialog.Show();
        await Until(() => dialog.MakesRepository);

        await dialog.CreateAsync();

        Assert.Null(dialog.Problem);
        Assert.Null(dialog.RepositoryProblem);
        string made = Assert.IsType<ProjectGenerationResult>(dialog.Created).Destination;
        Assert.True(Directory.Exists(Path.Combine(made, ".git")));
        Assert.Equal(App.Shell.Vcs.RepositoryFiles.GitAttributes, File.ReadAllText(Path.Combine(made, ".gitattributes")));
        Assert.False(File.Exists(Path.Combine(made, ".editorconfig")));
        Assert.True(File.Exists(Path.Combine(made, ".gitignore")));
        Assert.Equal(new App.Shell.State.RepositoryChoices { Repository = true, EditorConfig = false }, dialog.RepositoryChosen);
    }

    [AvaloniaFact]
    public async Task OffersNoRepositoryInsideOne()
    {
        string outer = Path.Combine(folder, "outer");
        Directory.CreateDirectory(outer);
        using (System.Diagnostics.Process init = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init -q") { WorkingDirectory = outer })!)
        {
            await init.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        NewProjectDialog dialog = new(outer, folder, repository: new App.Shell.State.RepositoryChoices { Repository = true });
        dialog.Show();

        await Until(() => dialog.RepositoryNote is not null);

        Assert.False(dialog.MakesRepository);
        Assert.StartsWith("The project goes into the Git repository at ", dialog.RepositoryNote, StringComparison.Ordinal);
        Assert.True(dialog.RepositoryChosen.Repository);
        dialog.Close();
    }

    /// <summary>Waits, the UI thread turning, until <paramref name="done"/> holds or ten seconds pass.</summary>
    private static async Task Until(Func<bool> done)
    {
        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void JdkDropDownListsActionsKeptAndDetectedJdks()
    {
        string kept = NewProjectTests.JdkHome(NewProjectLanguage.Java);
        string registryFile = Path.Combine(folder, "jdks.json");
        JdkRegistry jdks = new(registryFile, candidates: () => [kept], intelliJConfig: Path.Combine(folder, "no-intellij"));

        NewProjectDialog dialog = new(folder, jdkHome: kept, jdks);
        Assert.Equal([JdkEntryKind.Download, JdkEntryKind.AddFromDisk, JdkEntryKind.Separator, JdkEntryKind.Header, JdkEntryKind.Jdk],
            dialog.JdkEntries.Take(5).Select(e => e.Kind));
        Assert.Equal("Detected JDKs", dialog.JdkEntries[3].Text);
        Assert.Equal(Path.GetFullPath(JdkRegistry.Resolved(kept)), Path.GetFullPath(dialog.JdkHome));

        // A home the table lacks is listed at the top of the table's JDKs and chosen.
        dialog.JdkHome = folder;
        Assert.Equal(folder, dialog.JdkHome);
        JdkEntry other = Assert.Single(dialog.JdkEntries, e => e.IsJdk && e.Home == folder);
        Assert.True(dialog.JdkEntries.ToList().IndexOf(other) < dialog.JdkEntries.ToList().FindIndex(e => e.Text == "Detected JDKs"));
    }

    [AvaloniaFact]
    public void CoordinatesWaitUnderAdvancedSettings()
    {
        NewProjectDialog dialog = new(folder, jdkHome: folder);
        Assert.False(dialog.AdvancedShown);
        dialog.AdvancedShown = true;
        Assert.True(dialog.AdvancedShown);
    }

    [Fact]
    public void DefaultFolderSitsBesideTheProjectOpen()
    {
        string open = Path.Combine(folder, "open");
        Assert.Equal(folder, NewProjectAction.DefaultFolder(open));
        Assert.EndsWith("WayfarerProjects", NewProjectAction.DefaultFolder(null));
    }
}
