// From Passenger 494009d7 src/Passenger.App/MainWindow.Frame.cs, MIT (notices/Passenger/LICENSE): its places in
// Wayfarer's menu table (MainMenuPlaces), its actions in Wayfarer's registry.
using Avalonia.Controls;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Settings;
using A = WayfarerWanderer.App.Shell.AppActions;

namespace WayfarerWanderer.App;

/// <summary>The window's own frame: Zoom In, Zoom Out and Reset Zoom, which step the whole window through Settings' Display
/// scale and keep it there; Full Screen; Hide Side Windows; Jump to Last Tool Window, back to the tool window that last had
/// the keys; and Close Tool Window Tab, the tab shown of the tool window with the keys closed as its × closes it. Each runs
/// from the terminal too.</summary>
public sealed partial class EditorWindow
{
    private const string FrameGroup = "IDE";

    /// <summary>The window's state before Full Screen, which it goes back to.</summary>
    private WindowState beforeFullScreen = WindowState.Normal;

    private void AddFrameActions()
    {
        Frame(A.ZoomIn, "Zoom In", _ => SettingsKept.Update(s => Declared.DisplayScale.Stepped(s, up: true) ?? s),
            _ => Declared.DisplayScale.Stepped(SettingsKept.Value, up: true) is null ? Refusals.ZoomedAsFarAsItGoes(up: true) : null);
        Frame(A.ZoomOut, "Zoom Out", _ => SettingsKept.Update(s => Declared.DisplayScale.Stepped(s, up: false) ?? s),
            _ => Declared.DisplayScale.Stepped(SettingsKept.Value, up: false) is null ? Refusals.ZoomedAsFarAsItGoes(up: false) : null);
        Frame(A.ResetZoom, "Reset Zoom", _ => SettingsKept.Update(Declared.DisplayScale.Unset), null);
        Actions.Registry.Add(new IdeAction(A.FullScreen, "Full Screen", FrameGroup, _ => ToggleFullScreen())
        {
            IsChecked = _ => WindowState == WindowState.FullScreen,
            FromTerminal = true,
        });
        Frame(A.HideSideWindows, "Hide Side Windows", _ => ToolDock.ToggleHideSides(), null);
        Frame(A.LastToolWindow, "Jump to Last Tool Window", _ => ToolDock.Activate(ToolDock.LastWithKeys!.Id),
            _ => ToolDock.LastWithKeys is null ? Refusals.NoToolWindowHadKeys : null);
        Frame(A.CloseToolTab, "Close Tool Window Tab", context =>
        {
            if (!TabsWithKeys(context)!.CloseTab())
            {
                StatusText = Refusals.ToolTabStays;
            }
        }, context => TabsWithKeys(context) is null ? Refusals.NoToolTabs : null);
    }

    /// <summary>One of the frame's actions, which the terminal's keys run too.</summary>
    private void Frame(string id, string title, Action<ActionContext> run, Func<ActionContext, string?>? whyNot) =>
        Actions.Registry.Add(new IdeAction(id, title, FrameGroup, run)
        {
            WhyNot = whyNot ?? (_ => null),
            FromTerminal = true,
        });

    /// <summary>Full Screen, or back to the state the window had before it.</summary>
    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = beforeFullScreen;
            return;
        }
        beforeFullScreen = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        WindowState = WindowState.FullScreen;
    }
}
