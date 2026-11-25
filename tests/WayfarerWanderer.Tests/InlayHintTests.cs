using System.Diagnostics;
using System.Text.Json;
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
        HintList hints = Hints(answer, text);

        Assert.Equal(4, hints.Count);
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
        HintList hints = Hints(answer, text);

        Assert.Equal(3, hints.Count);
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
        HintList hints = Hints(answer, TextSnapshot.From("one\ntwo\n"));
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
    }

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
                if (expected.All(hints.Contains))
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

        static async Task Until(Func<bool> done, Func<string> failure)
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
    }

    private static HintList Hints(string answer, TextSnapshot text)
    {
        using JsonDocument parsed = JsonDocument.Parse(answer);
        return JvmInlayHints.Of(parsed.RootElement, text);
    }

    private static int At(TextSnapshot text, string what)
    {
        int at = text.ToString().IndexOf(what, StringComparison.Ordinal);
        Assert.True(at >= 0, what);
        return at;
    }
}
