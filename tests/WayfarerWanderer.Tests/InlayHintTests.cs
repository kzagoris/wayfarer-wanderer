using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>Parameter and type name hints from the JVM language servers: their answers placed as the editor draws them, the
/// settings that turn the servers' hints on, and (with the servers installed) the hints each gives for a file.</summary>
[Collection(JvmTrustCollection.Name)]
public sealed class InlayHintTests
{
    private const string JavaProbe = "package example;\n\nimport java.util.*;\n\nclass Probe {\n"
        + "    static int add(int left, int right) { return left + right; }\n"
        + "    void run() {\n"
        + "        var list = new ArrayList<String>();\n"
        + "        list.add(\"x\");\n"
        + "        int total = add(1, 2);\n"
        + "        java.util.function.Function<String, Integer> f = s -> s.length();\n"
        + "        var max = Math.max(total, 3);\n"
        + "    }\n}\n";

    private const string KotlinProbe = "package example\n\nfun add(left: Int, right: Int) = left + right\n\n"
        + "fun run() {\n"
        + "    val total = add(1, 2)\n"
        + "}\n";

    [Fact]
    public void JdtHintsAreReadByTheirWordsAndALambdaParametersTypeFollowsItsName()
    {
        // As JDT LS 1.57 answered for JavaProbe: no kinds, a lambda parameter's type before its name.
        const string answer = """
            [{"position":{"line":9,"character":24},"label":"left:","paddingRight":true},
             {"position":{"line":9,"character":27},"label":"right:","paddingRight":true},
             {"position":{"line":10,"character":57},"label":"String","paddingRight":true},
             {"position":{"line":11,"character":15},"label":": int","paddingLeft":true}]
            """;
        TextSnapshot text = TextSnapshot.From(JavaProbe);
        InlayHint[] hints = Hints(answer, text);

        Assert.Equal(4, hints.Length);
        Assert.Equal(new InlayHint(At(text, "add(1") + 4, "left:", PadRight: true, Kind: InlayHintKind.Parameter), hints[0]);
        Assert.Equal(new InlayHint(At(text, "add(1") + 7, "right:", PadRight: true, Kind: InlayHintKind.Parameter), hints[1]);
        Assert.Equal(new InlayHint(At(text, "s ->") + 1, ": String", Kind: InlayHintKind.Type), hints[2]);
        Assert.Equal(new InlayHint(At(text, "max =") + 3, ": int", Kind: InlayHintKind.Type), hints[3]);
    }

    [Fact]
    public void KotlinHintsKeepTheKindTheServerSaysAndTheirLabelsParts()
    {
        // As the Kotlin server 271 answered for KotlinProbe, its data left out.
        const string answer = """
            [{"position":{"line":2,"character":30},"label":[{"value":": "},{"value":"Int"}],"kind":1},
             {"position":{"line":5,"character":20},"label":[{"value":"left"},{"value":" ="}],"kind":2,"paddingRight":true},
             {"position":{"line":5,"character":23},"label":[{"value":"right"},{"value":" ="}],"kind":2,"paddingRight":true}]
            """;
        TextSnapshot text = TextSnapshot.From(KotlinProbe);
        InlayHint[] hints = Hints(answer, text);

        Assert.Equal(3, hints.Length);
        Assert.Equal(new InlayHint(At(text, " = left"), ": Int", Kind: InlayHintKind.Type), hints[0]);
        Assert.Equal(new InlayHint(At(text, "add(1") + 4, "left =", PadRight: true, Kind: InlayHintKind.Parameter), hints[1]);
        Assert.Equal(new InlayHint(At(text, "add(1") + 7, "right =", PadRight: true, Kind: InlayHintKind.Parameter), hints[2]);
    }

    [Fact]
    public void HintsOffTheTextOrWithoutWordsAreLeftOut()
    {
        const string answer = """
            [{"position":{"line":40,"character":0},"label":"far:"},
             {"position":{"line":0,"character":99},"label":"wide:"},
             {"position":{"line":0,"character":0},"label":"  "},
             {"position":{"line":0,"character":0}},
             {"position":{"line":0,"character":3},"label":"kept:"}]
            """;
        InlayHint[] hints = Hints(answer, TextSnapshot.From("one\ntwo\n"));
        Assert.Equal([new InlayHint(3, "kept:")], hints);
        Assert.Empty(Hints("null", TextSnapshot.From("one")));
    }

