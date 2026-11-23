using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Explorer;
using A = WayfarerWanderer.App.Shell.AppActions;
using E = Passenger.Editor.EditorActions;

namespace WayfarerWanderer.Tests;

/// <summary>The Explorer's row menu, its keys, its file operations and its drags, in a window as the person has it.</summary>
public sealed class ExplorerActionsTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("wayfarer-explorer-actions-").FullName);
    private readonly string trash = Directory.CreateTempSubdirectory("wayfarer-trash-").FullName;

    public ExplorerActionsTests()
    {
        Write("pom.xml", "<project />");
        Write("app/pom.xml", "<project />");
        Write("app/src/main/java/demo/Main.java", "class Main {}");
        Write("docs/guide.md", "# guide");
        Write("README.md", "# demo");
        Trash.Folder = trash;
    }

    public void Dispose()
    {
        Trash.Folder = null;
        foreach (string folder in (string[])[root, trash])
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A watcher still letting go: the temporary folder is the system's to clear.
            }
        }
    }

    [AvaloniaFact]
    public async Task FolderMenuOffersAddEditPathAndOpenIn()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            ExplorerNode app = Choose(window, "app");
            IReadOnlyList<Control> menu = window.Explorer.MenuFor!(app)!;
            Assert.Equal(["Add", "Edit", "Copy Path", "Open In"], Headers(menu));
            Assert.Equal(["New File…", "New Folder…", "Java/Kotlin Class…"], Headers(Sub(menu, "Add")));
            Assert.Equal(["Cut", "Copy", "Paste", "Delete…", "Rename…"], Headers(Sub(menu, "Edit")));
            // Nothing was cut or copied yet, so Paste says so by being off.
            Assert.False(Item(Sub(menu, "Edit"), E.Paste).IsEnabled);
            Assert.True(Item(Sub(menu, "Edit"), E.Delete).IsEnabled);

            // The folder opened is never cut, deleted or renamed from inside itself.
            ExplorerNode top = window.Explorer.Tree.Rows[0];
            window.Explorer.RowList.SelectedItem = top;
            Assert.Equal(["Paste"], Headers(Sub(window.Explorer.MenuFor!(top)!, "Edit")));

            // A right-click (or the menu key) opens it on the row.
            Assert.True(window.Explorer.OpenMenuOn(app));
            Assert.NotNull(window.Explorer.OpenMenu);
            window.Explorer.OpenMenu!.Close();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task NewFileAsksItsNameMakesItAndOpensIt()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            Click(Item(Sub(window.Explorer.MenuFor!(Choose(window, "docs"))!, "Add"), A.NewFile));
            NamePopup popup = Assert.IsType<NamePopup>(window.Asking);
            popup.Text = "guide.md";
            await popup.SubmitAsync();
            // A name taken keeps the popup open, saying why.
            Assert.Equal("guide.md is already there", popup.Problem);
            popup.Text = "notes/today.txt";
            await popup.SubmitAsync();

            string made = Path.Combine(root, "docs", "notes", "today.txt");
            Assert.True(File.Exists(made));
            Assert.Null(window.Asking);
            Assert.Equal(made, window.SourcePath);
            Assert.Equal(made, window.Explorer.Selected?.Path);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task NewJavaClassIsDeclaredInItsFoldersPackage()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            window.Explorer.Reveal(Path.Combine(root, "app", "src", "main", "java", "demo"));
            Click(Item(Sub(window.Explorer.MenuFor!(window.Explorer.Selected!)!, "Add"), A.NewType));
            NamePopup popup = Assert.IsType<NamePopup>(window.Asking);
            Assert.Equal("Java Class", popup.Kind);
            popup.Text = "greet/Greeter";
            await popup.SubmitAsync();

            string made = Path.Combine(root, "app", "src", "main", "java", "demo", "greet", "Greeter.java");
            Assert.Equal("package demo.greet;\n\npublic class Greeter {\n}\n", File.ReadAllText(made).ReplaceLineEndings("\n"));
            Assert.Equal(made, window.SourcePath);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RenameMovesTheFileAndTheEditorFollowsIt()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            string main = Path.Combine(root, "app", "src", "main", "java", "demo", "Main.java");
            window.OpenSource(main);
            window.Explorer.Reveal(main);
            Click(Item(Sub(window.Explorer.MenuFor!(window.Explorer.Selected!)!, "Edit"), A.Rename));
            NamePopup popup = Assert.IsType<NamePopup>(window.Asking);
            Assert.Equal("Main.java", popup.Text);
            popup.Text = "Entry.java";
            await popup.SubmitAsync();

            string renamed = Path.Combine(Path.GetDirectoryName(main)!, "Entry.java");
            Assert.False(File.Exists(main));
            Assert.True(File.Exists(renamed));
            Assert.Equal(renamed, window.SourcePath);
            Assert.Equal("Entry.java", window.Explorer.Selected?.Name);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DeleteKeyAsksThenMovesTheRowToTheTrash()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            string readme = Path.Combine(root, "README.md");
            window.OpenSource(readme);
            Choose(window, "README.md");
            window.Explorer.FocusRows();
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);

            Control asked = Assert.IsAssignableFrom<Control>(window.Asking);
            Assert.True(File.Exists(readme));
            Click(asked.GetVisualDescendants().Concat(asked.GetLogicalDescendants()).OfType<Button>().First(b => Equals(b.Content, "Delete")));
            Dispatcher.UIThread.RunJobs();

            Assert.False(File.Exists(readme));
            Assert.True(File.Exists(Path.Combine(trash, "README.md")));
            Assert.DoesNotContain(window.Explorer.Tree.Rows, r => r.Name == "README.md");
            Assert.Null(window.SourcePath);
            Assert.Equal("Deleted README.md", window.Explorer.Said);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task UndoBringsADeleteBackFromTheTrashAndRedoDeletesItAgain()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            string guide = Path.Combine(root, "docs", "guide.md");
            Assert.True(await window.DeleteAsync([guide]));
            Assert.False(File.Exists(guide));

            Choose(window, "docs");
            window.Explorer.FocusRows();
            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            await Settle();
            Assert.True(File.Exists(guide));
            Assert.Equal($"Brought guide.md back from the {(OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash")}", window.Explorer.Said);

            window.Explorer.FocusRows();
            window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
            await Settle();
            Assert.False(File.Exists(guide));
            Assert.Equal("Deleted guide.md", window.Explorer.Said);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task APasteNeverReplacesTheFolderHoldingIt()
    {
        Write("docs/docs/inner.md", "# inner");
        EditorWindow window = await OpenAsync();
        try
        {
            string inner = Path.Combine(root, "docs", "docs");
            Assert.Empty(await window.PutAsync([inner], root, copy: false));
            Assert.True(File.Exists(Path.Combine(inner, "inner.md")));
            Assert.Equal("docs would replace the folder holding it", window.Explorer.Said);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CutThenPasteMovesTheFileIntoTheFolder()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            ExplorerNode readme = Choose(window, "README.md");
            window.Explorer.FocusRows();
            window.KeyPress(Key.X, RawInputModifiers.Control, PhysicalKey.X, "x");
            await Settle();
            Assert.True(readme.IsCut);

            Choose(window, "docs");
            window.Explorer.FocusRows();
            window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            await Settle();

            Assert.False(File.Exists(Path.Combine(root, "README.md")));
            Assert.True(File.Exists(Path.Combine(root, "docs", "README.md")));
            Assert.Equal("Moved README.md to docs", window.Explorer.Said);
            Assert.Equal(Path.Combine(root, "docs", "README.md"), window.Explorer.Selected?.Path);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DraggingARowOntoAFolderMovesItThere()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            ExplorerNode readme = Row(window, "README.md");
            ExplorerNode docs = Row(window, "docs");
            Point from = Centre(window, readme);
            Point to = Centre(window, docs);
            window.MouseMove(from, RawInputModifiers.None);
            window.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(new Point(from.X, from.Y - 4), RawInputModifiers.LeftMouseButton);
            window.MouseMove(new Point(to.X, (from.Y + to.Y) / 2), RawInputModifiers.LeftMouseButton);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.MouseUp(to, MouseButton.Left, RawInputModifiers.None);
            await Settle();

            Assert.False(File.Exists(Path.Combine(root, "README.md")));
            Assert.True(File.Exists(Path.Combine(root, "docs", "README.md")));
            Assert.Equal("Moved README.md to docs", window.Explorer.Said);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PuttingACopyLeavesTheOriginalAndAskedReplacesATakenName()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            string readme = Path.Combine(root, "README.md");
            string docs = Path.Combine(root, "docs");
            IReadOnlyList<string> put = await window.PutAsync([readme], docs, copy: true);
            Assert.Equal([Path.Combine(docs, "README.md")], put);
            Assert.True(File.Exists(readme));

            // A folder never goes inside itself.
            Assert.Empty(await window.PutAsync([docs], Path.Combine(docs), copy: false));
            Assert.Empty(await window.PutAsync([root], docs, copy: false));
        }
        finally
        {
            window.Close();
        }
    }

    private async Task<EditorWindow> OpenAsync()
    {
        EditorWindow window = EditorApplication.CreateWindow(new TextDocument("class Scratch {}"));
        window.Width = 1000;
        window.Height = 700;
        window.Show();
        await window.OpenWorkspaceAsync(root, TestContext.Current.CancellationToken);
        window.ToolDock.Show(ToolWindowIds.Explorer);
        window.UpdateLayout();
        return window;
    }

    private static ExplorerNode Row(EditorWindow window, string name) => window.Explorer.Tree.Rows.Single(r => r.Name == name);

    private static ExplorerNode Choose(EditorWindow window, string name)
    {
        ExplorerNode row = Row(window, name);
        window.Explorer.RowList.SelectedItem = row;
        return row;
    }

    private static Point Centre(EditorWindow window, ExplorerNode row)
    {
        window.Explorer.RowList.ScrollIntoView(row);
        window.UpdateLayout();
        Control container = window.Explorer.RowList.ContainerFromItem(row)!;
        return container.TranslatePoint(new Point(container.Bounds.Width / 3, container.Bounds.Height / 2), window)!.Value;
    }

    private static async Task Settle()
    {
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static IReadOnlyList<string> Headers(IEnumerable<Control> items) => [.. items.OfType<MenuItem>().Select(i => (string)i.Header!)];

    private static IReadOnlyList<Control> Sub(IEnumerable<Control> items, string header) =>
        [.. ((IEnumerable<object>)items.OfType<MenuItem>().Single(i => (string?)i.Header == header).ItemsSource!).OfType<Control>()];

    private static MenuItem Item(IEnumerable<Control> items, string id) => items.OfType<MenuItem>().Single(i => (string?)i.Tag == id);

    private static void Click(Control control) => control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));

    private void Write(string relative, string text)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
