using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Navigation;

namespace WayfarerWanderer.Tests;

/// <summary>Find in Files and Replace in Files, ported from Passenger: the scopes, the mask, going to a match, and Replace
/// All over open and closed files.</summary>
public sealed class FindInFilesTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("wayfarer-find-").FullName);

    public FindInFilesTests()
    {
        Write("pom.xml", "<project><name>greeter</name></project>");
        Write("app/src/main/java/demo/Main.java", "package demo;\n\nclass Main {\n    String greeting = \"hello\";\n    String other = \"Hello\";\n}\n");
        Write("app/src/main/java/demo/Greeter.java", "package demo;\n\nclass Greeter { String hello() { return \"hello\"; } }\n");
        Write("app/target/classes/Main.java", "hello hello");
        Write("docs/notes.md", "hello from the docs\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A watcher still letting go.
        }
    }

    [Fact]
    public void FileMaskTakesWildcardsAndExclusions()
    {
        FileMask mask = FileMask.Parse("*.java, *.kt, !Test*")!;
        Assert.True(mask.Matches("Main.JAVA"));
        Assert.True(mask.Matches("App.kt"));
        Assert.False(mask.Matches("TestMain.java"));
        Assert.False(mask.Matches("notes.md"));
        Assert.Equal("File masks should be comma-separated", FileMask.Problem("*.java;*.kt"));
    }

    [AvaloniaFact]
    public async Task FindsInTheProjectButNotInBuildOutputAndGoesToTheMatch()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            FindInFilesPopup popup = window.ShowFindInFiles(replace: false)!;
            Assert.Same(popup, window.SearchShown);
            popup.ScopeKind = SearchScopeKind.Project;
            popup.Text = "hello";
            await Searched(popup);
            Assert.Equal("5 matches in 3 files", popup.Status);
            Assert.DoesNotContain(popup.Matches, m => m.Path.Contains("target"));
            popup.MatchCase = true;
            await Searched(popup);
            Assert.Equal("4 matches in 3 files", popup.Status);

            SearchLine greeting = popup.Lines.First(l => l.Name == "Main.java");
            popup.Selected = greeting;
            popup.Choose();
            Assert.Null(window.SearchShown);
            Assert.EndsWith("Main.java", window.SourcePath);
            // The match chosen, the caret at its end.
            Assert.Equal(new TextPosition(3, 28), window.Editor.CaretPosition);
            Assert.Equal("hello", window.Editor.Document.Snapshot.GetText(window.Editor.Selections.Primary.Start, window.Editor.Selections.Primary.Length));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ScopesAndTheMaskNarrowTheFilesSearched()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            FindInFilesPopup popup = window.ShowFindInFiles(replace: false)!;
            popup.Text = "hello";
            popup.ScopeKind = SearchScopeKind.Directory;
            popup.Directory = Path.Combine(root, "docs");
            await Searched(popup);
            Assert.Equal(["notes.md"], popup.Matches.Select(m => m.Name).Distinct());

            popup.ScopeKind = SearchScopeKind.Project;
            popup.FileMask = "*.java";
            await Searched(popup);
            Assert.All(popup.Matches, m => Assert.EndsWith(".java", m.Name));

            window.OpenSource(Path.Combine(root, "docs", "notes.md"));
            popup = window.ShowFindInFiles(replace: false)!;
            // The scope and the mask were kept from the popup before.
            Assert.Equal(SearchScopeKind.Project, popup.ScopeKind);
            Assert.Equal("*.java", popup.FileMask);
            popup.FileMask = null;
            popup.ScopeKind = SearchScopeKind.Scope;
            popup.NamedScope = EditorWindow.CurrentFileScope;
            popup.Text = "hello";
            await Searched(popup);
            Assert.Equal("1 match in 1 file", popup.Status);

            popup.Regex = true;
            popup.Text = "(";
            await Searched(popup);
            Assert.Empty(popup.Matches);
            Assert.NotEqual("Nothing found", popup.Status);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ReplaceInFilesEditsAnOpenFileAndWritesAClosedOne()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            string main = Path.Combine(root, "app", "src", "main", "java", "demo", "Main.java");
            string greeter = Path.Combine(root, "app", "src", "main", "java", "demo", "Greeter.java");
            window.OpenSource(main);
            FindInFilesPopup popup = window.ShowFindInFiles(replace: true)!;
            popup.ScopeKind = SearchScopeKind.Project;
            popup.FileMask = "*.java";
            popup.MatchCase = true;
            popup.Text = "hello";
            await Searched(popup);
            string said = await window.ReplaceInFilesAsync(popup.Query, "bye", popup.Found.Replaceable);
            Assert.Equal("Replaced 3 matches in 2 files", said);
            // The open file changed as an edit, unsaved; the closed one was written.
            TextDocument open = window.FindOpenDocument(main)!;
            Assert.Contains("\"bye\"", open.Snapshot.ToString());
            Assert.Contains("\"Hello\"", open.Snapshot.ToString());
            Assert.True(open.IsModified);
            Assert.DoesNotContain("bye", File.ReadAllText(main));
            Assert.Equal("package demo;\n\nclass Greeter { String bye() { return \"bye\"; } }\n", File.ReadAllText(greeter));
            open.Undo();
            Assert.DoesNotContain("bye", open.Snapshot.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ReplaceInFilesNeverWritesThroughALink()
    {
        string outside = Directory.CreateTempSubdirectory("wayfarer-find-outside-").FullName;
        string target = Path.Combine(outside, "Shared.java");
        File.WriteAllText(target, "class Shared { String s = \"hello\"; }\n");
        string link = Path.Combine(root, "docs", "Shared.java");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // This system lets no one without the right make a link: nothing to check.
            Directory.Delete(outside, recursive: true);
            return;
        }
        EditorWindow window = await OpenAsync();
        try
        {
            SearchMatch match = new(link, 0, 27, 5, "class Shared { String s = \"hello\"; }");
            string said = await window.ReplaceInFilesAsync(new SearchQuery("hello"), "bye", [match]);
            Assert.Equal("Nothing replaced; not replaced: Shared.java (a link, or outside the folder where a link leads)", said);
            Assert.Contains("hello", File.ReadAllText(target));
        }
        finally
        {
            window.Close();
            File.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ShortcutOpensFindInFilesWithTheSelectedWord()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            window.OpenSource(Path.Combine(root, "app", "src", "main", "java", "demo", "Greeter.java"));
            int at = window.Editor.Document.Snapshot.ToString().IndexOf("Greeter", StringComparison.Ordinal);
            window.Editor.Select(new TextRange(at, "Greeter".Length));
            var modifier = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.KeyPress(Key.F, modifier | RawInputModifiers.Shift, PhysicalKey.F, null);
            Dispatcher.UIThread.RunJobs();
            FindInFilesPopup popup = Assert.IsType<FindInFilesPopup>(window.SearchShown);
            Assert.Equal("Greeter", popup.Text);
            await Searched(popup, again: false);
            // The pom's "greeter" too: Match case is off.
            Assert.Equal("2 matches in 2 files", popup.Status);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.Null(window.SearchShown);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The popup's search as it stands, once the changes made to it (whose events the dispatcher raises later)
    /// have landed: searched again now, or with <paramref name="again"/> false the search they began waited for.</summary>
    private static async Task Searched(FindInFilesPopup popup, bool again = true)
    {
        Dispatcher.UIThread.RunJobs();
        if (again)
        {
            await popup.SearchAsync();
        }
        for (int i = 0; i < 500 && popup.Status == "Searching…"; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private async Task<EditorWindow> OpenAsync()
    {
        EditorWindow window = EditorApplication.CreateWindow(null);
        window.Width = 1100;
        window.Height = 800;
        window.Show();
        await window.OpenWorkspaceAsync(root, TestContext.Current.CancellationToken);
        window.UpdateLayout();
        return window;
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
