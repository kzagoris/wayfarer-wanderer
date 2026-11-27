using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Vcs;
using A = WayfarerWanderer.App.Shell.AppActions;

namespace WayfarerWanderer.Tests;

/// <summary>Passenger's .gitignore and .gitattributes help in Wayfarer: completion of untracked paths and of attributes and
/// their values, the card saying what a line ignores or what an attribute does, the marks on lines ignoring nothing and
/// values git reads by nothing, and the Explorer's Add to .gitignore, Go to Ignoring Line and New .gitattributes.</summary>
public sealed class GitIgnoreTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("wayfarer-ignore-").FullName);

    public GitIgnoreTests()
    {
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "core.autocrlf", "false");
        Write("pom.xml", "<project />\n");
        Write(".gitignore", "*.log\n*.tmp\n");
        Git("add", ".");
        Git("commit", "-q", "-m", "first");
        Write("app.log", "log\n");
        Write("notes/a.txt", "a\n");
        Write("notes/b.txt", "b\n");
        Write("todo.md", "todo\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A watcher still letting go: the temporary folder is the system's to clear.
        }
    }

    [Fact]
    public void ASpotSkipsANegationAndALeadingSlashAndNoneGoesInAComment()
    {
        GitIgnoreAssist assist = new(() => null);
        Assert.Equal((2, 5), assist.Spot("!/out\n", 5));
        Assert.Null(assist.Spot("# out\n", 3));
        Assert.True(GitIgnoreAssist.IsGitIgnore(Path.Combine(root, ".gitignore")));
        Assert.False(GitIgnoreAssist.IsGitIgnore(Path.Combine(root, ".dockerignore")));
    }

    [Fact]
    public async Task CompletionListsTheUntrackedPathsGitDoesNotIgnoreAndAPatternForEachKind()
    {
        Git git = await RepositoryAsync();
        GitIgnoreAssist assist = new(() => git);
        string text = "*.log\n*.tmp\n";
        IReadOnlyList<AssistItem> items = await assist.ItemsAsync(Path.Combine(root, ".gitignore"), text, text.Length, TestContext.Current.CancellationToken);
        Assert.Equal(["notes/", "todo.md", "*.md"], items.Select(i => i.Label));
        Assert.Equal("untracked folder", items[0].Detail);
        Assert.DoesNotContain(items, i => i.Label == "app.log");
    }

    [Fact]
    public async Task TheCardSaysWhatALineIgnoresAndTheMarksFadeALineIgnoringNothing()
    {
        Git git = await RepositoryAsync();
        GitIgnoreAssist assist = new(() => git);
        string path = Path.Combine(root, ".gitignore");
        TextSnapshot text = TextSnapshot.From(File.ReadAllText(path));
        string? card = await assist.HoverAsync(path, text, modified: false, offset: 1, TestContext.Current.CancellationToken);
        Assert.Contains("Ignores 1 path here: app.log", card);
        Assert.Contains("```gitignore\n*.log\n```", card);
        Assert.Contains("save it", await assist.HoverAsync(path, text, modified: true, offset: 1, TestContext.Current.CancellationToken));

        IReadOnlyList<Marker> marks = await assist.MarksAsync(path, text, TestContext.Current.CancellationToken);
        Marker faded = Assert.Single(marks);
        Assert.Equal(MarkerKind.Unnecessary, faded.Kind);
        Assert.Equal("*.tmp", text.GetText(faded.Range.Start, faded.Range.Length));
        // Text that differs from the file saved is not what git reads: no marks.
        Assert.Empty(await assist.MarksAsync(path, TextSnapshot.From("*.log\n*.tmp\nx\n"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitAttributesCompletesAttributesThenValuesAndMarksAnEolGitReadsByNothing()
    {
        Git git = await RepositoryAsync();
        GitAttributesAssist assist = new(() => git);
        string path = Path.Combine(root, ".gitattributes");
        Assert.True(assist.Owns(path));
        string named = "*.sh ";
        IReadOnlyList<AssistItem> attributes = await assist.ItemsAsync(path, named, named.Length, TestContext.Current.CancellationToken);
        Assert.Contains(attributes, i => i.Label == "eol");
        Assert.Contains(attributes, i => i.Label == "linguist-generated");
        Assert.Null(assist.Spot(named, 2));

        string valued = "*.sh eol=";
        Assert.True(assist.Opens(valued, valued.Length, '='));
        IReadOnlyList<AssistItem> values = await assist.ItemsAsync(path, valued, valued.Length, TestContext.Current.CancellationToken);
        Assert.Contains(values, v => v.Label == "lf");
        Assert.Contains(values, v => v.Label == "crlf");

        TextSnapshot wrong = TextSnapshot.From("*.sh eol=cr\n");
        Marker mark = Assert.Single(await assist.MarksAsync(path, wrong, TestContext.Current.CancellationToken));
        Assert.Equal(MarkerKind.Warning, mark.Kind);
        Assert.Equal("cr", wrong.GetText(mark.Range.Start, mark.Range.Length));
        Assert.Contains("```gitattributes", await assist.HoverAsync(path, wrong, false, 6, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task TypingAtTheStartOfALineListsUntrackedPathsAndEnterPutsOneIn()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            await Until(() => window.Vcs.Repository is not null);
            window.OpenSource(Path.Combine(root, ".gitignore"));
            TextDocument document = window.Editor.Document;
            window.Editor.GoTo(document.Snapshot.GetPosition(document.Snapshot.Length));
            window.Editor.Focus();
            window.KeyTextInput("t");
            await Until(() => window.Completion.Shown.Count > 0);
            Assert.Equal("todo.md", window.Completion.Selected?.Label);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await Until(() => !window.Completion.IsOpen);
            Assert.Equal("*.log\n*.tmp\ntodo.md", document.Snapshot.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task QuickDocumentationOnALineSaysWhatItIgnoresAndTheOpenFileIsMarked()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            await Until(() => window.Vcs.Repository is not null);
            window.OpenSource(Path.Combine(root, ".gitignore"));
            await Until(() => window.Editor.Markers.Count > 0);
            Marker faded = Assert.Single(window.Editor.Markers);
            Assert.Equal(MarkerKind.Unnecessary, faded.Kind);
            window.Editor.GoTo(new TextPosition(0, 1));
            await window.QuickDocumentationAsync();
            Assert.True(window.QuickDocCard.IsOpen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AddToGitIgnoreAddsTheFileShownAndGoToIgnoringLineOpensTheLineDecidingAnother()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            await Until(() => window.Vcs.Changes.Any(c => c.Kind == ChangeKind.Unversioned));
            window.OpenSource(Path.Combine(root, "notes", "a.txt"));
            ActionContext context = window.Actions.Registry.Context();
            Assert.Null(window.Actions.Registry.WhyNot(A.AddToGitIgnore, context));
            Assert.Null(window.Actions.Registry.Run(A.AddToGitIgnore, context));
            await Until(() => File.ReadAllText(Path.Combine(root, ".gitignore")).Contains("/notes/a.txt"));
            Assert.Equal("*.log\n*.tmp\n/notes/a.txt\n", File.ReadAllText(Path.Combine(root, ".gitignore")));

            await window.GoToIgnoringLineAsync(Path.Combine(root, "app.log"));
            Assert.True(PathNames.SameFile(Path.Combine(root, ".gitignore"), window.SourcePath!));
            Assert.Equal(0, window.Editor.CaretPosition.Line);
            Assert.Contains("is ignored by line 1", window.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task NewGitAttributesMakesWayfarersFileOnceAndThenOpensIt()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            window.AddRepositoryFile(root, RepositoryFile.GitAttributes);
            string made = Path.Combine(root, ".gitattributes");
            Assert.Equal(RepositoryFiles.GitAttributes, File.ReadAllText(made));
            Assert.True(PathNames.SameFile(made, window.SourcePath!));
            window.AddRepositoryFile(root, RepositoryFile.GitAttributes);
            Assert.Contains("already", window.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    private async Task<Git> RepositoryAsync() => (await Passenger.Vcs.Git.OpenAsync(root)).Tree ?? throw new InvalidOperationException("No repository");

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
