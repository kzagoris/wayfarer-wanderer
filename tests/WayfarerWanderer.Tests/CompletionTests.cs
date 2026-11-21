using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.App;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

public sealed class CompletionTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void ParsesAListLeavingOutSnippetsAndRepeatedDetail()
    {
        var choices = CompletionModel.Parse(Json("""
            { "isIncomplete": true, "items": [
              { "label": "System - java.lang", "kind": 7, "detail": "java.lang.System", "sortText": "2", "filterText": "System" },
              { "label": "sysout", "kind": 15, "insertTextFormat": 2, "insertText": "System.out.println(${0});" },
              { "label": "size", "kind": 2, "labelDetails": { "detail": "()", "description": "Int" }, "detail": "size" }
            ] }
            """), out bool incomplete);

        Assert.True(incomplete);
        Assert.Equal(["System - java.lang", "size()"], choices.Select(c => c.Label));
        Assert.Equal(["java.lang.System", "Int"], choices.Select(c => c.Detail));
        Assert.Equal(["C", "m"], choices.Select(c => c.Badge));
        // Without a filter text the label's name narrows it, not what JDT LS appends.
        Assert.Equal("size", choices[1].Filter);
    }

    [Fact]
    public void SplitsALabelIntoNameQualifierAndType()
    {
        var choices = CompletionModel.Parse(Json("""
            [ { "label": "System - java.lang", "kind": 7, "detail": "java.lang.System" },
              { "label": "println(String x) : void", "kind": 2, "detail": "PrintStream.println(String x) : void" },
              { "label": "SynchronousQueue<E> - java.util.concurrent", "kind": 7, "detail": "java.util.concurrent.SynchronousQueue" },
              { "label": "size", "kind": 2, "labelDetails": { "detail": "()", "description": "Int" } },
              { "label": "old", "kind": 5, "tags": [1] } ]
            """), out _);

        Assert.Equal(("System", "java.lang", ""), choices[0].Parts);
        Assert.Equal(("println(String x)", "", "void"), choices[1].Parts);
        Assert.Equal(("SynchronousQueue<E>", "java.util.concurrent", ""), choices[2].Parts);
        Assert.Equal(("size()", "", "Int"), choices[3].Parts);
        Assert.Equal([false, false, false, false, true], choices.Select(c => c.Deprecated));
    }

    [Fact]
    public void MarksTheLettersTypedWhereTheyMatched()
    {
        Assert.Equal([0, 1, 2], CompletionModel.MatchedAt("SystemTray", "sys"));
        Assert.Equal([0, 6], CompletionModel.MatchedAt("SystemTray", "ST"));
        Assert.Equal([2, 3], CompletionModel.MatchedAt("toString", "St"));
        Assert.Empty(CompletionModel.MatchedAt("toString", "xyz"));
        Assert.Empty(CompletionModel.MatchedAt("toString", ""));
    }

    [Fact]
    public void NarrowsByPrefixThenCaseThenHumpsThenAnywhere()
    {
        var choices = CompletionModel.Parse(Json("""
            [ { "label": "getSize", "sortText": "1" }, { "label": "toString", "sortText": "2" }, { "label": "GetType", "sortText": "3" },
              { "label": "getsome", "sortText": "4" }, { "label": "forget", "sortText": "5" }, { "label": "hashCode", "sortText": "6" } ]
            """), out _);

        Assert.Equal(["getSize", "getsome", "GetType", "forget"], CompletionModel.Narrow(choices, "get").Select(c => c.Label));
        Assert.Equal(["getSize"], CompletionModel.Narrow(choices, "gS").Select(c => c.Label));
        Assert.Equal("toString", CompletionModel.Narrow(choices, "tS")[0].Label);
        Assert.Empty(CompletionModel.Narrow(choices, "xyz"));
        Assert.Equal(6, CompletionModel.Narrow(choices, "").Count);
    }

    [Fact]
    public void AppliesTheServersEditFromItsStartToTheCaretNowAndKeepsTheImport()
    {
        // Asked at "Li|"; "st" typed since. The import goes at the top and moves the caret down a line.
        TextSnapshot text = TextSnapshot.From("package a;\nclass A { List }\n");
        int caret = text.ToString().IndexOf(" }", StringComparison.Ordinal);
        int wordStart = CompletionModel.WordStart(text, caret);
        JsonElement item = Json("""
            { "label": "List - java.util", "textEdit": { "range": { "start": { "line": 1, "character": 10 }, "end": { "line": 1, "character": 12 } }, "newText": "List" },
              "additionalTextEdits": [ { "range": { "start": { "line": 0, "character": 10 }, "end": { "line": 0, "character": 10 } }, "newText": "\n\nimport java.util.List;" } ] }
            """);

        var (edits, after) = CompletionModel.Edits(text, item, wordStart, caret);
        TextSnapshot applied = text.Apply([.. edits.OrderBy(e => e.Start)]);

        Assert.Equal("package a;\n\nimport java.util.List;\nclass A { List }\n", applied.ToString());
        Assert.Equal(applied.ToString().IndexOf(" }", StringComparison.Ordinal), after);
    }

    [Fact]
    public void WithoutAnEditTheTypedWordIsReplacedByTheName()
    {
        TextSnapshot text = TextSnapshot.From("x.pri");
        var (edits, after) = CompletionModel.Edits(text, Json("""{ "label": "println(String x) : void" }"""), 2, 5);

        Assert.Equal("x.println", text.Apply([.. edits]).ToString());
        Assert.Equal(9, after);
    }

    [AvaloniaFact(SkipUnless = nameof(KotlinClassBridgeTests.RunLanguageAcceptance), SkipType = typeof(KotlinClassBridgeTests),
        Skip = "Set WAYFARER_LANGUAGE_ACCEPTANCE=1 and install JDT LS to complete Java as it is typed.")]
    public async Task TypingInANewIntelliJStarterSuggestsAndInsertsWithImports()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string parent = Directory.CreateTempSubdirectory("wayfarer completion ").FullName;
        ProjectGenerationResult generated = await ProjectGenerator.GenerateAsync(new ProjectGenerationOptions
        {
            ProjectName = "Typed",
            Destination = Path.Combine(parent, "typed"),
            GroupId = "com.example",
            ArtifactId = "typed",
            PackageName = "com.example.typed",
            JdkHome = NewProjectTests.JdkHome(),
            Kind = NewProjectKind.Application,
            Language = NewProjectLanguage.Java,
            Build = NewProjectBuild.IntelliJ,
            LaunchName = "Run",
        }, cancel);
        string root = generated.Destination;
        WorkspaceTrust.Trust(root);
        var window = EditorApplication.CreateWindow(new TextDocument());
        window.Width = 1600;
        window.Height = 900;
        window.Show();
        try
        {
            await window.OpenWorkspaceAsync(root, cancel);
            string source = Path.Combine(root, "src", "com", "example", "typed", "App.java");
            window.OpenSource(source);
            TextDocument document = window.Editor.Document;
            // Start on an empty line inside main, after the println.
            int line = Enumerable.Range(0, document.Snapshot.LineCount).First(l => document.Snapshot.GetLineText(l).Contains("println", StringComparison.Ordinal));
            window.Editor.GoTo(new TextPosition(line, document.Snapshot.GetLineText(line).Length));
            window.Editor.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

            // A word typed opens the list by itself, and Enter takes the first match.
            window.KeyTextInput("Sys");
            await UntilAsync(() => window.Completion.Shown.Any(c => c.Label.StartsWith("System ", StringComparison.Ordinal)), "System was never suggested.");
            Assert.StartsWith("System", window.Completion.Selected!.Label, StringComparison.Ordinal);
            // The list opens under the word, its names starting where the word starts.
            Avalonia.Point word = window.Editor.PointOf(window.Editor.Selections.Primary.Caret - 3);
            Avalonia.Rect place = window.Completion.Place!.Value;
            Assert.Equal(word.X, place.X + window.Completion.TextInset, 1);
            Assert.Equal(word.Y + window.Editor.LineHeight, place.Y, 1);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UntilAsync(() => !window.Completion.IsOpen && LineAtCaret(window).TrimStart() == "System", "System was never inserted.");

            // A dot asks at once; typing narrows what was sent without asking again.
            window.KeyTextInput(".");
            await UntilAsync(() => window.Completion.Shown.Any(c => c.Label.StartsWith("out", StringComparison.Ordinal)), "System's members were never suggested.");
            window.KeyTextInput("ou");
            await UntilAsync(() => window.Completion.Shown.Count > 0 && window.Completion.Shown.All(c => c.Filter.Contains("ou", StringComparison.OrdinalIgnoreCase)), "The list never narrowed to 'ou'.");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(window.Completion.IsOpen);
            Assert.Equal("System.ou", LineAtCaret(window).TrimStart());

            // A type from another package brings its import (on a line of its own, the statement above finished first).
            window.KeyTextInput("t.println();");
            Assert.False(window.Completion.IsOpen);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyTextInput("ArrayLis");
            await UntilAsync(() => window.Completion.Selected?.Label.StartsWith("ArrayList - java.util", StringComparison.Ordinal) == true, () => "ArrayList was never suggested first: " + string.Join(" | ", window.Completion.Shown.Take(8).Select(c => c.Label)) + " / line: " + LineAtCaret(window));
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            await UntilAsync(() => document.Snapshot.ToString().Contains("import java.util.ArrayList;", StringComparison.Ordinal), "ArrayList's import was never added.");
            Assert.Equal("ArrayList", LineAtCaret(window).TrimStart());
        }
        finally
        {
            window.Close();
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root) ?? root);
            try { Directory.Delete(parent, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string LineAtCaret(EditorWindow window) => window.Editor.Document.Snapshot.GetLineText(window.Editor.CaretPosition.Line);

    private static Task UntilAsync(Func<bool> done, string failure) => UntilAsync(done, () => failure);

    private static async Task UntilAsync(Func<bool> done, Func<string> failure)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed > TimeSpan.FromMinutes(3)) Assert.Fail(failure());
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
