using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>IntelliJ IDEA-style Quick Documentation and symbol hover: Markdown/Javadoc parsing, synchronous TextMate
/// colouring, parameter-per-line signature formatting, interactive card lifecycle, menu &amp; shortcut wiring.</summary>
[Collection(JvmTrustCollection.Name)]
public sealed class QuickDocTests
{
    private const string IoPrintlnMarkdown = """
        ```java
        public static void java.lang.IO.println(Object obj)
        ```

        Writes a string representation of the specified object and then writes a line separator to the standard output.

        The effect is as if `println(obj)` had been called on `System.out`.

         *  **Parameters:**
            *  **obj** the object to print, may be `null`
         *  **Since:**
            *  25
        """;

    [Fact]
    public void ParsesJdtMethodHoverIntoOwnerSignatureParagraphsAndTagSections()
    {
        QuickDoc doc = QuickDocParser.Parse(IoPrintlnMarkdown, "java");

        Assert.False(doc.IsEmpty);
        Assert.Equal("java.lang.IO", doc.Owner);
        Assert.Equal("public static void println(Object obj)", doc.Signature);
        Assert.Equal("java", doc.SignatureLanguage);

        Assert.Equal(4, doc.Body.Count);
        QuickDocParagraph first = Assert.IsType<QuickDocParagraph>(doc.Body[0]);
        Assert.Contains("Writes a string representation", first.PlainText(), StringComparison.Ordinal);

        QuickDocParagraph second = Assert.IsType<QuickDocParagraph>(doc.Body[1]);
        Assert.Contains(second.Inlines, inline => inline is QuickDocCodeSpan { Value: "println(obj)" });
        Assert.Contains(second.Inlines, inline => inline is QuickDocCodeSpan { Value: "System.out" });

        QuickDocTagSection parameters = Assert.IsType<QuickDocTagSection>(doc.Body[2]);
        Assert.Equal("Params:", parameters.Label);
        QuickDocTagItem param = Assert.Single(parameters.Items);
        Assert.Equal("obj", param.Name);
        Assert.Contains(param.Description, inline => inline is QuickDocCodeSpan { Value: "null" });

        QuickDocTagSection since = Assert.IsType<QuickDocTagSection>(doc.Body[3]);
        Assert.Equal("Since:", since.Label);
        Assert.Equal("25", Assert.Single(since.Items).PlainText());
    }

    [Fact]
    public void ExtractsOwnerFromJdtLocalsFieldsConstructorsAndKotlinPackageFences()
    {
        Assert.Equal(("example.Probe.run()", "int max"), QuickDocParser.ExtractHeader("int max - example.Probe.run()", "java"));
        Assert.Equal(("java.lang.System", "PrintStream out"), QuickDocParser.ExtractHeader("PrintStream java.lang.System.out", "java"));
        Assert.Equal(("java.util.ArrayList", "ArrayList(int initialCapacity)"), QuickDocParser.ExtractHeader("java.util.ArrayList.ArrayList(int initialCapacity)", "java"));
        Assert.Equal(("java.util.List", "<T> java.util.List<T> of(T e1)"), QuickDocParser.ExtractHeader("<T> java.util.List<T> java.util.List.of(T e1)", "java"));
        Assert.Equal(("kotlin.io", "public fun println(message: Any?): Unit"), QuickDocParser.ExtractHeader("package kotlin.io\npublic fun println(message: Any?): Unit", "kotlin"));
    }

    [Fact]
    public void ParsesKotlinFourBacktickFenceDividersAndAtTags()
    {
        const string kotlin = """
            ````kotlin
            package kotlin.io
            public fun println(message: Any?): Unit
            ````
            ---
            Prints the given `message` and the line separator to the standard output stream.

            @param message the value to print
            @since 1.0
            """;

        QuickDoc doc = QuickDocParser.Parse(kotlin, "kotlin");
        Assert.Equal("kotlin.io", doc.Owner);
        Assert.Equal("public fun println(message: Any?): Unit", doc.Signature);
        Assert.Equal("kotlin", doc.SignatureLanguage);
        Assert.Equal(3, doc.Body.Count);
        Assert.IsType<QuickDocParagraph>(doc.Body[0]);
        QuickDocTagSection param = Assert.IsType<QuickDocTagSection>(doc.Body[1]);
        Assert.Equal("Params:", param.Label);
        Assert.Equal("message", Assert.Single(param.Items).Name);
        QuickDocTagSection since = Assert.IsType<QuickDocTagSection>(doc.Body[2]);
        Assert.Equal("Since:", since.Label);
        Assert.Equal("1.0", Assert.Single(since.Items).PlainText());
    }

