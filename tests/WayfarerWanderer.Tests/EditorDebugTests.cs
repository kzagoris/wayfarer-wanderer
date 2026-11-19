using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.App;

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
            Assert.Contains(window.DebugTool.Frames, line => line.Contains("Main.kt:4", StringComparison.Ordinal));
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
            Assert.Contains(window.BuildTool.Output, line => line.Contains("Hello, Wayfarer"));
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
