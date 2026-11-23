using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Passenger.Core;
using Passenger.Text;
using Passenger.Vcs;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Vcs;
using A = WayfarerWanderer.App.Shell.AppActions;

namespace WayfarerWanderer.Tests;

/// <summary>Passenger 0.4.2's Git features in Wayfarer's window: the status bar's branch, staging a change or just its
/// selected lines from a diff, rolling back the change at a diff's caret in a file not open, Group by Directory, and Show
/// Diff with Working Tree of a file the branch lacks.</summary>
public sealed class GitUiTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("wayfarer-git-").FullName);

    public GitUiTests()
    {
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "core.autocrlf", "false");
        Write("pom.xml", "<project />\n");
        Write("Main.java", Lines("a", "b", "c", "d", "e", "f", "g", "h", "i", "j"));
        Git("add", ".");
        Git("commit", "-q", "-m", "first");
    }

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A watcher still letting go: the temporary folder is the system's to clear.
        }
    }

    [Fact]
    public void TheBranchPopupListsTheCurrentBranchThenFavouritesThenTheRest()
    {
        BranchInfo current = Branch("feature", current: true);
        BranchInfo main = Branch("main");
        BranchInfo other = Branch("other");
        List<BranchInfo> listed = GitBranchWidget.Listed([other, main, current], new App.Shell.ToolWindows.GitBranchesState());
        Assert.Equal(["feature", "main", "other"], listed.Select(b => b.Name));
    }

    [AvaloniaFact]
    public async Task TheStatusBarShowsTheBranchCheckedOut()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            await Until(() => window.BranchShown == "main");
            Assert.Equal("main", window.BranchShown);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task StageInADiffTakesOnlyTheChangeAtTheCaretIntoTheIndex()
    {
        Write("Main.java", Lines("a", "B", "c", "d", "e", "f", "g", "h", "I", "j"));
        EditorWindow window = await OpenAsync();
        try
        {
            await Until(() => window.Vcs.Changes.Count == 1);
            DiffWindow diffs = (await window.ShowDiffAsync(Path.Combine(root, "Main.java")))!;
            DiffView view = diffs.View;
            await Until(() => view.Stages && view.HeldAt(0) is not null);
            view.Right.Selections = SelectionSet.Single(view.Right.Document.Snapshot.GetOffset(new TextPosition(1, 0)));
            Assert.Null(window.Actions.Registry.Run(A.Stage, new ActionContext(window.Editor, view.Right)));
            await Until(() => Git("diff", "--cached").Contains("+B"));
            string staged = Git("diff", "--cached");
            Assert.Contains("+B", staged);
            Assert.DoesNotContain("+I", staged);
            await Until(() => view.HeldAt(0) == true);
            Assert.False(view.HeldAt(1));
            diffs.Close();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task StageInADiffTakesOnlyTheSelectedLinesOfAChange()
    {
        Write("Main.java", Lines("a", "b", "new1", "new2", "c", "d", "e", "f", "g", "h", "i", "j"));
        EditorWindow window = await OpenAsync();
        try
        {
            await Until(() => window.Vcs.Changes.Count == 1);
            DiffWindow diffs = (await window.ShowDiffAsync(Path.Combine(root, "Main.java")))!;
            DiffView view = diffs.View;
            await Until(() => view.Stages && view.HeldAt(0) is not null);
            TextSnapshot text = view.Right.Document.Snapshot;
            // The first added line alone, ending at the next line's start.
            view.Right.Selections = SelectionSet.Single(new Selection(text.GetOffset(new TextPosition(2, 0)), text.GetOffset(new TextPosition(3, 0))));
            Assert.Null(window.Actions.Registry.Run(A.Stage, new ActionContext(window.Editor, view.Right)));
            await Until(() => Git("diff", "--cached").Contains("+new1"));
            string staged = Git("diff", "--cached");
            Assert.Contains("+new1", staged);
            Assert.DoesNotContain("+new2", staged);
            diffs.Close();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RollbackLinesInADiffOfAFileNotOpenWritesTheChangeBackOnDisk()
    {
        string main = Write("Main.java", Lines("a", "B", "c", "d", "e", "f", "g", "h", "i", "j"));
        EditorWindow window = await OpenAsync();
        try
        {
            await Until(() => window.Vcs.Changes.Count == 1);
            DiffWindow diffs = (await window.ShowDiffAsync(main))!;
            DiffView view = diffs.View;
            Assert.False(view.IsEditable);
            view.Right.Selections = SelectionSet.Single(view.Right.Document.Snapshot.GetOffset(new TextPosition(1, 0)));
            Assert.Null(window.Actions.Registry.Run(A.RollbackLines, new ActionContext(window.Editor, view.Right)));
            await Until(() => File.ReadAllText(main) == Lines("a", "b", "c", "d", "e", "f", "g", "h", "i", "j"));
            Assert.Equal(Lines("a", "b", "c", "d", "e", "f", "g", "h", "i", "j"), File.ReadAllText(main));
            diffs.Close();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task GroupByDirectoryTurnsAndIsTickedWhileOn()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            await Until(() => window.Vcs.Repository is not null);
            bool was = window.Vcs.GroupsByDirectory;
            Assert.Null(window.Actions.Registry.Run(A.GroupByDirectory));
            Assert.Equal(!was, window.Vcs.GroupsByDirectory);
            Assert.Equal(!was, window.Actions.Registry[A.GroupByDirectory]!.IsChecked!(window.Actions.Registry.Context()));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ComparingAFileTheBranchLacksShowsItBesideAnEmptySide()
    {
        string added = Write("Added.java", Lines("class Added {}"));
        EditorWindow window = await OpenAsync();
        try
        {
            await Until(() => window.Vcs.Repository is not null);
            Assert.Null(await window.CompareWithAsync(added, "refs/heads/main", "main", added));
            DiffWindow? shown = await window.CompareWithAsync(added, "refs/heads/main", "main", added, added: true);
            Assert.NotNull(shown);
            Assert.Equal(string.Empty, shown.View.Left.Document.Snapshot.ToString());
            shown.Close();
        }
        finally
        {
            window.Close();
        }
    }

    private static BranchInfo Branch(string name, bool current = false) =>
        new(new GitRef($"refs/heads/{name}", name, RefKind.Local), "0000000000000000000000000000000000000000", current);

    private static string Lines(params string[] lines) => string.Concat(lines.Select(l => l + "\n"));

    private async Task<EditorWindow> OpenAsync()
    {
        EditorWindow window = EditorApplication.CreateWindow(null);
        window.Width = 1000;
        window.Height = 700;
        window.Show();
        await window.OpenWorkspaceAsync(root, TestContext.Current.CancellationToken);
        window.UpdateLayout();
        return window;
    }

    /// <summary>Waits, the UI thread turning, until <paramref name="done"/> holds or ten seconds pass.</summary>
    private static async Task Until(Func<bool> done)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (!done() && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Git(params string[] args)
    {
        ProcessStartInfo start = new("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using Process git = Process.Start(start)!;
        string output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        return output;
    }
}