    [Fact]
    public void ExtractsJdtSourceLinkAndDecodesJavadocHtmlTags()
    {
        const string markdown = """
            ```java
            int max - example.Probe.run()
            ```

            Computes <b>maximum</b> of <code>a &lt; b</code>.<br/>See <a href="https://example.com/docs">docs</a>.

            Source: *[service](file:///C:/p/Probe.java#11)*
            """;

        QuickDoc doc = QuickDocParser.Parse(markdown, "java");
        Assert.Equal("example.Probe.run()", doc.Owner);
        Assert.Equal("int max", doc.Signature);
        Assert.Equal("service", doc.SourceLabel);
        Assert.Equal("file:///C:/p/Probe.java#11", doc.SourceTarget);

        QuickDocParagraph body = Assert.IsType<QuickDocParagraph>(Assert.Single(doc.Body));
        Assert.Contains(body.Inlines, inline => inline is QuickDocBold);
        Assert.Contains(body.Inlines, inline => inline is QuickDocCodeSpan { Value: "a < b" });
        Assert.Contains(body.Inlines, inline => inline is QuickDocBreak);
        Assert.Contains(body.Inlines, inline => inline is QuickDocLink { Target: "https://example.com/docs" });
    }

    [Fact]
    public void SyntaxHighlighterColoursJavaAndMapsBrokenSignatureParameters()
    {
        const string signature = "public static void println(Object obj)";
        var language = QuickDocSyntax.LanguageOf("java");
        Assert.NotNull(language);

        IReadOnlyList<SyntaxSpan> spans = QuickDocSyntax.Tokenize(signature, language);
        Assert.Contains(spans, s => s.Kind == TokenKind.Keyword && signature.Substring(s.Start, s.Length) == "public");
        Assert.Contains(spans, s => s.Kind == TokenKind.Method && signature.Substring(s.Start, s.Length) == "println");

        QuickDocSyntax.FormattedSignature? formatted = QuickDocSyntax.BreakParameters(signature);
        Assert.NotNull(formatted);
        Assert.Equal("public static void println(\n    Object obj\n)", formatted.Text);

        SyntaxSpan[] mapped = formatted.Map(spans);
        Assert.Contains(mapped, s => s.Kind == TokenKind.Method && formatted.Text.Substring(s.Start, s.Length) == "println");
        Assert.Null(QuickDocSyntax.BreakParameters("String toString()"));
    }

    [Fact]
    public void ParserNeverLoopsOnUnknownInlineColonsOrEmptyJavadocTagBullets()
    {
        const string tricky = """
            ```java
            void java.lang.IO.println(Object obj)
            ```

            Note: this line has a colon within 28 characters.

            For example: calling this writes to standard output.

            Source: java.base

             *  **Parameters:**

             *  ![icon](data:image/png;base64,AAAA)

             *  Normal bullet item
            """;

        QuickDoc doc = QuickDocParser.Parse(tricky, "java");
        Assert.False(doc.IsEmpty);
        Assert.Equal("java.lang.IO", doc.Owner);
        Assert.Equal("java.base", doc.SourceLabel);
        Assert.Contains("Note: this line has a colon", doc.PlainText(), StringComparison.Ordinal);
        Assert.Contains("Normal bullet item", doc.PlainText(), StringComparison.Ordinal);

        // Standalone JDT LS Javadoc tag header followed by parameter lines and plain Source footer.
        const string jdtJdkHover = """
            ```java
            void java.io.PrintStream.println(String arg0)
            ```

            Prints a String and then terminates the line.

            Parameters:
            `x` - The `String` to be printed.

            Source: *Java 21.0.10 (module: java.base)*
            """;
        QuickDoc jdkDoc = QuickDocParser.Parse(jdtJdkHover, "java");
        Assert.Equal("Java 21.0.10 (module: java.base)", jdkDoc.SourceLabel);
        Assert.Null(jdkDoc.SourceTarget);
        QuickDocTagSection paramSection = Assert.IsType<QuickDocTagSection>(jdkDoc.Body[1]);
        Assert.Equal("Params:", paramSection.Label);
        Assert.Equal("x", Assert.Single(paramSection.Items).Name);
    }