    [Fact]
    public void TheServersAreToldToGiveBothKindsOfHints()
    {
        JsonElement kotlin = JsonSerializer.SerializeToElement(KotlinInlayHints.Settings);
        // Each option by its dotted name, and nested as VS Code nests them.
        Assert.True(kotlin.GetProperty("hints.parameters").GetBoolean());
        Assert.True(kotlin.GetProperty("hints.settings.types.variable").GetBoolean());
        Assert.False(kotlin.GetProperty("hints.settings.lambda.return").GetBoolean());
        JsonElement nested = kotlin.GetProperty("hints");
        Assert.True(nested.GetProperty("parameters").GetBoolean());
        Assert.True(nested.GetProperty("settings").GetProperty("types").GetProperty("variable").GetBoolean());
        Assert.True(nested.GetProperty("type").GetProperty("function").GetProperty("parameter").GetBoolean());
        Assert.False(nested.GetProperty("call").GetProperty("chains").GetBoolean());

        JsonElement java = JsonSerializer.SerializeToElement(JdtInlayHints.Settings);
        Assert.Equal("literals", java.GetProperty("parameterNames").GetProperty("enabled").GetString());
        Assert.True(java.GetProperty("variableTypes").GetProperty("enabled").GetBoolean());
        Assert.True(java.GetProperty("parameterTypes").GetProperty("enabled").GetBoolean());
        Assert.Equal(true, JdtInlayHints.Value("java.inlayHints.variableTypes.enabled"));
        Assert.Null(JdtInlayHints.Value("java.home"));

        // JDT LS's signature help, off by default, says what a parameter name hint's card shows.
        Assert.True(JsonSerializer.SerializeToElement(JdtInlayHints.SignatureHelp).GetProperty("enabled").GetBoolean());
        Assert.Equal(true, JdtInlayHints.Value("java.signatureHelp.enabled"));
    }

    [Fact]
    public void TheKotlinServersImportEndingSaysItsHintsMayHaveChanged()
    {
        // As the Kotlin server 271 logged its Maven import: the last line ends it.
        Assert.True(JvmLanguageClient.ImportEnded(Json("""
            {"jsonrpc":"2.0","method":"intellij/importLog","params":{"type":3,"message":"Workspace imported successfully","failed":false,"succeeded":true,"started":false}}
            """)));
        Assert.True(JvmLanguageClient.ImportEnded(Json("""{"method":"intellij/importLog","params":{"message":"Import failed","failed":true}}""")));
        Assert.False(JvmLanguageClient.ImportEnded(Json("""
            {"jsonrpc":"2.0","method":"intellij/importLog","params":{"type":3,"message":"Importing with Maven: C:\\p","tool":"Maven","failed":false,"succeeded":false,"started":true}}
            """)));
        Assert.False(JvmLanguageClient.ImportEnded(Json("""{"method":"window/logMessage","params":{"succeeded":true}}""")));
    }

    [Fact]
    public void EachHintKeepsTheServersOwnToResolve()
    {
        const string answer = """[{"position":{"line":0,"character":3},"label":"kept:","data":{"id":7}}]""";
        HintList hints = JvmInlayHints.Of(Json(answer), TextSnapshot.From("one\ntwo\n"));
        JsonElement sent = Assert.IsType<JsonElement>(Assert.Single(hints).Data);
        Assert.Equal(7, sent.GetProperty("data").GetProperty("id").GetInt32());
    }

    [Fact]
    public void AResolvedHintsTooltipsAreItsCard()
    {
        // Markup as it is, plain text escaped to read as written, the hint's own before its parts'.
        const string resolved = """
            {"position":{"line":0,"character":3},"tooltip":"a * b [c]",
             "label":[{"value":"left","tooltip":{"kind":"markdown","value":"The **left** side"}},{"value":" ="}]}
            """;
        string card = JvmInlayHints.Tooltip(Json(resolved));
        Assert.Equal("a \\* b \\[c\\]\n\nThe **left** side", card);
        Assert.Equal(["a * b [c]", "The left side"], Words(card));
        Assert.Equal(string.Empty, JvmInlayHints.Tooltip(Json("""{"label":"left:"}""")));
    }

