using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;
using Passenger.Dap;

namespace WayfarerWanderer.Jvm;

public sealed record JvmDebugFrame(string Name, string? Path, int Line);

public sealed record JvmDebugPause(IReadOnlyList<JvmDebugFrame> Frames, IReadOnlyList<string> Locals);

/// <summary>One launch. Owns both the target process and the JDK's JDI adapter process.</summary>
public sealed class JvmInteractiveDebugger : IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly CancellationTokenSource stopping = new();
    private Process? target;
    private Process? adapter;
    private DapConnection? connection;
    private long thread;
    private int ended;
    private int started;
    private Task targetOutput = Task.CompletedTask;
    private Task adapterErrors = Task.CompletedTask;

    public event Action<JvmDebugPause>? Paused;
    public event Action<string>? Output;
    public event Action? Ended;

    public async Task StartAsync(WorkspaceSnapshot snapshot, JvmLaunchConfiguration configuration,
        IReadOnlyDictionary<string, int[]> breakpoints, CancellationToken cancellationToken = default)
    {
        WorkspaceTrust.Check(snapshot.Root);
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("Create a new debugger for each launch.");
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        CancellationToken cancel = startup.Token;
        await lifecycle.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            JvmModule module = snapshot.Module(configuration.Module)
                ?? throw new InvalidDataException($"Unknown module '{configuration.Module}'.");
            ProcessStartInfo launch = await DirectJvmLaunchAdapter.ResolvedCommandAsync(snapshot, configuration, cancel).ConfigureAwait(false);

            // The adapter listens and the target connects out to it, so the target never exposes a
            // JDWP port. The adapter accepts one connection and then stops listening.
            string source = Path.Combine(AppContext.BaseDirectory, "Debug", "WayfarerDebugAdapter.java");
            string? toolingHome = snapshot.ToolingToolchain.Home;
            string toolingJava = string.IsNullOrWhiteSpace(toolingHome) ? launch.FileName :
                Path.Combine(toolingHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            ProcessStartInfo startAdapter = ChildProcesses.Command(toolingJava, ["--add-modules", "jdk.jdi", source], AppContext.BaseDirectory);
            startAdapter.RedirectStandardInput = startAdapter.RedirectStandardOutput = startAdapter.RedirectStandardError = true;
            adapter = await ChildProcesses.StartAsync(startAdapter).ConfigureAwait(false);
            ChildProcesses.HoldFamily(adapter);
            adapterErrors = ReadAdapterErrorsAsync(adapter.StandardError);
            var dap = new DapConnection(adapter.StandardOutput.BaseStream, adapter.StandardInput.BaseStream, line => Output?.Invoke(line));
            connection = dap;
            dap.OnEvent<JsonElement>("stopped", body => _ = InspectAsync(dap, body.GetProperty("threadId").GetInt64()));
            dap.Start();
            await dap.RequestAsync("initialize", new { adapterID = "wayfarer-jdi", linesStartAt1 = true, pathFormat = "path" }, cancel).ConfigureAwait(false);
            var listening = await dap.RequestAsync<JsonElement>("listen", new { }, cancel).ConfigureAwait(false);
            int port = listening.GetProperty("port").GetInt32();

            launch.ArgumentList.Insert(0, $"-agentlib:jdwp=transport=dt_socket,server=n,suspend=y,address=127.0.0.1:{port}");
            launch.RedirectStandardOutput = launch.RedirectStandardError = true;
            target = await ChildProcesses.StartAsync(launch).ConfigureAwait(false);
            ChildProcesses.HoldFamily(target);
            targetOutput = ChildProcesses.Hear(target, line => Output?.Invoke(line), line => Output?.Invoke(line));
            Task exited = target.WaitForExitAsync();

            string[] roots = [.. module.SourceRoots.Concat(snapshot.Modules.SelectMany(m => m.SourceRoots))
                .Select(r => r.Path).Distinct(PathNames.Comparer)];
            Task attach = dap.RequestAsync("attach", new { sourcePaths = roots }, cancel);
            if (await Task.WhenAny(attach, exited).WaitAsync(TimeSpan.FromSeconds(45), cancel).ConfigureAwait(false) == exited)
            {
                _ = attach.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw new InvalidOperationException($"Java exited with code {target.ExitCode} before the debugger attached.");
            }
            await attach.ConfigureAwait(false);
            foreach (var (path, lines) in breakpoints)
                await SetBreakpointsAsync(dap, path, lines, cancel).ConfigureAwait(false);
            await dap.RequestAsync("configurationDone", new { }, cancel).ConfigureAwait(false);
            _ = ObserveEndAsync(exited, dap.Completion);
        }
        catch
        {
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
        finally { lifecycle.Release(); }
    }

    public Task SetBreakpointsAsync(string path, int[] lines) =>
        SetBreakpointsAsync(Active(), path, lines, stopping.Token);

    private static Task SetBreakpointsAsync(DapConnection dap, string path, int[] lines, CancellationToken token) =>
        dap.RequestAsync("setBreakpoints", new { source = new { path = Path.GetFullPath(path) },
            breakpoints = lines.Distinct().Select(line => new { line }) }, token);

    public Task ContinueAsync() => ResumeAsync("continue");
    public Task StepIntoAsync() => ResumeAsync("stepIn");
    public Task StepOverAsync() => ResumeAsync("next");
    public Task StepOutAsync() => ResumeAsync("stepOut");

    private Task ResumeAsync(string command) =>
        Active().RequestAsync(command, new { threadId = Interlocked.Read(ref thread) }, stopping.Token);

    private DapConnection Active() => connection ?? throw new InvalidOperationException("The debugger is not connected.");

    private async Task InspectAsync(DapConnection dap, long id)
    {
        try
        {
            CancellationToken cancel = stopping.Token;
            Interlocked.Exchange(ref thread, id);
            var stack = await dap.RequestAsync<JsonElement>("stackTrace", new { threadId = id }, cancel).ConfigureAwait(false);
            List<JvmDebugFrame> frames = [];
            List<string> locals = [];
            int? top = null;
            foreach (var item in stack.GetProperty("stackFrames").EnumerateArray())
            {
                top ??= item.GetProperty("id").GetInt32();
                string? path = item.TryGetProperty("source", out var source) ? source.GetProperty("path").GetString() : null;
                frames.Add(new(item.GetProperty("name").GetString()!, path, item.GetProperty("line").GetInt32()));
            }
            if (top is int frameId)
            {
                var scopes = await dap.RequestAsync<JsonElement>("scopes", new { frameId }, cancel).ConfigureAwait(false);
                foreach (var scope in scopes.GetProperty("scopes").EnumerateArray())
                {
                    int reference = scope.GetProperty("variablesReference").GetInt32();
                    var values = await dap.RequestAsync<JsonElement>("variables", new { variablesReference = reference }, cancel).ConfigureAwait(false);
                    foreach (var value in values.GetProperty("variables").EnumerateArray())
                        locals.Add($"{value.GetProperty("name").GetString()} = {value.GetProperty("value").GetString()}");
                }
            }
            if (!cancel.IsCancellationRequested) Paused?.Invoke(new(frames, locals));
        }
        catch (Exception ex) when (ex is DapException or OperationCanceledException or IOException)
        {
            if (!stopping.IsCancellationRequested) Output?.Invoke(ex.Message);
        }
    }

    private async Task ReadAdapterErrorsAsync(StreamReader errors)
    {
        while (await errors.ReadLineAsync().ConfigureAwait(false) is { } line) Output?.Invoke(line);
    }

    private async Task ObserveEndAsync(Task targetExit, Task adapterExit)
    {
        await Task.WhenAny(targetExit, adapterExit).ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { await CleanupAsync().ConfigureAwait(false); }
        finally { lifecycle.Release(); }
        if (Interlocked.Exchange(ref ended, 1) == 0) Ended?.Invoke();
    }

    private async Task CleanupAsync()
    {
        foreach (var process in new[] { target, adapter })
        {
            if (process == null) continue;
            ChildProcesses.Kill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        await Task.WhenAll(targetOutput, adapterErrors).ConfigureAwait(false);
        if (connection != null) await connection.DisposeAsync().ConfigureAwait(false);
        connection = null;
        target?.Dispose();
        adapter?.Dispose();
        target = adapter = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
