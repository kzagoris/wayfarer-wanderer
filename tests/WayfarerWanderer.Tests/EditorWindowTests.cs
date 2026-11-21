using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using SkiaSharp;
using WayfarerWanderer.App;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

public class EditorWindowTests
{
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void RendersDocumentAndBreakpoint(string theme)
    {
        var window = EditorApplication.CreateWindow(new TextDocument("class Main {\n    int count; }"));
        window.Width = 460;
        window.Height = 180;
        window.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        window.Show();
        // The pixels checked are the editor's: a window this narrow gives it room only with Project closed.
        window.ToolDock.Hide(WayfarerWanderer.App.Shell.ToolWindowIds.Explorer);
        window.UpdateLayout();
        try
        {
            var editor = window.Editor;
            Assert.Equal(window.RequestedThemeVariant, editor.ActualThemeVariant);
            CheckResources(editor, theme);
            editor.CaretBlink = CaretBlink.Steady;
            editor.CaretWidth = 3;
            Assert.True(editor.Focus());
            Point InWindow(Point p) => editor.TranslatePoint(p, window)!.Value;
            var marker = InWindow(editor.GutterColumnPoint(window.Breakpoints, 1));
            window.MouseDown(marker, MouseButton.Left);
            window.MouseUp(marker, MouseButton.Left);
            Assert.Equal(new TextPosition(1, 0), editor.CaretPosition);
            Assert.True(window.Breakpoints.IsMarked(1));

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            string folder = Environment.GetEnvironmentVariable("WAYFARER_TEST_OUTPUT") ??
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../test-results"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"editor-{theme.ToLowerInvariant()}.png");
            frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            using var pixels = SKBitmap.Decode(path);
            var first = InWindow(editor.PointOf(0));
            var caret = InWindow(editor.PointOf(editor.Document.Snapshot.GetLineStart(1)));
            var gutter = InWindow(editor.GutterColumnPoint(window.Breakpoints, 0));
            CheckPixels(pixels, editor, "Background", first.X + 180, first.Y, 12, 10);
            CheckPixels(pixels, editor, "Text", first.X, first.Y, 90, editor.LineHeight);
            CheckPixels(pixels, editor, "Caret", caret.X - 2, caret.Y, 6, editor.LineHeight);
            CheckPixels(pixels, editor, "Gutter", gutter.X - 3, gutter.Y - 3, 6, 6);
            CheckPixels(pixels, editor, "Breakpoint", marker.X - 3, marker.Y - 3, 6, 6);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MissingTokensFailsResourceCheck()
    {
        var dictionaries = Application.Current!.Resources.MergedDictionaries;
        var window = EditorApplication.CreateWindow(new TextDocument());
        window.RequestedThemeVariant = ThemeVariant.Light;
        window.Show();
        var tokens = dictionaries.Single(dictionary =>
            dictionary.TryGetResource("Passenger.Editor.Background", ThemeVariant.Light, out _));
        int index = dictionaries.IndexOf(tokens);
        dictionaries.RemoveAt(index);
        try
        {
            var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => CheckResources(window.Editor, "Light"));
            Assert.Contains("Passenger.", error.Message);
        }
        finally
        {
            dictionaries.Insert(index, tokens);
            window.Close();
        }
    }

    private static void CheckResources(CodeEditor editor, string theme)
    {
        // Compare with the source at the selected pin, including its optional syntax styles.
        using var stream = typeof(EditorWindowTests).Assembly.GetManifestResourceStream("PassengerTheme")!;
        XName key = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Key";
        var variant = XDocument.Load(stream).Descendants().Single(e => (string?)e.Attribute(key) == theme);
        foreach (var entry in variant.Elements())
        {
            string name = (string)entry.Attribute(key)!;
            if (entry.Name.LocalName == "SolidColorBrush")
            {
                var brush = Resource<ISolidColorBrush>(editor, name);
                Assert.Equal(Color.Parse((string)entry.Attribute("Color")!), brush.Color);
            }
            else if (name.StartsWith("Passenger.SyntaxStyle.", StringComparison.Ordinal))
                Assert.Equal(entry.Value, Resource<string>(editor, name));
        }

        Resource<double>(editor, "Passenger.EditorTextPadding");
        Resource<CornerRadius>(editor, "Passenger.PopupRadius");
        foreach (var name in new[] { "Icon.Error", "Icon.Problems", "Icon.Check" })
            Resource<Geometry>(editor, name);
    }

    private static T Resource<T>(CodeEditor editor, string name)
    {
        Assert.True(editor.TryFindResource(name, editor.ActualThemeVariant, out var value), name);
        return Assert.IsAssignableFrom<T>(value);
    }

    private static void CheckPixels(SKBitmap pixels, CodeEditor editor, string token,
        double left, double top, double width, double height)
    {
        var colour = Resource<ISolidColorBrush>(editor, "Passenger.Editor." + token).Color;
        var expected = new SKColor(colour.R, colour.G, colour.B, colour.A);
        for (int y = (int)top; y < (int)(top + height); y++)
            for (int x = (int)left; x < (int)(left + width); x++)
                if (pixels.GetPixel(x, y) == expected)
                    return;

        Assert.Fail($"Missing {token} ({expected}) in rendered region {left},{top} {width}x{height}.");
    }

    [AvaloniaFact]
    public void MenuShortcutSearchAndGutterToggleTheSameLine()
    {
        var window = EditorApplication.CreateWindow(new TextDocument("first\nsecond"));
        var other = EditorApplication.CreateWindow(new TextDocument("separate\nwindow"));
        other.Show();
        window.Show();
        try
        {
            var editor = window.Editor;
            editor.Focus();
            var at = editor.TranslatePoint(editor.GutterColumnPoint(window.Breakpoints, 1), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Assert.True(window.Breakpoints.IsMarked(1));
            Assert.False(window.Breakpoints.IsMarked(0));
            Assert.Equal(1, editor.CaretPosition.Line);
            window.KeyPress(Key.F9, RawInputModifiers.None, PhysicalKey.F9, null);
            Assert.False(window.Breakpoints.IsMarked(1));
            var action = Assert.Single(window.Actions.Find("breakPOINT"));
            Assert.Equal("app.debug.toggleBreakpoint", action.Id);
            Assert.True(action.TryExecute());
            Assert.True(window.Breakpoints.IsMarked(1));
            Assert.False(other.Breakpoints.IsMarked(1));
            var item = ToggleMenuItem(window);
            Assert.Equal(action.Title, item.Header);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(window.Breakpoints.IsMarked(1));
        }
        finally
        {
            window.Close();
            other.Close();
        }
    }

    [AvaloniaFact]
    public void ToggleRequiresAnActiveDocument()
    {
        var window = EditorApplication.CreateWindow(null);
        window.Show();
        try
        {
            Assert.False(ToggleMenuItem(window).IsEnabled);
            Assert.False(Assert.Single(window.Actions.Find("breakpoint")).TryExecute());
            window.KeyPress(Key.F9, RawInputModifiers.None, PhysicalKey.F9, null);
            Assert.False(window.Breakpoints.IsMarked(0));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void FindActionShortcutSearchesAndRunsTheSelectedAction()
    {
        var window = EditorApplication.CreateWindow(new TextDocument("class Main {}"));
        window.Show();
        try
        {
            var modifier = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.KeyPress(Key.A, modifier | RawInputModifiers.Shift, PhysicalKey.A, null);
            var query = Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), box => box.IsEffectivelyVisible);
            Assert.True(query.IsFocused);
            window.KeyTextInput("breakpoint");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(window.Breakpoints.IsMarked(0));
            Assert.False(query.IsEffectivelyVisible);
            Assert.True(window.Editor.IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task OpenWorkspaceConnectsBuildTestRunAndDebugActionsToOneSession()
    {
        string source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../tests/fixtures/jvm/maven"));
        string root = Directory.CreateTempSubdirectory("wayfarer-editor-workspace-").FullName;
        CopyTree(source, root);
        string trust = WorkspaceTrust.FolderToTrust(root) ?? root;
        WorkspaceTrust.Trust(root);
        var window = EditorApplication.CreateWindow(new TextDocument("class Main {}"));
        window.Show();
        try
        {
            await window.OpenWorkspaceAsync(root, TestContext.Current.CancellationToken);
            Assert.NotNull(window.Session);
            Assert.Equal(root, window.Session.Root);
            // The reactor's own pom is a module beside library, app and service, as Maven evaluates it.
            Assert.Equal(4, window.Session.Snapshot!.Modules.Count);
            Assert.All(new[] { "library", "app", "service" }, name => Assert.Contains(window.Session.Snapshot.Modules, module => module.Name == name));
            Assert.All(
                new[]
                {
                    WayfarerWanderer.App.EditorActions.Build,
                    WayfarerWanderer.App.EditorActions.RunTests,
                    WayfarerWanderer.App.EditorActions.Run,
                    WayfarerWanderer.App.EditorActions.Debug,
                    WayfarerWanderer.App.EditorActions.NewProject
                },
                id => Assert.True(window.Actions[id].Available(), id));
            Assert.True(window.ToolDock.IsShown(WayfarerWanderer.App.Shell.ToolWindowIds.Build));
            // Building, testing or running brings the tool window its output goes to into view.
            foreach (var (action, shown) in new[]
            {
                (WayfarerWanderer.App.EditorActions.Build, WayfarerWanderer.App.Shell.ToolWindowIds.Build),
                (WayfarerWanderer.App.EditorActions.RunTests, WayfarerWanderer.App.Shell.ToolWindowIds.Tests),
                (WayfarerWanderer.App.EditorActions.Run, WayfarerWanderer.App.Shell.ToolWindowIds.Build),
            })
            {
                foreach (var tool in window.ToolDock.Windows)
                    window.ToolDock.Hide(tool.Id);
                Assert.True(window.Actions.TryExecute(action), action);
                Assert.True(window.ToolDock.IsShown(shown), $"{action} shows {shown}");
                await window.Session.CancelAsync();
                for (int wait = 0; window.Session.IsRunning && wait < 200; wait++)
                    await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            window.Close();
            WorkspaceTrust.TakeBack(trust);
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task AStaleModelTurnsRenameOffKeepsEditsAndARepairTurnsItBackOn()
    {
        string root = WorkspaceImportTests.Copy("maven-layouts");
        string trust = WorkspaceTrust.FolderToTrust(root) ?? root;
        WorkspaceTrust.Trust(root);
        var window = EditorApplication.CreateWindow(new TextDocument("class Main {}"));
        window.Show();
        try
        {
            await window.OpenWorkspaceAsync(root, TestContext.Current.CancellationToken);
            WorkspaceSnapshot good = window.Session!.Snapshot!;
            string source = Path.Combine(root, "core", "src", "main", "java", "example", "core", "Greeting.java");
            window.OpenSource(source);
            TextDocument document = window.Editor.Document;
            document.Edit([new TextEdit(0, 0, "// unsaved\n")], SelectionSet.Single(0), SelectionSet.Single(11));
            string edited = document.Snapshot.ToString();
            Assert.True(window.Actions["app.jvm.rename"].Available());
            Assert.True(window.Actions["app.jvm.complete"].Available());

            string pom = Path.Combine(root, "core", "pom.xml");
            string text = await File.ReadAllTextAsync(pom, TestContext.Current.CancellationToken);
            // The window watches the files the model was read from and reimports on its own after the change.
            Task reimported = SnapshotChangedAsync(window.Session);
            await File.WriteAllTextAsync(pom, text.Replace("</project>", "<broken>", StringComparison.Ordinal), TestContext.Current.CancellationToken);
            await reimported;

            // The last good model stays, marked stale; a workspace rename would edit files from it, so it waits.
            Assert.True(window.Session.IsStale);
            Assert.Same(good, window.Session.Snapshot);
            Assert.False(window.Actions["app.jvm.rename"].Available());
            Assert.True(window.Actions["app.jvm.complete"].Available());
            Assert.Equal(edited, window.Editor.Document.Snapshot.ToString());
            Assert.True(window.Editor.Document.IsModified);

            reimported = SnapshotChangedAsync(window.Session);
            await File.WriteAllTextAsync(pom, text, TestContext.Current.CancellationToken);
            await reimported;
            Assert.False(window.Session.IsStale);
            Assert.True(window.Actions["app.jvm.rename"].Available());
            Assert.Equal(edited, window.Editor.Document.Snapshot.ToString());
        }
        finally
        {
            window.Close();
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(trust);
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task SnapshotChangedAsync(ProjectSession session)
    {
        TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? handler = null;
        handler = () =>
        {
            session.SnapshotChanged -= handler;
            changed.TrySetResult();
        };
        session.SnapshotChanged += handler;
        return changed.Task.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static MenuItem ToggleMenuItem(Window window)
    {
        var menu = window.GetVisualDescendants().OfType<Menu>().Single();
        var run = Assert.IsType<MenuItem>(menu.Items.Cast<MenuItem>().Single(item => item.Header?.ToString() == "Run"));
        return Assert.IsType<MenuItem>(run.Items.Cast<MenuItem>().Single(item =>
            item.Header?.ToString() == "Toggle Breakpoint"));
    }
}
