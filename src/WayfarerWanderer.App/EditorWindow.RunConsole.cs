using Avalonia.Controls;
using Passenger.Core;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Docking;
using WayfarerWanderer.App.Shell.Settings;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>The Run and Debug tool windows' sessions, as IntelliJ's: the program runs in a console that takes its input,
/// each window has its session's buttons (Rerun, Stop, and Debug's Resume, Pause and steps), and the editor marks the line
/// the debugger stopped on.</summary>
public sealed partial class EditorWindow
{
    /// <summary>How the last program was started, so Rerun starts it so again: null before any.</summary>
    private bool? lastLaunchDebugs;

    /// <summary>The Run tool window: Rerun and Stop, and the console the program runs in.</summary>
    public RunToolWindow RunTool { get; } = new();

    private void AddRunAndDebugTools()
    {
        ToolDock.Add(new ToolWindow(ToolWindowIds.Run, "Run", IconKeys.Run, RunTool, ToolAnchor.BottomLeft) { Action = AppActions.ShowRun, ActionTitle = "Run Tool Window" });
        RunTool.SetTools(
        [
            ActionButton(EditorActions.Rerun, IconKeys.Rerun, "Rerun"),
            ActionButton(EditorActions.Stop, IconKeys.Stop, "Stop", StyleClasses.ToolbarStop),
        ]);
        DebugTool.SetTools(
        [
            ActionButton(EditorActions.Rerun, IconKeys.Rerun, "Rerun"),
            ActionButton(EditorActions.Stop, IconKeys.Stop, "Stop", StyleClasses.ToolbarStop),
            Rule(),
            ActionButton(EditorActions.Continue, IconKeys.Resume, "Resume Program"),
            ActionButton(EditorActions.Pause, IconKeys.Pause, "Pause Program"),
            Rule(),
            ActionButton(EditorActions.StepOver, IconKeys.StepOver, "Step Over"),
            ActionButton(EditorActions.StepInto, IconKeys.StepInto, "Step Into"),
            ActionButton(EditorActions.StepOut, IconKeys.StepOut, "Step Out"),
        ]);
        // A program's start shows its console, which takes the keys then, as IntelliJ's Run window does.
        RunTool.Console.Started += (_, _) =>
        {
            ToolDock.Show(ToolWindowIds.Run);
            RunTool.Console.View.Focus();
        };
        DebugTool.FrameChosen += frame =>
        {
            OpenFrame(frame);
            MarkExecution(frame);
        };
        FollowTerminalView(RunTool.Console.View, SettingsKept.Effective, null);
        FollowTerminalView(DebugTool.Console.View, SettingsKept.Effective, null);
    }

    private static Border Rule() => new() { Classes = { StyleClasses.ToolbarRule } };

    private void AddRunAndDebugActions()
    {
        Actions.Add(new EditorAction(EditorActions.Pause, "Pause Program", () => Session is { IsDebugging: true, IsPaused: false },
            () => UnawaitedWork.Observe(Session!.PauseAsync())));
        Actions.Add(new EditorAction(EditorActions.Rerun, "Rerun", () => Session is { IsTrusted: true } && lastLaunchDebugs is not null,
            () => UnawaitedWork.Observe(RerunAsync())));
    }

    /// <summary>Rerun: the running program stopped, then started again as it was, run or debugged.</summary>
    private async Task RerunAsync()
    {
        if (Session is not { } session || lastLaunchDebugs is not { } debugs) return;
        if (session.IsRunning) await session.CancelAsync();
        if (Session != session) return;
        if (debugs) DebugCurrent();
        else RunCurrent();
    }

    /// <summary>The session's own events: a stop marks its line, and running on takes the mark away.</summary>
    private void FollowDebugSession(ProjectSession session)
    {
        session.PausedAt += frame =>
        {
            OpenFrame(frame);
            MarkExecution(frame);
            UpdateToolbar();
        };
        session.Resumed += () =>
        {
            Breakpoints.MarkExecution(null, 0);
            UpdateToolbar();
        };
    }

    private void MarkExecution(JvmDebugFrame frame)
    {
        if (frame.Path is { } path && frame.Line > 0) Breakpoints.MarkExecution(path, frame.Line - 1);
    }
}