    [AvaloniaFact(SkipUnless = nameof(KotlinClassBridgeTests.RunLanguageAcceptance), SkipType = typeof(KotlinClassBridgeTests),
        Skip = "Set WAYFARER_LANGUAGE_ACCEPTANCE=1 and install JDT LS to verify real Java symbol hover.")]
    public async Task LiveJavaServerHoverOpensQuickDocCardWithoutHanging()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string root = WorkspaceImportTests.Copy("maven");
        string file = Path.Combine(root, "service/src/main/java/example/HoverProbe.java");
        const string source = """
            package example;

            class HoverProbe {
                void run() {
                    System.out.println("Hello from Wayfarer!");
                }
            }
            """;
        await File.WriteAllTextAsync(file, source, cancel);
        WorkspaceTrust.Trust(root);
        bool starts = EditorWindow.StartsLanguageServersOnOpen;
        EditorWindow.StartsLanguageServersOnOpen = true;
        EditorWindow window = EditorApplication.CreateWindow(null);
        window.Width = 1100;
        window.Height = 680;
        window.Show();
        try
        {
            await window.OpenWorkspaceAsync(root, cancel);
            window.OpenSource(file);
            int at = source.IndexOf("println", StringComparison.Ordinal) + 2;
            window.Editor.GoTo(window.Editor.Document.Snapshot.GetPosition(at));
            window.Editor.Focus();

            // Wait for JDT LS to answer Quick Documentation on System.out.println.
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            while (!window.QuickDocCard.IsOpen && clock.Elapsed < TimeSpan.FromMinutes(2))
            {
                await window.QuickDocumentationAsync();
                Dispatcher.UIThread.RunJobs();
                if (!window.QuickDocCard.IsOpen)
                {
                    await Task.Delay(500, cancel);
                }
            }

            Assert.True(window.QuickDocCard.IsOpen, "JDT LS hover never opened QuickDocCard");
            Assert.Contains("println", window.QuickDocCard.Text, StringComparison.Ordinal);

            if (Environment.GetEnvironmentVariable("WAYFARER_TEST_OUTPUT") is { Length: > 0 } outputDir)
            {
                Directory.CreateDirectory(outputDir);
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(Path.Combine(outputDir, "quick-doc-live-java.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Close();
            EditorWindow.StartsLanguageServersOnOpen = starts;
            await WayfarerWanderer.Jvm.ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
        }
    }

    [AvaloniaFact]
    public async Task QuickDocumentationActionShowsStickyCardDismissesOnEscapeAndEdit()
    {
        string temp = Directory.CreateTempSubdirectory("wayfarer-quickdoc-").FullName;
        string file = Path.Combine(temp, "Main.java");
        const string source = """
            void main() {
                int k = 3;
                IO.println("Hello from Wayfarer!");
                int y = k + 1;
            }
            """;
        await File.WriteAllTextAsync(file, source, TestContext.Current.CancellationToken);

        EditorWindow window = EditorApplication.CreateWindow(null);
        window.Width = 960;
        window.Height = 600;
        window.HoverProvider = (_, _, _) => Task.FromResult<string?>(IoPrintlnMarkdown);
        window.Show();
        try
        {
            window.OpenSource(file);
            // Place caret on `println`
            int at = source.IndexOf("println", StringComparison.Ordinal);
            window.Editor.GoTo(window.Editor.Document.Snapshot.GetPosition(at + 2));
            window.Editor.Focus();
            Dispatcher.UIThread.RunJobs();

            // View menu exposes Quick Documentation.
            Avalonia.Controls.MenuItem viewMenu = window.MainMenuNamed("View");
            Assert.Contains(viewMenu.Items.OfType<Avalonia.Controls.MenuItem>(), item => (string?)item.Tag == AppActions.QuickDocumentation);

            // Invoking Quick Documentation opens the sticky IDEA-style card.
            Assert.True(window.Actions.TryExecute(AppActions.QuickDocumentation));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.QuickDocCard.IsOpen);
            Assert.True(window.QuickDocCard.IsSticky);
            Assert.Equal("java.lang.IO", window.QuickDocCard.Document?.Owner);
            Assert.Contains("public static void println(Object obj)", window.QuickDocCard.Text, StringComparison.Ordinal);
            Assert.Contains("Writes a string representation", window.QuickDocCard.Text, StringComparison.Ordinal);
            Assert.Contains("Params: obj", window.QuickDocCard.Text, StringComparison.Ordinal);

            // Capture headless render when WAYFARER_TEST_OUTPUT is configured for visual verification.
            if (Environment.GetEnvironmentVariable("WAYFARER_TEST_OUTPUT") is { Length: > 0 } outputDir)
            {
                Directory.CreateDirectory(outputDir);
                window.QuickDocCard.Show(
                    QuickDocParser.Parse(IoPrintlnMarkdown, "java"),
                    new Point(window.Editor.PointOf(at).X, window.Editor.PointOf(at).Y + window.Editor.LineHeight + 4),
                    window.Editor.PointOf(at).Y,
                    footer: "< temurin-27 >",
                    sticky: true,
                    canGoToDeclaration: true);
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(Path.Combine(outputDir, "quick-doc-card.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            // Escape closes the card.
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.QuickDocCard.IsOpen);

            // Re-open and verify editing dismisses the card.
            await window.QuickDocumentationAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.QuickDocCard.IsOpen);

            window.Editor.Focus();
            window.KeyTextInput(" ");
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.QuickDocCard.IsOpen);

            // Non-sticky hover card opens for symbol hover and closes when Escape is pressed.
            window.Editor.Document.Undo();
            Dispatcher.UIThread.RunJobs();
            bool hovered = await window.ShowQuickDocAtAsync(at + 2, window.Editor.PointOf(at + 2), sticky: false, startServer: false);
            Dispatcher.UIThread.RunJobs();
            Assert.True(hovered);
            Assert.True(window.QuickDocCard.IsOpen);
            Assert.False(window.QuickDocCard.IsSticky);

            // Pressing Quick Documentation while hovered on the same position promotes the card to sticky.
            await window.QuickDocumentationAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.QuickDocCard.IsSticky);

            window.QuickDocCard.Close();
            Assert.False(window.QuickDocCard.IsOpen);
        }
        finally
        {
            window.Close();
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }
}
