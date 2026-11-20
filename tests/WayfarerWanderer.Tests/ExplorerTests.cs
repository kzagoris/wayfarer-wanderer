using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Explorer;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

public sealed class ExplorerTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("wayfarer-explorer-").FullName);

    public ExplorerTests()
    {
        Write("pom.xml", "<project />");
        Write("app/pom.xml", "<project />");
        Write("app/src/main/java/demo/Main.java", "class Main {}");
        Write("app/src/test/java/demo/MainTest.java", "class MainTest {}");
        Write("app/target/classes/demo/Main.class", "");
        Write(".git/HEAD", "ref: refs/heads/main");
        Write("README.md", "# demo");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A watcher still letting go: the temporary folder is the system's to clear.
        }
    }

    [AvaloniaFact]
    public void TreeShowsTheFolderWithModulesAndSourceRootsMarked()
    {
        ExplorerTree tree = new();
        tree.Load(root, Snapshot());

        ExplorerNode top = tree.Rows[0];
        Assert.Equal(Path.GetFileName(root), top.Name);
        Assert.True(top.IsExpanded);
        // Folders first, then files, each by name; version control's store never listed.
        Assert.Equal(["app", "pom.xml", "README.md"], tree.Rows.Skip(1).Select(r => r.Name));
        Assert.DoesNotContain(tree.Rows, r => r.Name == ".git");
        Assert.Equal(JvmFolder.Module, top.JvmFolder);

        ExplorerNode app = tree.Rows.Single(r => r.Name == "app");
        Assert.Equal(JvmFolder.Module, app.JvmFolder);
        Assert.Equal("demo-app", app.Detail);

        ExplorerNode main = tree.Reveal(Path.Combine(root, "app", "src", "main", "java", "demo", "Main.java"))!;
        Assert.True(main.IsFile);
        ExplorerNode sources = tree.Rows.Single(r => r.Path == Path.Combine(root, "app", "src", "main", "java"));
        Assert.Equal(JvmFolder.SourceRoot, sources.JvmFolder);
        Assert.Equal("sources", sources.Detail);
        Assert.Equal("tests", tree.Reveal(Path.Combine(root, "app", "src", "test", "java"))!.Detail);

        ExplorerNode target = tree.Rows.Single(r => r.Path == Path.Combine(root, "app", "target"));
        Assert.True(target.IsDimmed);
        Assert.False(app.IsDimmed);
        Assert.NotNull(main.Icon);
    }

    [AvaloniaFact]
    public void TreeWithoutAnImportShowsThePlainFolder()
    {
        ExplorerTree tree = new();
        tree.Load(root, snapshot: null);
        Assert.All(tree.Rows, r => Assert.Equal(JvmFolder.None, r.JvmFolder));
        Assert.All(tree.Rows, r => Assert.Null(r.Detail));
    }

    [Fact]
    public void IndexPassesOverVersionControlAndBuildOutput()
    {
        ExplorerTree tree = new();
        tree.Take(Snapshot());
        IReadOnlyList<string> files = ExplorerView.Index(root, tree.IsOutput);
        Assert.Contains(Path.Combine("app", "src", "main", "java", "demo", "Main.java"), files);
        Assert.DoesNotContain(files, f => f.StartsWith(".git", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.EndsWith(".class", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task FilterNarrowsTheRowsToMatchingFiles()
    {
        ExplorerView view = new();
        Window host = new() { Content = view, Width = 400, Height = 600 };
        host.Show();
        try
        {
            view.Load(root, Snapshot());
            view.FilterText = "maintest";
            await view.FilterNowAsync();
            Assert.True(view.Tree.IsFiltered);
            Assert.Equal(["MainTest.java"], view.Tree.Rows.Where(r => r.IsFile).Select(r => r.Name));

            view.FilterText = "";
            await view.FilterNowAsync();
            Assert.False(view.Tree.IsFiltered);
            Assert.Contains(view.Tree.Rows, r => r.Name == "README.md");
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task RowsFollowFilesMadeOnDisk()
    {
        ExplorerView view = new();
        Window host = new() { Content = view, Width = 400, Height = 600 };
        host.Show();
        try
        {
            view.Load(root, null);
            Write("NOTES.txt", "later");
            DateTime until = DateTime.UtcNow.AddSeconds(10);
            while (!view.Tree.Rows.Any(r => r.Name == "NOTES.txt") && DateTime.UtcNow < until)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Contains(view.Tree.Rows, r => r.Name == "NOTES.txt");
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task EnterOnAFileOpensItInTheEditor()
    {
        var window = EditorApplication.CreateWindow(new TextDocument("class Scratch {}"));
        window.Width = 1000;
        window.Height = 700;
        window.Show();
        try
        {
            await window.OpenWorkspaceAsync(root, TestContext.Current.CancellationToken);
            Assert.True(window.ToolDock.IsShown(ToolWindowIds.Explorer));
            string main = Path.Combine(root, "app", "src", "main", "java", "demo", "Main.java");
            Assert.True(window.Explorer.Reveal(main));
            window.Explorer.FocusRows();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.Equal(main, window.SourcePath);
            Assert.Equal("class Main {}", window.Editor.Document.Snapshot.ToString());

            using var frame = window.CaptureRenderedFrame();
            string folder = Environment.GetEnvironmentVariable("WAYFARER_TEST_OUTPUT") ??
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../test-results"));
            Directory.CreateDirectory(folder);
            frame!.Save(Path.Combine(folder, "explorer.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void FocusExplorerShowsTheProjectWindow()
    {
        var window = EditorApplication.CreateWindow(new TextDocument("class Main {}"));
        window.Show();
        try
        {
            window.ToolDock.Hide(ToolWindowIds.Explorer);
            Assert.False(window.ToolDock.IsShown(ToolWindowIds.Explorer));
            EditorAction show = Assert.Single(window.Actions.Find("Project"), a => a.Id == AppActions.FocusExplorer);
            Assert.True(show.TryExecute());
            Assert.True(window.ToolDock.IsShown(ToolWindowIds.Explorer));
        }
        finally
        {
            window.Close();
        }
    }

    private WorkspaceSnapshot Snapshot()
    {
        JvmToolchain none = new(null, null, null);
        JvmClasspath empty = new([], [], []);
        string app = Path.Combine(root, "app");
        return new WorkspaceSnapshot(1, root, JvmBuildSystem.Maven, Path.Combine(root, "pom.xml"), none,
        [
            new JvmModule(".", Path.GetFileName(root), root, [], empty, none, none, none, [], []),
            new JvmModule("app", "demo-app", app,
            [
                new JvmSourceRoot(Path.Combine(app, "src", "main", "java"), JvmSourceKind.MainJava),
                new JvmSourceRoot(Path.Combine(app, "src", "test", "java"), JvmSourceKind.TestJava),
            ], empty, none, none, none, [], []),
        ]);
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
