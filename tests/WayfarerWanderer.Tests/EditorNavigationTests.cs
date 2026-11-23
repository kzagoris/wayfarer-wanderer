using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell.Navigation;

namespace WayfarerWanderer.Tests;

/// <summary>Getting about the files open, ported from Passenger v0.4.2: the Switcher, the navigation bar over the editor,
/// a word's places lit by a double click, Ctrl+click's links and declarations, and Read as Text.</summary>
public sealed class EditorNavigationTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("wayfarer-editor-navigation-").FullName);
    private readonly string main;
    private readonly string service;

    public EditorNavigationTests()
    {
        Write("pom.xml", "<project />");
        main = Write("src/main/java/demo/Main.java", "package demo;\n\nclass Main {\n    int count;\n\n    void run() {\n        count = count + 1;\n    }\n}\n");
        service = Write("src/main/java/demo/OrderService.java", "package demo;\n\nclass OrderService {}\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A watcher still letting go: the temporary folder is the system's to clear.
        }
    }

    [AvaloniaFact]
    public async Task CtrlTabPressedAndLetGoGoesBackToTheFileShownBeforeAndHeldShowsTheSwitcher()
    {
        EditorWindow window = await OpenAsync();
        try
        {
            window.OpenSource(main);
            window.OpenSource(service);
            Assert.Equal(["OrderService.java", "Main.java"], window.SwitcherRows().Where(r => r.Path is not null).Select(r => r.Name));
            Assert.Contains(window.SwitcherRows(), r => r.Window?.Title == "Project");

            // Pressed and let go at once: the file used before, and again back.
            CtrlTab(window);
            LetGoOfCtrl(window);
            Assert.Equal(main, window.SourcePath);
            CtrlTab(window);
            LetGoOfCtrl(window);
            Assert.Equal(service, window.SourcePath);

            // Held: the popup shows after a moment, Tab moves its choice, and letting go goes to it.
            CtrlTab(window);
            Assert.Equal(1, window.SwitchingTo);
            SwitcherPopup popup = await Shown<SwitcherPopup>(window);
            Assert.Equal("Main.java", popup.Selected!.Name);
            CtrlTab(window, shift: true);
            Assert.Equal("OrderService.java", popup.Selected!.Name);
            CtrlTab(window);
            CtrlTab(window);
            // Past the files, the tool windows by name.
            WayfarerWanderer.App.Shell.Docking.ToolWindow tool = Assert.IsType<WayfarerWanderer.App.Shell.Docking.ToolWindow>(popup.Selected!.Window);
            Assert.Equal(window.SwitcherRows().Where(r => r.Window is not null).Select(r => r.Name).Order(StringComparer.CurrentCultureIgnoreCase).First(), tool.Title);
            LetGoOfCtrl(window);
            Assert.Null(window.SearchShown);
            Assert.Null(window.SwitchingTo);
            Assert.True(window.ToolDock.IsShown(tool.Id));

            // Asked for from Find Action, it waits for Enter.
            Assert.True(window.RunFromMenu(WayfarerWanderer.App.Shell.AppActions.Switcher));
            SwitcherPopup asked = Assert.IsType<SwitcherPopup>(window.SearchShown);
            asked.Choose();
            Assert.Equal(main, window.SourcePath);
        }
        finally
        {
            window.Close();
        }
    }

    private static void CtrlTab(EditorWindow window, bool shift = false)
    {
        window.KeyPress(Key.Tab, RawInputModifiers.Control | (shift ? RawInputModifiers.Shift : RawInputModifiers.None), PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void LetGoOfCtrl(EditorWindow window)
    {
        window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
        Dispatcher.UIThread.RunJobs();
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

    /// <summary>The popup of a kind once it shows over the window, after the work its action began.</summary>
    private static async Task<T> Shown<T>(EditorWindow window)
        where T : Control
    {
        for (int i = 0; i < 200 && window.SearchShown is not T; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<T>(window.SearchShown);
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return Path.GetFullPath(path);
    }
}