    [Fact]
    public void AParameterNamesCardIsTheCallsSignature()
    {
        // As JDT LS 1.57 answered at the argument 2 of add(1, 2), its signature help on.
        const string java = """
            {"signatures":[{"label":"add(int left, int right) : int","parameters":[{"label":"int left"},{"label":"int right"}]}],"activeSignature":0,"activeParameter":1}
            """;
        Assert.Equal("```java\nadd(int left, int right) : int\n```", JvmInlayHints.Signature(Json(java), "java"));

        // The parameter's own words follow it; the signature's active parameter wins over the answer's.
        const string documented = """
            {"signatures":[{"label":"add(left: Int, right: Int): Int","activeParameter":0,
              "parameters":[{"label":[4,13],"documentation":"The first, <one>"},{"label":[15,25]}]}],"activeParameter":1}
            """;
        string card = JvmInlayHints.Signature(Json(documented), "kotlin");
        Assert.Equal(["add(left: Int, right: Int): Int", "The first, <one>"], Words(card));
        Assert.Equal(string.Empty, JvmInlayHints.Signature(Json("""{"signatures":[]}"""), "java"));
        Assert.Equal(string.Empty, JvmInlayHints.Signature(Json("null"), "java"));
    }

    [Fact]
    public void AResolvedKotlinParameterSaysWhereItIsDeclared()
    {
        // As the Kotlin server 271 resolved the hint at add(1's argument, its data left out.
        const string resolved = """
            {"position":{"line":5,"character":20},"kind":2,"paddingRight":true,
             "label":[{"value":"left","location":{"uri":"file:///c%3A/p/Probe.kt","range":{"start":{"line":2,"character":8},"end":{"line":2,"character":12}}}},{"value":" ="}]}
            """;
        (string uri, JsonElement position) = Assert.NotNull(JvmInlayHints.Declared(Json(resolved)));
        Assert.Equal("file:///c%3A/p/Probe.kt", uri);
        Assert.Equal(2, position.GetProperty("line").GetInt32());
        Assert.Equal(8, position.GetProperty("character").GetInt32());
        Assert.Null(JvmInlayHints.Declared(Json("""{"label":[{"value":"left"}]}""")));
        Assert.Null(JvmInlayHints.Declared(Json("""{"label":"left:"}""")));
    }

    [Fact]
    public void ATypesCardIsTheHoverOfTheNameItTypes()
    {
        // As JDT LS 1.57 answered at var max's name: its code fenced, its source's link read as its words.
        const string java = """
            {"contents":[{"language":"java","value":"int max - example.Probe.run()"},"Source: *[service](file:///C:/p/Probe.java#11)*"]}
            """;
        string card = JvmInlayHints.Hover(Json(java));
        Assert.Equal("```java\nint max - example.Probe.run()\n```\n\nSource: *[service](file:///C:/p/Probe.java#11)*", card);
        Assert.Equal(["int max - example.Probe.run()", "Source: service"], Words(card));

        // As the Kotlin server 271 answered: Markdown as it is.
        const string kotlin = """{"contents":{"kind":"markdown","value":"````kotlin\nleft: Int\n````\n"}}""";
        Assert.Equal(["left: Int"], Words(JvmInlayHints.Hover(Json(kotlin))));
        Assert.Equal(string.Empty, JvmInlayHints.Hover(Json("""{"contents":""}""")));
        Assert.Equal(string.Empty, JvmInlayHints.Hover(Json("null")));
    }

