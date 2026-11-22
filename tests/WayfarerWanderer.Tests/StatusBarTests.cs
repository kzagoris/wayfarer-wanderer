using System.Text;
using Avalonia.Headless.XUnit;
using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell.Workspace;
using A = WayfarerWanderer.App.Shell.AppActions;

namespace WayfarerWanderer.Tests;

/// <summary>The status bar, as Passenger's and IntelliJ IDEA's: the path, the caret, the line separator, the encoding, the
/// indentation, the language and the read-only lock, and the widgets' changes of the file.</summary>
public sealed class StatusBarTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("wayfarer-status-").FullName);

    public StatusBarTests() => Write("pom.xml", "<project />");

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
    public void FileIndentPrefersTheChoiceThenTheEditorConfigThenTheFileThenTheDefault()
    {
        IndentOptions tabs = new(4, true, 4);
        IndentOptions two = new(2, false, 4);
        Assert.Equal(IndentSource.Default, FileIndent.Choose(EditorConfig.None, null, FileIndent.Default).Source);
        Assert.Equal(new FileIndent(tabs, IndentSource.Detected), FileIndent.Choose(EditorConfig.None, tabs, FileIndent.Default));
        Assert.Equal(IndentSource.Chosen, FileIndent.Choose(EditorConfig.None, tabs, FileIndent.Default, two).Source);
        Assert.Equal("Tab", new FileIndent(tabs, IndentSource.Detected).Shown);
        Assert.Equal("2 spaces, chosen", new FileIndent(two, IndentSource.Chosen).Shown);
        Assert.Equal("4 spaces, from .editorconfig", new FileIndent(FileIndent.Default, IndentSource.EditorConfig).Shown);
    }

    [Fact]
    public void TextBlockLinesAreTheLinesThatBeginInsideATextBlock()
    {
        string text = "class A {\n  String s = \"\"\"\n    kept\n      too\n    \"\"\";\n  String t = \"a\\\"\"\"\"; // \"\"\"\n  int x;\n}\n";
        OpenDocument document = new(Path.Combine(root, "A.java"), new TextDocument(text), new FileText(text, new UTF8Encoding(false), false));
        Assert.Equal([2, 3, 4], document.TextBlockLines(document.Text.Snapshot)!.Order());
        Assert.Null(new OpenDocument(Path.Combine(root, "a.txt"), new TextDocument(text), new FileText(text, new UTF8Encoding(false), false)).TextBlockLines(document.Text.Snapshot));
    }

    [AvaloniaFact]
    public async Task TheWidgetsNameTheShownFilesCaretSeparatorEncodingIndentAndLanguage()
    {
        string main = Path.Combine(root, "src", "main", "java", "demo", "Main.java");
        WriteBytes(main, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("package demo;\r\n\r\nclass Main {\r\n\tvoid run() {\r\n\t\tint x;\r\n\t}\r\n}\r\n")]);
        EditorWindow window = await OpenAsync();
        try
        {
            Assert.Equal("", window.CaretShown);
            window.OpenSource(main);
            Assert.Equal("1:1", window.CaretShown);
            Assert.Equal("CRLF", window.LineSeparatorShown);
            Assert.Equal("UTF-8 BOM", window.EncodingShown);
            Assert.Equal("Tab", window.IndentShown);
            Assert.True(window.Editor.Indent.UseTabs);
            Assert.Equal("Java", window.LanguageShown);
            Assert.False(window.ReadOnlyShown);
            Assert.Equal(["java", "demo", "Main.java"], window.Breadcrumbs);

            window.Editor.Selections = SelectionSet.Single(new Selection(window.Editor.Document.Snapshot.GetOffset(new TextPosition(2, 0)), window.Editor.Document.Snapshot.GetOffset(new TextPosition(2, 5))));
            Assert.Equal("3:6 (5 chars)", window.CaretShown);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AnEditorConfigsIndentIsShownAsItsAndTheEditorIndentsSo()
    {
        Write(".editorconfig", "root = true\n\n[*.java]\nindent_style = space\nindent_size = 2\n");
        string main = Write("Main.java", "class Main {\n\tvoid run() {}\n\tvoid stop() {}\n}\n");
        EditorWindow window = await OpenAsync();
        try
        {
            window.OpenSource(main);
            Assert.Equal("2 spaces, from .editorconfig", window.IndentShown);
            Assert.Equal(new IndentOptions(2, false, 2), window.Editor.Indent);
            Assert.Equal("LF", window.LineSeparatorShown);
            Assert.Equal("UTF-8", window.EncodingShown);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task LineSeparatorsConvertAndWriteTheFileAndUndoWritesItBack()
    {
        string main = Write("Main.java", "class Main {\r\n}\r\n");
        EditorWindow window = await OpenAsync();
        try
        {
            window.OpenSource(main);
            Assert.True(window.Actions.TryExecute(A.LineSeparatorsLf));
            Assert.Equal("LF", window.LineSeparatorShown);
            Assert.Equal("class Main {\n}\n", File.ReadAllText(main));
            Assert.False(window.Editor.Document.IsModified);

            window.Editor.Document.Undo();
            Assert.Equal("CRLF", window.LineSeparatorShown);
            Assert.Equal("class Main {\r\n}\r\n", File.ReadAllText(main));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ChoosingUtf8OverUtf8BomRemovesTheBomAndConvertingToLatin1AsksFirst()
    {
        string main = Path.Combine(root, "Main.java");
        WriteBytes(main, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("class Café {}\n")]);
        EditorWindow window = await OpenAsync();
        try
        {
            window.OpenSource(main);
            OpenDocument document = window.ShownDocument!;
            await window.ChooseEncodingAsync(document, FileEncoding.Utf8);
            Assert.Equal("UTF-8", window.EncodingShown);
            Assert.Equal(Encoding.UTF8.GetBytes("class Café {}\n"), File.ReadAllBytes(main));

            List<string> asked = [];
            window.EncodingAnswer = (_, to) =>
            {
                asked.Add(to.Name);
                return "Convert";
            };
            await window.ChooseEncodingAsync(document, FileEncoding.Latin1);
            Assert.Equal(["ISO-8859-1"], asked);
            Assert.Equal("ISO-8859-1", window.EncodingShown);
            Assert.Equal(Encoding.Latin1.GetBytes("class Café {}\n"), File.ReadAllBytes(main));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ConvertingIndentationToSpacesLeavesTextBlocksAndIsChosenForTheFile()
    {
        string main = Write("Main.java", "class Main {\n\tString s = \"\"\"\n\t\tkept\n\t\t\"\"\";\n\tvoid run() {\n\t\tint x;\n\t}\n}\n");
        EditorWindow window = await OpenAsync();
        try
        {
            window.OpenSource(main);
            Assert.Equal("Tab", window.IndentShown);
            Assert.True(window.Actions.TryExecute(A.ConvertIndentationToSpaces));
            Assert.Equal("class Main {\n    String s = \"\"\"\n\t\tkept\n\t\t\"\"\";\n    void run() {\n        int x;\n    }\n}\n", window.Editor.Document.Snapshot.ToString());
            Assert.Equal("4 spaces, chosen", window.IndentShown);
            Assert.Contains("inside text blocks", window.StatusText);

            window.Editor.Document.Undo();
            Assert.Equal("Tab", window.IndentShown);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AReadOnlyFileShowsTheLockAndRefusesChangesToHowItIsStored()
    {
        string main = Write("Main.java", "class Main {}\n");
        File.SetAttributes(main, FileAttributes.ReadOnly);
        EditorWindow window = await OpenAsync();
        try
        {
            window.OpenSource(main);
            Assert.True(window.ReadOnlyShown);
            Assert.True(window.Editor.IsReadOnly);
            Assert.False(window.Actions.TryExecute(A.LineSeparatorsCrlf));
            Assert.Equal("Main.java is read-only", window.Actions.Registry.WhyNot(A.FileEncoding, window.Actions.Registry.Context()));
        }
        finally
        {
            window.Close();
        }
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

    private string Write(string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}
