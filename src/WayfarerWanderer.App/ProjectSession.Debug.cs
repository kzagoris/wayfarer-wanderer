using Passenger.Core;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

public sealed partial class ProjectSession
{
    private JvmInteractiveDebugger? debugger;
    private Task? debugWork;
    private bool ready;
    private readonly Dictionary<string, int[]> pendingBreakpoints = new(PathNames.Comparer);

    public bool IsPaused { get; private set; }
    public event Action<JvmDebugFrame>? PausedAt;

    public Task DebugAsync(JvmLaunchConfiguration configuration, IReadOnlyDictionary<string, int[]> breakpoints)
    {
        WorkspaceTrust.Check(Root);
        if (IsRunning) throw new InvalidOperationException("Another workspace command is running.");
        return debugWork = DebugLoopAsync(configuration, breakpoints);
    }

    private async Task DebugLoopAsync(JvmLaunchConfiguration configuration, IReadOnlyDictionary<string, int[]> breakpoints)
    {
        CancellationTokenSource running = BeginOperation(default);
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = new JvmInteractiveDebugger();
        debugger = active;
        void IfCurrent(Action update) => OnUi(() =>
        {
            if (debugger == active && !running.IsCancellationRequested) update();
        });
        active.Output += line => IfCurrent(() => BuildWindow.Append(line));
        active.Ended += () => ended.TrySetResult();
        active.Paused += pause => IfCurrent(() =>
        {
            IsPaused = true;
            DebugWindow.Reset();
            DebugWindow.Status = "Paused";
            foreach (var frame in pause.Frames)
                DebugWindow.Frames.Add(frame.Line > 0 ? $"{frame.Name} ({Path.GetFileName(frame.Path)}:{frame.Line})" : frame.Name);
            foreach (string local in pause.Locals) DebugWindow.Variables.Add(local);
            if (pause.Frames.FirstOrDefault() is { } top) PausedAt?.Invoke(top);
        });
        DebugWindow.Reset();
        DebugWindow.Status = "Starting debuggerâ€¦";
        BuildWindow.Reset();
        string status = "Debugger finished";
        try
        {
            WorkspaceSnapshot snapshot = Snapshot ?? await ImportAsync(running.Token);
            var saved = (await launches.ListAsync(running.Token)).FirstOrDefault(item =>
                item.MainClass == configuration.MainClass && snapshot.Module(item.Module) == snapshot.Module(configuration.Module));
            await active.StartAsync(snapshot, saved ?? configuration, breakpoints, running.Token);
            ready = true;
            if (!IsPaused) DebugWindow.Status = "Running";
            foreach (var (path, lines) in pendingBreakpoints.ToArray())
                await active.SetBreakpointsAsync(path, lines);
            await ended.Task.WaitAsync(running.Token);
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested)
        {
            status = "Debugger stopped";
        }
        catch (Exception error)
        {
            status = error.Message;
            BuildWindow.Append(error.Message);
        }
        finally
        {
            ready = false;
            debugger = null;
            IsPaused = false;
            pendingBreakpoints.Clear();
            try { await active.DisposeAsync(); }
            finally
            {
                DebugWindow.Reset();
                DebugWindow.Status = status;
                EndOperation(running);
            }
        }
    }

    internal async Task UpdateBreakpointsAsync(string path, int[] lines)
    {
        if (debugger is not { } active) return;
        if (!ready)
        {
            pendingBreakpoints[path] = lines;
            return;
        }
        try { await active.SetBreakpointsAsync(path, lines); }
        catch (Exception error) when (active == debugger) { DebugWindow.Status = error.Message; }
    }

    internal async Task ResumeAsync(Func<JvmInteractiveDebugger, Task> command)
    {
        if (!IsPaused || debugger is not { } active) return;
        IsPaused = false;
        DebugWindow.Reset();
        DebugWindow.Status = "Running";
        try { await command(active); }
        catch (Exception error) when (active == debugger)
        {
            IsPaused = true;
            DebugWindow.Status = error.Message;
        }
    }
}
