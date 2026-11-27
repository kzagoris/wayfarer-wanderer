using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell.Languages;

namespace WayfarerWanderer.Tests;

public sealed class SyntaxColourTests
{
    [Theory]
    [InlineData("src/main/java/demo/Main.java", "java")]
    [InlineData("build.gradle.kts", "kotlin")]
    [InlineData("src/main/kotlin/App.kt", "kotlin")]
    [InlineData("build.gradle", "groovy")]
    [InlineData("Jenkinsfile", "groovy")]
    [InlineData("pom.xml", "xml")]
    [InlineData("app.iml", "xml")]
    [InlineData("application.yml", "yaml")]
    [InlineData("gradle.properties", "properties")]
    [InlineData(".editorconfig", "properties")]
    [InlineData("package.json", "json")]
    [InlineData("README.md", "markdown")]
    [InlineData("gradlew", "shellscript")]
    [InlineData("mvnw", "shellscript")]
    [InlineData(".gitignore", "ignore")]
    [InlineData(".gitattributes", "gitattributes")]
    [InlineData("notes.txt", "plaintext")]
    [InlineData("Main.class", "plaintext")]
    public void FilesAreInTheirLanguage(string path, string id) => Assert.Equal(id, Language.ForPath(path).Id);

    [Fact]
    public void EveryLanguageIsOneAThemeCanName() =>
        Assert.All(Language.Every, language => Assert.Contains(language.Id, LanguageIds.All));

    [Theory]
    [InlineData("Main.java", "public class Main { int count = 42; } // done", "class", TokenKind.Keyword)]
    [InlineData("Main.java", "public class Main { int count = 42; } // done", "42", TokenKind.Number)]
    [InlineData("Main.java", "public class Main { int count = 42; } // done", "// done", TokenKind.Comment)]
    [InlineData("App.kt", "fun main() { val greeting = \"hello\" }", "\"hello\"", TokenKind.String)]
    [InlineData("App.kt", "fun main() { val greeting = \"hello\" }", "fun", TokenKind.Keyword)]
    [InlineData("build.gradle", "plugins { id 'java' } // build", "// build", TokenKind.Comment)]
    [InlineData("build.gradle", "plugins { id 'java' } // build", "'java'", TokenKind.String)]
    [InlineData("pom.xml", "<project><artifactId>demo</artifactId></project>", "artifactId", TokenKind.Tag)]
    [InlineData("package.json", "{ \"name\": \"demo\" }", "\"name\"", TokenKind.JsonKey)]
    [InlineData("application.yml", "server:\n  port: 8080 # web", "server", TokenKind.YamlKey)]
    [InlineData("application.yml", "server:\n  port: 8080 # web", "# web", TokenKind.Comment)]
    [InlineData("gradle.properties", "# memory\norg.gradle.jvmargs=-Xmx2g", "# memory", TokenKind.Comment)]
    [InlineData("README.md", "# Demo\n\nText", "Demo", TokenKind.Heading)]
    [InlineData("gradlew", "#!/bin/sh\necho \"starting\" # go", "\"starting\"", TokenKind.String)]
    public async Task GrammarsColourTheirFiles(string path, string text, string needle, TokenKind kind)
    {
        TextDocument document = new(text);
        IHighlighter colours = Highlighters.ForPath(path, document) ?? throw new InvalidOperationException($"No colours for {path}");
        var highlighter = Assert.IsType<TextMateHighlighter>(colours);
        try
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (!highlighter.HasRead(document.Snapshot) && highlighter.Failure is null && waited.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            Assert.Null(highlighter.Failure);
            Assert.True(highlighter.HasRead(document.Snapshot), $"{path} was not coloured in time");

            int start = text.IndexOf(needle, StringComparison.Ordinal);
            IReadOnlyList<SyntaxSpan> spans = highlighter.GetSpans(document.Snapshot, 0, text.Length);
            Assert.Contains(spans, span => span.Kind == kind && span.Start <= start && span.End >= start + needle.Length);
        }
        finally
        {
            highlighter.Dispose();
        }
    }

