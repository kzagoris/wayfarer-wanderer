using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell.Navigation;

namespace WayfarerWanderer.Tests;

/// <summary>Getting about the files open, ported from Passenger v0.4.2: the Switcher, the navigation bar over the editor,
/// a word's places lit by a double click, Ctrl+click's links and declarations, and Read as Text.</summary>
public sealed class EditorNavigationTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("wayfarer-editor-navigation-").FullName);
    private readonly string main;
    private readonly string service;

    public EditorNavigationTests()
    {
        Write("pom.xml", "<project />");
        main = Write("src/main/java/demo/Main.java", "package demo;\n\nclass Main {\n    int count;\n\n    void run() {\n        count = count + 1;\n    }\n}\n");
        service = Write("src/main/java/demo/OrderService.java", "package demo;\n\nclass OrderService {}\n");
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
    public async Task CtrlTabPressedAndLetGoGoesBackToTheFileShownBeforeAndHeldShowsTheSwitcher()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            window.OpenSource(main);
            window.OpenSource(service);
            Assert.Equal(["OrderService.java", "Main.java"], window.SwitcherRows().Where(r => r.Path is not null).Select(r => r.Name));
            Assert.Contains(window.SwitcherRows(), r => r.Window?.Title == "Project");

            // Pressed and let go at once: the file used before, and again back.
            CtrlTab(window);
            LetGoOfCtrl(window);
            Assert.Equal(main, window.SourcePath);
            CtrlTab(window);
            LetGoOfCtrl(window);
            Assert.Equal(service, window.SourcePath);

            // Held: the popup shows after a moment, Tab moves its choice, and letting go goes to it.
            CtrlTab(window);
            Assert.Equal(1, window.SwitchingTo);
            SwitcherPopup popup = await Shown<SwitcherPopup>(window);
            Assert.Equal("Main.java", popup.Selected!.Name);
            CtrlTab(window, shift: true);
            Assert.Equal("OrderService.java", popup.Selected!.Name);
            CtrlTab(window);
            CtrlTab(window);
            // Past the files, the tool windows by name.
            WayfarerWanderer.App.Shell.Docking.ToolWindow tool = Assert.IsType<WayfarerWanderer.App.Shell.Docking.ToolWindow>(popup.Selected!.Window);
            Assert.Equal(window.SwitcherRows().Where(r => r.Window is not null).Select(r => r.Name).Order(StringComparer.CurrentCultureIgnoreCase).First(), tool.Title);
            LetGoOfCtrl(window);
            Assert.Null(window.SearchShown);
            Assert.Null(window.SwitchingTo);
            Assert.True(window.ToolDock.IsShown(tool.Id));

            // Asked for from Find Action, it waits for Enter.
            Assert.True(window.RunFromMenu(WayfarerWanderer.App.Shell.AppActions.Switcher));
            SwitcherPopup asked = Assert.IsType<SwitcherPopup>(window.SearchShown);
            asked.Choose();
            Assert.Equal(main, window.SourcePath);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheNavigationBarNamesThePathThenWhereTheCaretIsAndListsWhatStandsBeside()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            // A file no server outlines: the path alone.
            window.OpenSource(Path.Combine(root, "pom.xml"));
            Assert.True(window.NavigationBar.Bar.IsVisible);
            Assert.Equal(["pom.xml"], window.NavigationBar.Shown);

            window.OpenSource(main);
            Assert.Equal(["java", "demo", "Main.java"], window.NavigationBar.Shown);
            // The outline the Java server gives the status bar's path, given the bar.
            window.NavigationBar.ShowOutline(main, EditorWindow.DocumentSymbols(JsonDocument.Parse(MainOutline).RootElement, main));
            window.Editor.GoTo(new TextPosition(6, 8));
            await Settled(window, () => window.NavigationBar.Shown.Count == 5);
            Assert.Equal(["java", "demo", "Main.java", "Main", "run()"], window.NavigationBar.Shown);
            Assert.True(window.NavigationBar.NamesOutline);

            // A member's part lists the type's members, it chosen; another chosen is gone to.
            window.NavigationBar.Buttons[4].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SymbolPopup list = await Shown<SymbolPopup>(window);
            await list.SettledAsync();
            Assert.Equal("Members of Main", list.Title);
            Assert.Equal(["count", "run()"], list.Rows.Select(r => r.Name));
            Assert.Equal("run()", list.Selected!.Name);
            list.Selected = list.Rows[0];
            list.Choose();
            Assert.Equal(new TextPosition(3, 8), window.Editor.CaretPosition);
            await Settled(window, () => window.NavigationBar.Shown.Count == 5 && window.NavigationBar.Shown[4] == "count");

            // Turned off, the bar hides and Jump to Navigation Bar lists the caret's part's neighbours instead.
            window.NavigationBar.IsOn = false;
            Assert.False(window.NavigationBar.Bar.IsVisible);
            Assert.True(window.RunFromMenu(WayfarerWanderer.App.Shell.AppActions.JumpToNavigationBar));
            SymbolPopup jumped = await Shown<SymbolPopup>(window);
            Assert.Equal("Members of Main", jumped.Title);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ADoubleClickedWordLightsItsOtherPlacesInTheFileAndOnTheStripe()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            window.OpenSource(main);
            window.UpdateLayout();
            CodeEditor editor = window.Editor;
            int at = editor.Document.Snapshot.GetOffset(new TextPosition(6, 10));
            Avalonia.Point point = editor.TranslatePoint(editor.PointOf(at) + new Avalonia.Vector(2, editor.LineHeight / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.Equal("count", editor.Document.Snapshot.GetText(editor.Selections.Primary.Range));

            await Settled(window, () => editor.UsageHighlights.Count == 3);
            TextSnapshot text = editor.Document.Snapshot;
            Assert.All(editor.UsageHighlights, u => Assert.Equal("count", text.GetText(u.Range)));
            Assert.Equal([3, 6, 6], editor.UsageHighlights.Select(u => text.GetLineFromOffset(u.Range.Start)));
            await Settled(window, () => editor.StripeMarks.Count(m => m.Kind is StripeMarkKind.Usage) == 3);

            // The caret off the word takes them away; an edit too, until a word is chosen again.
            editor.GoTo(new TextPosition(0, 0));
            Assert.Empty(editor.UsageHighlights);
        }
        finally
        {
            window.Close();
        }
    }

    private const string MainOutline = """
        [{ "name": "demo", "kind": 4, "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 13 } },
           "selectionRange": { "start": { "line": 0, "character": 8 }, "end": { "line": 0, "character": 12 } } },
         { "name": "Main", "kind": 5, "range": { "start": { "line": 2, "character": 0 }, "end": { "line": 8, "character": 1 } },
           "selectionRange": { "start": { "line": 2, "character": 6 }, "end": { "line": 2, "character": 10 } },
           "children": [
             { "name": "count", "kind": 8, "range": { "start": { "line": 3, "character": 4 }, "end": { "line": 3, "character": 14 } },
               "selectionRange": { "start": { "line": 3, "character": 8 }, "end": { "line": 3, "character": 13 } } },
             { "name": "run()", "detail": ": void", "kind": 6, "range": { "start": { "line": 5, "character": 4 }, "end": { "line": 7, "character": 5 } },
               "selectionRange": { "start": { "line": 5, "character": 9 }, "end": { "line": 5, "character": 12 } } }
           ] }]
        """;

    /// <summary>Waits, running the window's work, until a condition holds; fails after two seconds.</summary>
    private static async Task Settled(EditorWindow window, Func<bool> holds)
    {
        for (int i = 0; i < 200 && !holds(); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(holds());
    }

    private static void CtrlTab(EditorWindow window, bool shift = false)
    {
        window.KeyPress(Key.Tab, RawInputModifiers.Control | (shift ? RawInputModifiers.Shift : RawInputModifiers.None), PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void LetGoOfCtrl(EditorWindow window)
    {
        window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
        Dispatcher.UIThread.RunJobs();
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

    private string Write(string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return Path.GetFullPath(path);
    }
}
