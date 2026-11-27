using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.Text;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Docking;

namespace WayfarerWanderer.Tests;

public class ToolDockTests
{
    [AvaloniaFact]
    public void StripeButtonOpensAndHidesItsToolWindow()
    {
        var window = EditorApplication.CreateWindow(new TextDocument("class Main {}"));
        window.Width = 900;
        window.Height = 600;
        window.Show();
        try
        {
            Assert.Equal([ToolWindowIds.Explorer, ToolWindowIds.Build, ToolWindowIds.Tests, ToolWindowIds.Debug, ToolWindowIds.Run, ToolWindowIds.Terminal,
                ToolWindowIds.Commit, ToolWindowIds.Git], window.ToolDock.Windows.Select(w => w.Id));
            Assert.False(window.ToolDock.IsShown(ToolWindowIds.Build));
            Click(window, StripeButton(window, ToolWindowIds.Build));
            Assert.True(window.ToolDock.IsShown(ToolWindowIds.Build));
            Assert.True(window.BuildTool.IsVisible);
            Assert.True(window.BuildTool.Bounds.Height > 100, $"Build is {window.BuildTool.Bounds.Height} high");
            Assert.True(window.Editor.Bounds.Height > 100, $"the editor is {window.Editor.Bounds.Height} high");
            window.BuildTool.Append("> mvn -q test");
            window.BuildTool.Append("BUILD SUCCESS");
            using (var frame = window.CaptureRenderedFrame())
            {
                string folder = Environment.GetEnvironmentVariable("WAYFARER_TEST_OUTPUT") ??
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../test-results"));
                Directory.CreateDirectory(folder);
                frame!.Save(Path.Combine(folder, "tool-dock-build.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            Click(window, StripeButton(window, ToolWindowIds.Tests));
            Assert.True(window.ToolDock.IsShown(ToolWindowIds.Tests));
            Assert.False(window.ToolDock.IsShown(ToolWindowIds.Build));

            Click(window, StripeButton(window, ToolWindowIds.Tests));
            Assert.DoesNotContain(window.ToolDock.Windows, w => w.IsShown && w.Anchor == ToolAnchor.BottomLeft);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void FindActionListsTheShowActions()
    {
        var window = EditorApplication.CreateWindow(new TextDocument("class Main {}"));
        window.Show();
        try
        {
            EditorAction show = Assert.Single(window.Actions.Find("Build"), a => a.Id == AppActions.ShowBuild);
            Assert.True(show.TryExecute());
            Assert.True(window.ToolDock.IsShown(ToolWindowIds.Build));
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void LayoutKeepsWhatIsOpenAndTheSizes()
    {
        DockLayout layout = new()
        {
            Windows = new() { [ToolWindowIds.Build] = new DockedWindow(ToolAnchor.BottomLeft, 0, true) { Mode = (ToolMode)99 } },
            BottomHeight = -5,
        };
        DockLayout clamped = layout.Clamped();
        Assert.Equal(ToolMode.Docked, clamped.Windows[ToolWindowIds.Build].Mode);
        Assert.Equal(0, clamped.BottomHeight);
    }

    /// <summary>Passenger 0.4.6's frame actions: Hide Side Windows leaves the windows below the editors, Zoom steps Settings'
    /// Display scale and says so at its end, and Code › Folding holds the levels' submenus.</summary>
    [AvaloniaFact]
    public void FrameActionsHideTheSidesAndZoomTheWholeWindow()
    {
        var window = EditorApplication.CreateWindow(new TextDocument("class Main {}"));
        window.Width = 900;
        window.Height = 600;
        window.Show();
        try
        {
            window.ToolDock.Show(ToolWindowIds.Explorer);
            window.ToolDock.Show(ToolWindowIds.Build);
            Assert.Null(window.Actions.Registry.Run(AppActions.HideSideWindows));
            Assert.False(window.ToolDock.IsShown(ToolWindowIds.Explorer));
            Assert.True(window.ToolDock.IsShown(ToolWindowIds.Build));
            Assert.Null(window.Actions.Registry.Run(AppActions.HideSideWindows));
            Assert.True(window.ToolDock.IsShown(ToolWindowIds.Explorer));

            Assert.Equal(1, window.SettingsKept.Value.DisplayScale);
            Assert.Equal("The window is as small as it goes", window.Actions.Registry.Run(AppActions.ZoomOut));
            Assert.Null(window.Actions.Registry.Run(AppActions.ZoomIn));
            Assert.Equal(1.25, window.SettingsKept.Value.DisplayScale);
            Assert.Null(window.Actions.Registry.Run(AppActions.ResetZoom));
            Assert.Equal(1, window.SettingsKept.Value.DisplayScale);

            MenuEntry folding = Assert.Single(window.MainMenuEntries.Single(m => m.Title == "Code").Items!, i => i.Title == "Folding");
            MenuEntry levels = Assert.Single(folding.Items!, i => i.Title == "Expand to Level");
            Assert.Equal(Passenger.Editor.EditorActions.UnfoldToLevels, levels.Items!.Select(i => i.Action));
        }
        finally
        {
            window.SettingsKept.Update(_ => new App.Shell.Settings.AppSettings());
            window.Close();
        }
    }

    private static ToggleButton StripeButton(Window window, string id) =>
        window.GetVisualDescendants().OfType<ToggleButton>().Single(b => Equals(b.Tag, id) && b.IsEffectivelyVisible);

    private static void Click(Window window, Control target)
    {
        window.UpdateLayout();
        Point at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.UpdateLayout();
    }
}