    [Fact]
    public void PlainTextHasNoColours() => Assert.Null(Highlighters.ForPath("notes.txt", new TextDocument("words")));

    [Theory]
    [InlineData("pom.xml", "<project>\n  <dependencies>\n    <dependency/>\n  </dependencies>\n  <!-- a\n  b -->\n</project>", "...</project>", "...</dependencies>", "...-->")]
    [InlineData("package.json", "{\n  \"scripts\": [\n    \"build\"\n  ]\n}", "...}", "...]")]
    public async Task ScansFindFolds(string path, string text, params string[] placeholders)
    {
        TextDocument document = new(text);
        IFoldFinder finder = Highlighters.FoldsFor(Language.ForPath(path), document) ?? throw new InvalidOperationException($"No folds for {path}");
        try
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (finder.Folds.Count == 0 && waited.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            Assert.Equal(placeholders.Order(), finder.Folds.Select(f => f.Placeholder).Order());
        }
        finally
        {
            (finder as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void CodeHasNoFoldScan() => Assert.Null(Highlighters.FoldsFor(Language.Java, new TextDocument("class A {\n}")));

    /// <summary>A Java file opened in the window is shown in its grammar's colours, and a text file opened after it in none.</summary>
    [AvaloniaFact]
    public void OpenedFilesShowTheirLanguagesColours()
    {
        string source = Repo.At("tests/fixtures/jvm/maven/app/src/main/java/example/Main.java");
        string folder = Directory.CreateTempSubdirectory("wayfarer-colours-").FullName;
        string notes = Path.Combine(folder, "notes.txt");
        File.WriteAllText(notes, "class is only a word here");
        var window = EditorApplication.CreateWindow(null);
        window.Width = 900;
        window.Height = 420;
        window.Show();
        try
        {
            window.OpenSource(source);
            var highlighter = Assert.IsType<TextMateHighlighter>(window.Editor.Highlighter);
            Stopwatch waited = Stopwatch.StartNew();
            while (!highlighter.HasRead(window.Editor.Document.Snapshot) && highlighter.Failure is null && waited.Elapsed < TimeSpan.FromSeconds(30))
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Assert.Null(highlighter.Failure);
            TextSnapshot text = window.Editor.Document.Snapshot;
            Assert.Contains(highlighter.GetSpans(text, 0, text.Length), span => span.Kind == TokenKind.Keyword);

            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            string output = Environment.GetEnvironmentVariable("WAYFARER_TEST_OUTPUT") ??
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../test-results"));
            Directory.CreateDirectory(output);
            frame.Save(Path.Combine(output, "editor-java-colours.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

            window.OpenSource(notes);
            Assert.Null(window.Editor.Highlighter);
            window.OpenSource(source);
            Assert.Same(highlighter, window.Editor.Highlighter);

            // A POM's elements fold, and what was folded stays folded across a switch to another file and back.
            string pom = Repo.At("tests/fixtures/jvm/maven/pom.xml");
            window.OpenSource(pom);
            waited.Restart();
            while (window.Editor.FoldRegions.Count == 0 && waited.Elapsed < TimeSpan.FromSeconds(30))
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Assert.Contains(window.Editor.FoldRegions, region => region.Placeholder == "...</modules>");
            window.Editor.Decorations.SetFoldRegions([.. window.Editor.FoldRegions.Select(r => r with { Folded = r.Placeholder == "...</modules>" })]);
            window.OpenSource(source);
            Assert.Empty(window.Editor.FoldRegions);
            window.OpenSource(pom);
            Assert.Contains(window.Editor.FoldRegions, region => region.Placeholder == "...</modules>" && region.Folded);
            window.UpdateLayout();
            using var folded = window.CaptureRenderedFrame();
            Assert.NotNull(folded);
            folded.Save(Path.Combine(output, "editor-pom-folds.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        finally
        {
            window.Close();
            Directory.Delete(folder, recursive: true);
        }
    }
}
