using System.Diagnostics;
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