    [AvaloniaFact]
    public void TheCardShowsCodeInTheEditorsFontAndGoesWhenClosed()
    {
        Avalonia.Controls.Window window = new() { Width = 800, Height = 600 };
        CodeEditor editor = new() { FontSize = 15 };
        window.Content = editor;
        window.Show();
        try
        {
            HintCard card = new(editor);
            card.Show("```java\nadd(int left, int right) : int\n```\n\nThe *first*", new Avalonia.Point(20, 40), 20);
            Assert.True(card.IsOpen);
            Assert.Equal("add(int left, int right) : int\nThe first", card.Text);
            card.Close();
            Assert.False(card.IsOpen);
            // Markdown without words shows no card.
            card.Show("```\n\n```", new Avalonia.Point(20, 40), 20);
            Assert.False(card.IsOpen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory(SkipUnless = nameof(KotlinClassBridgeTests.RunLanguageAcceptance), SkipType = typeof(KotlinClassBridgeTests),
        Skip = "Set WAYFARER_LANGUAGE_ACCEPTANCE=1 and install the language servers to see their hints' cards.")]
    [InlineData("java")]
    [InlineData("kotlin")]
    public async Task AHoveredHintsCardSaysWhatItsServerSaysOfIt(string language)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string root = WorkspaceImportTests.Copy("maven");
        (string file, string source, string parameter, string type, string call, string typed) = language == "java"
            ? (Path.Combine(root, "service/src/main/java/example/Probe.java"), JavaProbe, "left:", ": String", "add(int left, int right) : int", "String s")
            : (Path.Combine(root, "library/src/main/kotlin/example/Probe.kt"), KotlinProbe, "left =", ": Int", "add(left: Int, right: Int): Int", "Int");
        await File.WriteAllTextAsync(file, source, cancel);
        WorkspaceTrust.Trust(root);
        bool starts = EditorWindow.StartsLanguageServersOnOpen;
        EditorWindow.StartsLanguageServersOnOpen = true;
        EditorWindow window = EditorApplication.CreateWindow(null);
        window.Width = 1200;
        window.Height = 700;
        window.Show();
        try
        {
            await window.OpenWorkspaceAsync(root, cancel);
            window.OpenSource(file);
            CodeEditor editor = window.Editor;
            // Both kinds come with nothing typed: the Kotlin server's first answer, given while it still analyses, lacks the
            // types, which are asked for again as its import ends.
            await Until(() => Hint(parameter) is not null && Hint(type) is not null, () => "The hints never came: " + string.Join(", ", editor.Decorations.InlayHints.Select(h => h.Label)));

            // A parameter's name, the pointer resting on its chip (just before its argument): the call's signature.
            Avalonia.Point argument = editor.PointOf(Hint(parameter)!.Value.Offset);
            Avalonia.Point chip = editor.TranslatePoint(new Avalonia.Point(argument.X - 10, argument.Y + editor.LineHeight / 2), window)!.Value;
            window.MouseMove(chip, Avalonia.Input.RawInputModifiers.None);
            await Until(() => window.HintCard.IsOpen, () => $"No card for {parameter}");
            Assert.Contains(call, window.HintCard.Text, StringComparison.Ordinal);

            // A type: the hover of the name it types.
            await window.ShowHintCardAsync(Hint(type)!.Value, editor.PointOf(Hint(type)!.Value.Offset));
            Assert.True(window.HintCard.IsOpen, $"No card for {type}");
            Assert.Contains(typed, window.HintCard.Text, StringComparison.Ordinal);

            using (var frame = window.CaptureRenderedFrame())
            {
                string folder = Environment.GetEnvironmentVariable("WAYFARER_TEST_OUTPUT") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../test-results"));
                Directory.CreateDirectory(folder);
                frame?.Save(Path.Combine(folder, $"hint-card-{language}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            // An edit takes the card away.
            editor.GoTo(new TextPosition(0, 0));
            editor.Focus();
            window.KeyTextInput(" ");
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.HintCard.IsOpen);
        }
        finally
        {
            window.Close();
            EditorWindow.StartsLanguageServersOnOpen = starts;
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
        }

        InlayHint? Hint(string label) => window.Editor.Decorations.InlayHints.FirstOrDefault(h => h.Label == label) is { Label: not null } found ? found : null;
    }

    private static async Task Until(Func<bool> done, Func<string> failure)
    {
        for (Stopwatch clock = Stopwatch.StartNew(); !done(); Dispatcher.UIThread.RunJobs())
        {
            if (clock.Elapsed > TimeSpan.FromMinutes(3))
            {
                Assert.Fail(failure());
            }
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A card's Markdown as the card shows it, its lines with words.</summary>
    private static string[] Words(string markdown) =>
        [.. WayfarerWanderer.App.Shell.HoverMarkdown.ByKind(markdown, plain: false, words: true)
            .SelectMany(block => block.Lines).Select(line => line.Trim()).Where(line => line.Length > 0)];

    [Theory(SkipUnless = nameof(KotlinClassBridgeTests.RunLanguageAcceptance), SkipType = typeof(KotlinClassBridgeTests),
        Skip = "Set WAYFARER_LANGUAGE_ACCEPTANCE=1 and install the language servers to ask them for hints.")]
    [InlineData("java")]
    [InlineData("kotlin")]
    public async Task EachServerGivesParameterAndTypeNameHints(string language)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string root = WorkspaceImportTests.Copy("maven");
        WorkspaceTrust.Trust(root);
        using WorkspaceImportSession session = new(new ProjectHostImporter());
        JvmLanguageClient? server = null;
        try
        {
            WorkspaceSnapshot snapshot = (await session.RefreshAsync(root, cancel))!;
            (string file, string source) = language == "java"
                ? (Path.Combine(root, "service/src/main/java/example/Probe.java"), JavaProbe)
                : (Path.Combine(root, "library/src/main/kotlin/example/Probe.kt"), KotlinProbe);
            await File.WriteAllTextAsync(file, source, cancel);
            server = await JvmLanguageClient.StartAsync(root, language, cancel, snapshot);
            Assert.True(server.HasInlayHints);
            if (language == "java")
            {
                await server.WaitUntilReadyAsync(cancel);
            }
            string uri = new Uri(file).AbsoluteUri;
            await server.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = language, version = 1, text = source } }, cancel);
            TextSnapshot text = TextSnapshot.From(source);
            TextPosition end = text.GetPosition(text.Length);
            InlayHint[] expected = language == "java"
                ? [new(At(text, "add(1") + 4, "left:", PadRight: true), new(At(text, "s ->") + 1, ": String", Kind: InlayHintKind.Type)]
                : [new(At(text, "add(1") + 4, "left =", PadRight: true), new(At(text, " = left"), ": Int", Kind: InlayHintKind.Type)];
            HintList hints = HintList.Empty;
            // The servers answer with less while they still index.
            for (Stopwatch clock = Stopwatch.StartNew(); clock.Elapsed < TimeSpan.FromMinutes(2); await Task.Delay(1000, cancel))
            {
                JsonElement answer = await server.RequestAsync("textDocument/inlayHint", new
                {
                    textDocument = new { uri },
                    range = new { start = new { line = 0, character = 0 }, end = new { line = end.Line, character = end.Column } },
                }, cancel);
                hints = JvmInlayHints.Of(answer, text);
                if (expected.All(Drawn(hints).Contains))
                {
                    return;
                }
            }
            Assert.Fail($"The {language} server's hints lack some expected: {string.Join(", ", hints)}");
        }
        finally
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
        }
    }

