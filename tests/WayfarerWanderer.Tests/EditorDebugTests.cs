using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.App;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

[Collection(JvmTrustCollection.Name)]
public sealed class EditorDebugTests
{
    public static bool RunGradle => Environment.GetEnvironmentVariable("WAYFARER_DEBUG_ACCEPTANCE") == "1";

    [AvaloniaFact]
    public void SwitchingSourceRestoresItsBufferAndBreakpoints()
    {
        string root = Directory.CreateTempSubdirectory("wayfarer-source-").FullName;
        var window = EditorApplication.CreateWindow(null);
        try
        {
            string first = Path.Combine(root, "A.kt"), second = Path.Combine(root, "B.java");
            File.WriteAllText(first, "fun a() {}");
            File.WriteAllText(second, "class B {}");
            window.OpenSource(first);
            Assert.True(window.Actions.TryExecute(EditorActions.ToggleBreakpoint));
            TextDocument document = window.Editor.Document;
            document.Edit([new TextEdit(0, 0, "// unsaved\n")], SelectionSet.Single(0), SelectionSet.Single(11));
            window.OpenSource(second);
            Assert.False(window.Breakpoints.IsMarked(0));
            window.OpenSource(first);
            Assert.Same(document, window.Editor.Document);
            Assert.True(window.Editor.Document.IsModified);
            Assert.True(window.Breakpoints.IsMarked(0));
            Assert.True(window.Actions.TryExecute(EditorActions.SaveSource));
            Assert.StartsWith("// unsaved", File.ReadAllText(first));
        }
        finally
        {
            window.Close();
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task DebugAndRunAnIntelliJModuleInTheWindowWithConsoleInput()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await using JvmConsoleDebugTests.Module module = await JvmConsoleDebugTests.Module.CreateAsync(cancel);
        var window = EditorApplication.CreateWindow(null);
        try
        {
            window.Show();
            await window.OpenWorkspaceAsync(module.Snapshot.Root, cancel);
            ProjectSession session = window.Session!;
            await session.Launches.SaveAsync(module.Launch("ask") with { Name = "App" }, cancel);
            window.OpenSource(module.Source);
            window.Editor.GoTo(new TextPosition(11, 0));
            Assert.True(window.Actions.TryExecute(EditorActions.ToggleBreakpoint));

            Assert.True(window.Actions.TryExecute(EditorActions.Debug));
            await Until(() => session.IsPaused, "the breakpoint");
            Assert.Equal("main:12, App (com.example.console)", window.DebugTool.Frames[0]);
            Assert.Contains("answer = 42", window.DebugTool.Variables);
            Assert.Equal(11, window.Breakpoints.ExecutionLine);
            Assert.Contains(window.DebugTool.Threads, thread => thread.Name == "main");
            Assert.True(window.Actions[EditorActions.Pause].Available() is false);

            VariableNode primes = Assert.Single(window.DebugTool.VariableNodes, node => node.Name == "primes");
            primes.IsExpanded = true;
            await Until(() => primes.Children.Select(child => child.ToString()).SequenceEqual(["[0] = 2", "[1] = 3", "[2] = 5"]), "the array's elements");
            await window.DebugTool.EvaluateAsync("primes[1]");
            Assert.Equal("primes[1] = 3", window.DebugTool.Variables[0]);
            window.DebugTool.AddWatch("app.greeting");
            await Until(() => window.DebugTool.Variables.Contains("app.greeting = \"Hello\""), "the watch");

            // Inline values after the lines of main that name its variables.
            await Until(() => window.Editor.LineAnnotations.Any(a => a.Line == 8 && a.Text == "answer: 42"), "the inline values");
            Assert.Contains(window.Editor.LineAnnotations, a => a.Line == 11 && a.Text == "answer: 42");
            Assert.Contains(window.Editor.LineAnnotations, a => a.Line == 7 && a.Text.StartsWith("args: ", StringComparison.Ordinal));

            // Set Value on a local, then on an array's element; the inline values follow.
            VariableNode answer = Assert.Single(window.DebugTool.VariableNodes, node => node.Name == "answer");
            Assert.True(window.DebugTool.VariablesView.StartEditing(answer));
            Assert.Equal("42", answer.EditText);
            answer.IsEditing = false;
            await window.DebugTool.SetValueAsync(answer, "answer + 1");
            Assert.Equal("43", answer.Value);
            await Until(() => window.Editor.LineAnnotations.Any(a => a.Line == 8 && a.Text == "answer: 43"), "the inline value set");
            VariableNode first = primes.Children[0];
            Assert.Equal("primes[0]", first.Expression);
            await window.DebugTool.SetValueAsync(first, "11");
            Assert.Equal("[0] = 11", first.ToString());

            // A code fragment, then completion and history in the evaluate box.
            await window.DebugTool.EvaluateAsync("int sum = 0;\nfor (int p : primes) sum += p;\nsum");
            Assert.Equal("int sum = 0; for (int p : primes) sum += p; sum = 19", window.DebugTool.Variables[1]);
            // Evaluate Expression from the editor puts the expression at the caret in the box.
            window.Editor.GoTo(new TextPosition(11, window.Editor.Document.Snapshot.GetLineText(11).IndexOf("app.greet", StringComparison.Ordinal) + 1));
            Assert.True(window.Actions.TryExecute(WayfarerWanderer.App.Shell.AppActions.Evaluate));
            TextBox box = window.DebugTool.EvaluateBox;
            Assert.Equal("app", box.Text);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            box.Text = "app.greeti";
            box.CaretIndex = 10;
            box.Focus();
            window.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
            await Until(() => window.DebugTool.EvaluateCompletion.Shown.Any(choice => choice.Label == "greeting"), "completion in the evaluate box");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.Equal("app.greeting", box.Text);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await Until(() => window.DebugTool.Variables.Contains("app.greeting = \"Hello\"") && window.DebugTool.VariableNodes.Count(n => n.Name == "app.greeting") == 2, "the completed expression evaluated");
            box.Text = "";
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Assert.Equal("app.greeting", box.Text);
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Assert.StartsWith("int sum = 0;", box.Text);

            // Quick Evaluate the expression at the caret: a card with its value.
            window.Editor.GoTo(new TextPosition(11, window.Editor.Document.Snapshot.GetLineText(11).IndexOf("answer", StringComparison.Ordinal) + 2));
            Assert.True(window.Actions.TryExecute(WayfarerWanderer.App.Shell.AppActions.QuickEvaluate));
            await Until(() => window.ValueCard.Node?.ToString() == "answer = 43", "the Quick Evaluate card");
            window.ValueCard.Close();
            Render("debug-paused");

            Assert.True(window.Actions.TryExecute(EditorActions.StepOver));
            await Until(() => session.IsPaused, "the step");
            Assert.Equal(12, window.Breakpoints.ExecutionLine);
            await Until(() => window.DebugTool.Variables.Contains("app.greeting = \"Hello\""), "the watch after the step");

            Assert.True(window.Actions.TryExecute(EditorActions.Continue));
            await Until(() => window.DebugTool.Console.Text.Contains("Name?", StringComparison.Ordinal), "the program to ask");
            Assert.Equal(-1, window.Breakpoints.ExecutionLine);
            window.DebugTool.Console.View.Session!.Write("Ada\r");
            await Until(() => !session.IsRunning, "the debugged program to end");
            Assert.Contains("Typed Ada", window.DebugTool.Console.Text);
            Assert.Equal("Process finished with exit code 0", window.DebugTool.Status);

            Assert.True(window.Actions.TryExecute(EditorActions.Run));
            await Until(() => window.RunTool.Console.Text.Contains("Name?", StringComparison.Ordinal), "the run to ask");
            window.RunTool.Console.View.Session!.Write("Bob\r");
            await Until(() => !session.IsRunning, "the run to end");
            Assert.Contains("Typed Bob", window.RunTool.Console.Text);
            Render("run-console");
            Assert.Contains("Process finished with exit code 0", window.RunTool.Console.Text);
            Assert.True(window.Actions[EditorActions.Rerun].Available());
        }
        finally
        {
            if (window.Session != null) await window.Session.CancelAsync();
            window.Close();
        }

        // The window as it shows then, for a look at the Debug and Run windows.
        void Render(string name)
        {
            window.Width = 1400;
            window.Height = 900;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            string folder = Environment.GetEnvironmentVariable("WAYFARER_TEST_OUTPUT") ??
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../test-results"));
            Directory.CreateDirectory(folder);
            frame?.Save(Path.Combine(folder, $"{name}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        async Task Until(Func<bool> condition, string what)
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (!condition() && timeout.Elapsed < TimeSpan.FromMinutes(2))
                await Task.Delay(30, cancel);
            Assert.True(condition(), $"Waiting for {what}: {window.DebugTool.Status}\n{string.Join("\n", window.BuildTool.Output)}\n{window.DebugTool.Console.Text}\n{window.RunTool.Console.Text}");
        }
    }

    /// <summary>A JDK 25 starter's App, a compact source file, debugs in the window: it stops at a breakpoint inside
    /// main, shows its frame, variables and inline values, steps and runs to its end.</summary>
    [AvaloniaFact]
    public async Task DebugACompactSourceFileInTheWindow()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string jdk = NewProjectTests.CompactJdkHome();
        string parent = Directory.CreateTempSubdirectory("wayfarer compact ").FullName;
        ProjectGenerationResult generated = await ProjectGenerator.GenerateAsync(new ProjectGenerationOptions
        {
            ProjectName = "Compact", Destination = Path.Combine(parent, "compact"), GroupId = "com.example",
            ArtifactId = "compact", PackageName = "com.example.compact", JdkHome = jdk,
            Kind = NewProjectKind.Application, Language = NewProjectLanguage.Java, Build = NewProjectBuild.IntelliJ,
            LaunchName = "Run",
        }, cancel);
        string source = Path.Combine(generated.Destination, "src", "App.java");
        File.WriteAllText(source, "void main() {\n    int answer = 42;\n    IO.println(\"Answer \" + answer);\n}\n");
        WorkspaceTrust.Trust(generated.Destination);
        var window = EditorApplication.CreateWindow(null);
        try
        {
            window.Show();
            await window.OpenWorkspaceAsync(generated.Destination, cancel);
            ProjectSession session = window.Session!;
            window.OpenSource(source);
            window.Editor.GoTo(new TextPosition(2, 0));
            Assert.True(window.Actions.TryExecute(EditorActions.ToggleBreakpoint));

            Assert.True(window.Actions.TryExecute(EditorActions.Debug));
            await Until(() => session.IsPaused, "the breakpoint");
            Assert.Equal("main:3, App", window.DebugTool.Frames[0]);
            Assert.Contains("answer = 42", window.DebugTool.Variables);
            Assert.Equal(2, window.Breakpoints.ExecutionLine);
            await Until(() => window.Editor.LineAnnotations.Any(a => a.Line == 1 && a.Text == "answer: 42"), "the inline values");
            VariableNode self = Assert.Single(window.DebugTool.VariableNodes, node => node.Name == "this");
            self.IsExpanded = true;
            await window.DebugTool.EvaluateAsync("answer + 1");
            Assert.Equal("answer + 1 = 43", window.DebugTool.Variables[0]);

            Assert.True(window.Actions.TryExecute(EditorActions.StepOver));
            await Until(() => session.IsPaused, "the step");
            Assert.Equal(3, window.Breakpoints.ExecutionLine);

            Assert.True(window.Actions.TryExecute(EditorActions.Continue));
            await Until(() => !session.IsRunning, "the debugged program to end");
            Assert.Contains("Answer 42", window.DebugTool.Console.Text);
            Assert.Equal("Process finished with exit code 0", window.DebugTool.Status);
        }
        finally
        {
            if (window.Session != null) await window.Session.CancelAsync();
            window.Close();
            WorkspaceTrust.TakeBack(generated.Destination);
            try { Directory.Delete(parent, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        async Task Until(Func<bool> condition, string what)
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (!condition() && timeout.Elapsed < TimeSpan.FromMinutes(2))
                await Task.Delay(30, cancel);
            Assert.True(condition(), $"Waiting for {what}: {window.DebugTool.Status}\n{string.Join("\n", window.BuildTool.Output)}\n{window.DebugTool.Console.Text}");
        }
    }

    [AvaloniaFact(SkipUnless = nameof(RunGradle), Skip = "Set WAYFARER_DEBUG_ACCEPTANCE=1 to run the Gradle wrapper and debug JVM.")]
    public async Task DebugGradleMainAndStepFromKotlinIntoJava()
    {
        string fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../tests/fixtures/jvm/gradle"));
        string root = Directory.CreateTempSubdirectory("wayfarer-debug-ui-").FullName;
        var window = EditorApplication.CreateWindow(null);
        try
        {
            foreach (string file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(fixture, file);
                if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is ".gradle" or "build")) continue;
                string target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(file));
            }
            WorkspaceTrust.Trust(root);
            window.Show();
            await window.OpenWorkspaceAsync(root, TestContext.Current.CancellationToken);
            string main = Path.Combine(root, "app", "src", "main", "kotlin", "example", "Main.kt");
            window.OpenSource(main);
            window.Editor.GoTo(new TextPosition(3, 0));
            window.Editor.Focus();
            window.KeyPress(Key.F9, RawInputModifiers.None, PhysicalKey.F9, null);
            Assert.True(window.Breakpoints.IsMarked(3));
            Assert.True(window.Actions.TryExecute(EditorActions.Debug));
            await Until(() => window.Session!.IsPaused);
            Assert.Contains(window.DebugTool.Frames, line => line.StartsWith("main:4, MainKt", StringComparison.Ordinal));
            Assert.True(window.Actions.TryExecute(EditorActions.StepInto));
            await Until(() => window.Session!.IsPaused);
            Assert.Equal("Greeting.java", Path.GetFileName(window.SourcePath));
            Assert.Equal(4, window.Editor.CaretPosition.Line);
            Assert.Contains(window.DebugTool.Variables, line => line.Contains("name") && line.Contains("Wayfarer"));
            Assert.True(window.Actions.TryExecute(EditorActions.StepOut));
            await Until(() => window.Session!.IsPaused);
            Assert.Equal(main, window.SourcePath);
            Assert.True(window.Actions.TryExecute(EditorActions.Continue));
            await Until(() => !window.Session!.IsRunning);
            Assert.Contains("Hello, Wayfarer", window.DebugTool.Console.Text);
            window.OpenSource(main);
            Assert.True(window.Actions.TryExecute(EditorActions.Debug));
            await Until(() => window.Session!.IsPaused);
            Assert.True(window.Actions.TryExecute(EditorActions.Stop));
            await Until(() => !window.Session!.IsRunning);
            Assert.False(window.Actions[EditorActions.StepInto].Available());
        }
        finally
        {
            if (window.Session != null) await window.Session.CancelAsync();
            window.Close();
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root));
            Directory.Delete(root, true);
        }

        async Task Until(Func<bool> condition)
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (!condition() && window.Session!.IsRunning && timeout.Elapsed < TimeSpan.FromMinutes(4))
                await Task.Delay(30, TestContext.Current.CancellationToken);
            Assert.True(condition(), window.DebugTool.Status + Environment.NewLine + string.Join(Environment.NewLine, window.BuildTool.Output));
        }
    }
}
