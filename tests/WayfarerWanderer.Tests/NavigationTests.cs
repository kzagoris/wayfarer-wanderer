using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Navigation;
using A = WayfarerWanderer.App.Shell.AppActions;

namespace WayfarerWanderer.Tests;

/// <summary>The Navigate menu's popups and places, ported from Passenger: Find Action, Search Everywhere, Go to File, Go to
/// Symbol, Go to Line:Column, File Structure, Recent Files, and Back and Forward.</summary>
public sealed class NavigationTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("wayfarer-navigation-").FullName);

    public NavigationTests()
    {
        Write("pom.xml", "<project />");
        Write("app/src/main/java/demo/Main.java", "package demo;\n\nclass Main {\n    void run() {}\n}\n");
        Write("app/src/main/java/demo/OrderService.java", "package demo;\n\nclass OrderService {}\n");
        Write("app/target/classes/demo/Main.class", "binary");
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

    [Fact]
    public void FileIndexRanksNamesAndLeavesOutputAndGitOut()
    {
        FileIndex index = FileIndex.Of(root, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(index.Files, f => f.Contains("target") || f.Contains(".git"));
        Assert.Equal(Path.Combine("app", "src", "main", "java", "demo", "Main.java"), index.Search("Main")[0]);
        // CamelHumps, and a path with a slash.
        Assert.Equal("OrderService.java", Path.GetFileName(index.Search("OS")[0]));
        Assert.Equal("Main.java", Path.GetFileName(index.Search("demo/Main")[0]));
        Assert.Empty(index.Search("zzz"));
    }

    [Fact]
    public void PlaceInReadsALineAndColumnAfterTheName()
    {
        Assert.Equal(("Main.java", new TextPosition(19, 3)), GoToFilePopup.PlaceIn("Main.java:20:4"));
        Assert.Equal(("Main.java", new TextPosition(19, 0)), GoToFilePopup.PlaceIn("Main.java:20"));
        Assert.Equal(("Main.java", (TextPosition?)null), GoToFilePopup.PlaceIn("Main.java"));
    }

    [AvaloniaFact]
    public async Task GoToFileFindsAFileAndOpensItAtTheLineAsked()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            GoToFilePopup popup = (await window.ShowGoToFileAsync())!;
            Assert.Same(popup, window.SearchShown);
            Typed(popup, "Main.java:4");
            Assert.Equal("Main.java", popup.Selected!.Name);
            popup.Choose();
            Assert.Null(window.SearchShown);
            Assert.Equal(Path.Combine(root, "app", "src", "main", "java", "demo", "Main.java"), window.SourcePath);
            Assert.Equal(new TextPosition(3, 0), window.Editor.CaretPosition);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task GoToFileShortcutOpensThePopupWithTheKeysInItsQuery()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            var modifier = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.KeyPress(Key.T, modifier | RawInputModifiers.Shift, PhysicalKey.T, null);
            GoToFilePopup popup = await Shown<GoToFilePopup>(window);
            Assert.True(popup.QueryBox.IsFocused);
            window.KeyTextInput("OrderS");
            Assert.Equal("OrderService.java", popup.Selected!.Name);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.Null(window.SearchShown);
            Assert.Null(window.SourcePath);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SearchEverywhereListsRecentFilesThenFilesWithoutAServer()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            window.OpenSource(Path.Combine(root, "README.md"));
            window.OpenSource(Path.Combine(root, "pom.xml"));
            SymbolPopup popup = window.ShowSearchEverywhere()!;
            await popup.SettledAsync();
            // Before anything is typed: the recent files but the one shown.
            Assert.Equal(["README.md"], popup.Rows.Select(r => r.Name));
            Typed(popup, "OrderService");
            await popup.SettledAsync();
            SymbolRow file = Assert.Single(popup.Rows);
            Assert.Equal(SymbolKind.File, file.Kind);
            popup.Choose();
            Assert.EndsWith("OrderService.java", window.SourcePath);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task GoToSymbolSaysWhyWhenNoServerCanAnswer()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            SymbolPopup popup = window.ShowGoToSymbol()!;
            Typed(popup, "Main");
            await popup.SettledAsync();
            Assert.Empty(popup.Rows);
            Assert.Equal("Symbols are found once the folder is trusted", popup.Said);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task GoToLineMovesTheCaretAndSaysWhatItTakes()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            window.OpenSource(Path.Combine(root, "app", "src", "main", "java", "demo", "Main.java"));
            GoToLinePopup popup = window.ShowGoToLine()!;
            Assert.Equal("1:1", popup.Query);
            popup.Query = "99";
            popup.Go();
            Assert.Equal("A line from 1 to 6, and a column after a colon if wanted", popup.Problem);
            popup.Query = "4:5";
            popup.Go();
            Assert.Null(window.SearchShown);
            Assert.Equal(new TextPosition(3, 4), window.Editor.CaretPosition);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task BackAndForwardGoBetweenThePlacesLeft()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            string main = Path.Combine(root, "app", "src", "main", "java", "demo", "Main.java");
            string readme = Path.Combine(root, "README.md");
            window.OpenSource(main);
            window.NavigateTo(main, new TextPosition(3, 4));
            window.OpenSource(readme);
            Assert.True(window.RunFromMenu(A.Back));
            Assert.Equal(main, window.SourcePath);
            Assert.Equal(new TextPosition(3, 4), window.Editor.CaretPosition);
            Assert.True(window.RunFromMenu(A.Back));
            Assert.Equal(new TextPosition(0, 0), window.Editor.CaretPosition);
            Assert.False(window.RunFromMenu(A.Back));
            Assert.True(window.RunFromMenu(A.Forward));
            Assert.True(window.RunFromMenu(A.Forward));
            Assert.Equal(readme, window.SourcePath);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RecentFilesOpenAFileWhereItsCaretWasLeft()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            string main = Path.Combine(root, "app", "src", "main", "java", "demo", "Main.java");
            window.OpenSource(main);
            window.Editor.GoTo(new TextPosition(2, 3));
            window.OpenSource(Path.Combine(root, "README.md"));
            SymbolPopup popup = window.ShowRecentFiles();
            await popup.SettledAsync();
            Assert.Equal(["Main.java"], popup.Rows.Select(r => r.Name));
            popup.Choose();
            Assert.Equal(main, window.SourcePath);
            Assert.Equal(new TextPosition(2, 3), window.Editor.CaretPosition);

            // Show edited only lists none until a file is edited.
            SymbolPopup edited = window.ShowRecentFiles(editedOnly: true);
            await edited.SettledAsync();
            Assert.Empty(edited.Rows);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void FindActionRunsWhatIsChosenAndListsItFirstAfter()
    {
        EditorWindow window = EditorApplication.CreateWindow(new TextDocument("class Main {}"));
        window.Show();
        try
        {
            ActionPopup popup = window.ShowFindAction("Toggle Breakpoint");
            Assert.Equal(A.ToggleBreakpoint, popup.Selected!.Id);
            popup.Choose();
            Assert.True(window.Breakpoints.IsMarked(0));
            ActionPopup again = window.ShowFindAction();
            Assert.Equal(A.ToggleBreakpoint, again.Rows[0].Id);
            again.GiveUp();
            Assert.Null(window.SearchShown);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void NavigateMenuListsThePopups()
    {
        EditorWindow window = EditorApplication.CreateWindow(new TextDocument("class Main {}"));
        window.Show();
        try
        {
            MenuItem navigate = window.MainMenuNamed("Navigate");
            string[] titles = [.. navigate.Items.OfType<MenuItem>().Select(i => (string)i.Header!)];
            Assert.Equal(["Back", "Forward", "Search Everywhere", "Go to File…", "Go to Symbol…", "Go to Line:Column…", "File Structure", "Jump to Navigation Bar", "Recent Files", "Recently Changed Files"], titles);
            // No folder is open: the searches over one say so.
            Assert.False(navigate.Items.OfType<MenuItem>().Single(i => (string)i.Header! == "Go to File…").IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void WorkspaceSymbolsKeepFilesOnDiskWithTheirPlaces()
    {
        string main = Path.Combine(root, "Main.java");
        string json = JsonSerializer.Serialize(new object[]
        {
            new { name = "Main", kind = 5, containerName = "demo", location = new { uri = new Uri(main).AbsoluteUri, range = new { start = new { line = 2, character = 6 }, end = new { line = 2, character = 10 } } } },
            new { name = "String", kind = 5, location = new { uri = "jdt://contents/java.base/java.lang/String.class", range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } } } },
        });
        SymbolRow row = Assert.Single(EditorWindow.WorkspaceSymbols(JsonDocument.Parse(json).RootElement, root));
        Assert.Equal(("Main", "demo", "Main.java:3", SymbolKind.Class, new TextPosition(2, 6)), (row.Name, row.Detail, row.Where, row.Kind, row.Position));
        Assert.Equal(PathNames.Canonical(main), PathNames.Canonical(row.Path));
    }

    [Fact]
    public void DocumentSymbolsIndentMembersAndAFilterKeepsTheirContainers()
    {
        string json = """
            [{ "name": "Main", "kind": 5, "range": { "start": { "line": 2, "character": 0 }, "end": { "line": 9, "character": 1 } },
               "selectionRange": { "start": { "line": 2, "character": 6 }, "end": { "line": 2, "character": 10 } },
               "children": [
                 { "name": "run()", "detail": ": void", "kind": 6, "range": { "start": { "line": 3, "character": 4 }, "end": { "line": 3, "character": 18 } },
                   "selectionRange": { "start": { "line": 3, "character": 9 }, "end": { "line": 3, "character": 12 } } },
                 { "name": "count", "kind": 8, "range": { "start": { "line": 4, "character": 4 }, "end": { "line": 4, "character": 14 } },
                   "selectionRange": { "start": { "line": 4, "character": 8 }, "end": { "line": 4, "character": 13 } } }
               ] }]
            """;
        IReadOnlyList<SymbolRow> outline = EditorWindow.DocumentSymbols(JsonDocument.Parse(json).RootElement, "Main.java");
        Assert.Equal([("Main", 0), ("run()", 1), ("count", 1)], outline.Select(r => (r.Name, r.Depth)));
        Assert.Equal(new TextPosition(3, 9), outline[1].Position);
        Assert.True(outline[0].Spans(7));
        Assert.Equal(["Main", "count"], EditorWindow.FilterOutline(outline, "cou").Select(r => r.Name));
    }

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

    /// <summary>The popup of a kind once it shows over the window, after the work its action began.</summary>
    private static async Task<T> Shown<T>(EditorWindow window)
        where T : Control
    {
        for (int i = 0; i < 200 && window.SearchShown is not T; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<T>(window.SearchShown);
    }

    /// <summary>A query put in a search popup, its TextChanged heard as the dispatcher raises it.</summary>
    private static void Typed<T>(SearchPopup<T> popup, string query)
        where T : class
    {
        popup.Query = query;
        Dispatcher.UIThread.RunJobs();
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