    [AvaloniaFact(SkipUnless = nameof(KotlinClassBridgeTests.RunLanguageAcceptance), SkipType = typeof(KotlinClassBridgeTests),
        Skip = "Set WAYFARER_LANGUAGE_ACCEPTANCE=1 and install JDT LS to see the Java server's hints in the editor.")]
    public async Task TheJavaFileShownHasItsHintsAndAnEditAsksAgain()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string root = WorkspaceImportTests.Copy("maven");
        string file = Path.Combine(root, "service/src/main/java/example/Probe.java");
        await File.WriteAllTextAsync(file, JavaProbe, cancel);
        WorkspaceTrust.Trust(root);
        bool starts = EditorWindow.StartsLanguageServersOnOpen;
        EditorWindow.StartsLanguageServersOnOpen = true;
        EditorWindow window = EditorApplication.CreateWindow(null);
        window.Width = 1200;
        window.Height = 700;
        window.Show();
        try
        {
            await window.OpenWorkspaceAsync(root, cancel);
            window.OpenSource(file);
            CodeEditor editor = window.Editor;
            await Until(() => Labels(editor).Contains("left:") && Labels(editor).Contains(": String"), () => "The hints never came: " + string.Join(", ", Labels(editor)));

            // A call typed into the file has its own once editing pauses.
            int line = JavaProbe.Split('\n').ToList().FindIndex(l => l.Contains("var max", StringComparison.Ordinal));
            editor.GoTo(new TextPosition(line, editor.Document.Snapshot.GetLineText(line).Length));
            editor.Focus();
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            window.KeyTextInput("add(7, 8);");
            await Until(() => editor.Decorations.InlayHints.Count(h => h.Label == "left:") == 2, () => "The typed call never had its hints: " + string.Join(", ", Labels(editor)));

            using (var frame = window.CaptureRenderedFrame())
            {
                string folder = Environment.GetEnvironmentVariable("WAYFARER_TEST_OUTPUT") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../test-results"));
                Directory.CreateDirectory(folder);
                frame?.Save(Path.Combine(folder, "inlay-hints.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            // Settings' Editor page turns each kind off in the editor.
            window.SettingsKept.Update(s => s with { ParameterHints = false });
            Dispatcher.UIThread.RunJobs();
            Assert.False(editor.ShowParameterHints);
            Assert.True(editor.ShowTypeHints);
        }
        finally
        {
            window.Close();
            EditorWindow.StartsLanguageServersOnOpen = starts;
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
        }

        static string[] Labels(CodeEditor editor) => [.. editor.Decorations.InlayHints.Select(h => h.Label)];
    }

    /// <summary>An answer's hints as the editor draws them, without the server's own each keeps.</summary>
    private static InlayHint[] Hints(string answer, TextSnapshot text) => Drawn(JvmInlayHints.Of(Json(answer), text));

    private static InlayHint[] Drawn(IEnumerable<InlayHint> hints) => [.. hints.Select(h => h with { Data = null })];

    private static JsonElement Json(string json)
    {
        using JsonDocument parsed = JsonDocument.Parse(json);
        return parsed.RootElement.Clone();
    }

    private static int At(TextSnapshot text, string what)
    {
        int at = text.ToString().IndexOf(what, StringComparison.Ordinal);
        Assert.True(at >= 0, what);
        return at;
    }
}
