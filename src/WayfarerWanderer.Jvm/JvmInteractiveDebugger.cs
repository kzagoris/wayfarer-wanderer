using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;
using Passenger.Dap;

namespace WayfarerWanderer.Jvm;

/// <summary>A stack frame: <see cref="Name"/> is <c>Type.method</c>; <see cref="Id"/> asks for its variables while the
/// target stays suspended.</summary>
public sealed record JvmDebugFrame(string Name, string? Path, int Line)
{
    public int Id { get; init; }

    public string Method { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    /// <summary>As IntelliJ lists a frame: <c>main:7, Main (com.example)</c>.</summary>
    public string Display
    {
        get
        {
            string type = Type.Length > 0 ? Type : Name.Contains('.') ? Name[..Name.LastIndexOf('.')] : Name;
            string method = Method.Length > 0 ? Method : Name[(Name.LastIndexOf('.') + 1)..];
            int dot = type.LastIndexOf('.');
            string simple = type[(dot + 1)..];
            string place = Line > 0 ? $"{method}:{Line}, {simple}" : $"{method}, {simple}";
            return dot > 0 ? $"{place} ({type[..dot]})" : place;
        }
    }
}

public sealed record JvmDebugThread(long Id, string Name, string Group, string Status, bool Suspended)
{
    /// <summary>As IntelliJ names a thread: <c>"main"@1 in group "main": RUNNING</c>.</summary>
    public string Display => Group.Length > 0 ? $"\"{Name}\"@{Id} in group \"{Group}\": {Status}" : $"\"{Name}\"@{Id}: {Status}";
}

/// <summary>A variable, field, element or evaluated value; <see cref="Reference"/> is not 0 when it has children.</summary>
public sealed record JvmDebugVariable(string Name, string Value, string Type, int Reference);

/// <summary>A completion for the evaluate box: <see cref="Text"/> replaces <see cref="Length"/> characters from <see cref="Start"/>.</summary>
public sealed record JvmDebugCompletion(string Label, string Text, string Kind, string Detail, int Start, int Length);

public sealed record JvmDebugPause(IReadOnlyList<JvmDebugFrame> Frames, IReadOnlyList<string> Locals)
{
    public long ThreadId { get; init; }

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<JvmDebugThread> Threads { get; init; } = [];

    /// <summary>The top frame's variables, <see cref="Locals"/> as rows.</summary>
    public IReadOnlyList<JvmDebugVariable> Variables { get; init; } = [];
}

/// <summary>One launch. Owns the target program, where its host runs it, and the JDK's JDI adapter process.</summary>
public sealed class JvmInteractiveDebugger : IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly CancellationTokenSource stopping = new();
    private readonly IJvmProgramHost host;
    private IJvmProgram? target;
    private Process? adapter;
    private DapConnection? connection;
    private long thread;
    private int ended;
    private int started;
    private Task adapterErrors = Task.CompletedTask;

    /// <param name="host">Where the target runs; its output goes to <see cref="Output"/> through pipes when none is given.</param>
    public JvmInteractiveDebugger(IJvmProgramHost? host = null)
    {
        this.host = host ?? new PipedProgramHost(line => Output?.Invoke(line));
    }

    public event Action<JvmDebugPause>? Paused;
    public event Action<string>? Output;
    public event Action? Ended;

    /// <summary>The target's exit code, once it has ended on its own.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>The thread steps and Continue act on: the one that stopped, or the one chosen since.</summary>
    public long CurrentThread
    {
        get => Interlocked.Read(ref thread);
        set => Interlocked.Exchange(ref thread, value);
    }

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
            ProcessStartInfo launch = await DirectJvmLaunchAdapter.ResolvedCommandAsync(snapshot, configuration, cancel,
                output: line => Output?.Invoke(line.Line)).ConfigureAwait(false);

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
            dap.OnEvent<JsonElement>("stopped", body => _ = InspectAsync(dap, body));
            dap.Start();
            await dap.RequestAsync("initialize", new { adapterID = "wayfarer-jdi", linesStartAt1 = true, pathFormat = "path" }, cancel).ConfigureAwait(false);
            var listening = await dap.RequestAsync<JsonElement>("listen", new { }, cancel).ConfigureAwait(false);
            int port = listening.GetProperty("port").GetInt32();

            launch.ArgumentList.Insert(0, $"-agentlib:jdwp=transport=dt_socket,server=n,suspend=y,address=127.0.0.1:{port}");
            target = await host.StartAsync(launch, cancel).ConfigureAwait(false);
            Task exited = target.Exited;

            string[] roots = [.. module.SourceRoots.Concat(snapshot.Modules.SelectMany(m => m.SourceRoots))
                .Select(r => r.Path).Distinct(PathNames.Comparer)];
            Task attach = dap.RequestAsync("attach", new { sourcePaths = roots }, cancel);
            if (await Task.WhenAny(attach, exited).WaitAsync(TimeSpan.FromSeconds(45), cancel).ConfigureAwait(false) == exited)
            {
                _ = attach.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw new InvalidOperationException($"Java exited with code {target.Exited.Result} before the debugger attached.");
            }
            await attach.ConfigureAwait(false);
            foreach (var (path, lines) in breakpoints)
                await SetBreakpointsAsync(dap, path, lines, cancel).ConfigureAwait(false);
            await dap.RequestAsync("configurationDone", new { }, cancel).ConfigureAwait(false);
            _ = ObserveEndAsync(target.Exited, dap.Completion);
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

    /// <summary>Suspends every thread; <see cref="Paused"/> follows with the main thread's stack.</summary>
    public Task PauseAsync() => Active().RequestAsync("pause", new { }, stopping.Token);

    private Task ResumeAsync(string command) =>
        Active().RequestAsync(command, new { threadId = CurrentThread }, stopping.Token);

    public async Task<IReadOnlyList<JvmDebugThread>> ThreadsAsync()
    {
        var body = await Active().RequestAsync<JsonElement>("threads", new { }, stopping.Token).ConfigureAwait(false);
        return [.. body.GetProperty("threads").EnumerateArray().Select(item => new JvmDebugThread(
            item.GetProperty("id").GetInt64(), item.GetProperty("name").GetString() ?? "",
            item.TryGetProperty("group", out var group) ? group.GetString() ?? "" : "",
            item.TryGetProperty("status", out var status) ? status.GetString() ?? "" : "",
            item.TryGetProperty("suspended", out var suspended) && suspended.GetBoolean()))];
    }

    /// <summary>A suspended thread's frames, top first; none for one that runs.</summary>
    public async Task<IReadOnlyList<JvmDebugFrame>> FramesAsync(long threadId)
    {
        var stack = await Active().RequestAsync<JsonElement>("stackTrace", new { threadId }, stopping.Token).ConfigureAwait(false);
        return [.. stack.GetProperty("stackFrames").EnumerateArray().Select(item => new JvmDebugFrame(
            item.GetProperty("name").GetString()!,
            item.TryGetProperty("source", out var source) ? source.GetProperty("path").GetString() : null,
            item.GetProperty("line").GetInt32())
        {
            Id = item.GetProperty("id").GetInt32(),
            Method = item.TryGetProperty("method", out var method) ? method.GetString() ?? "" : "",
            Type = item.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
        })];
    }

    /// <summary>A frame's variables (by its <see cref="JvmDebugFrame.Id"/>), or an object's or array's children.</summary>
    public async Task<IReadOnlyList<JvmDebugVariable>> VariablesAsync(int reference)
    {
        var body = await Active().RequestAsync<JsonElement>("variables", new { variablesReference = reference }, stopping.Token).ConfigureAwait(false);
        return [.. body.GetProperty("variables").EnumerateArray().Select(value => new JvmDebugVariable(
            value.GetProperty("name").GetString() ?? "", value.GetProperty("value").GetString() ?? "",
            value.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
            value.TryGetProperty("variablesReference", out var children) ? children.GetInt32() : 0))];
    }

    /// <summary>
    /// An expression's or code fragment's value in a frame; a <see cref="DapException"/> says why it has none.
    /// The "hover" context refuses anything with side effects: assignments, calls and object creation.
    /// </summary>
    public async Task<JvmDebugVariable> EvaluateAsync(string expression, int frameId, string context = "watch")
    {
        var body = await Active().RequestAsync<JsonElement>("evaluate", new { expression, frameId, context }, stopping.Token).ConfigureAwait(false);
        return new JvmDebugVariable(expression, body.GetProperty("result").GetString() ?? "",
            body.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
            body.TryGetProperty("variablesReference", out var children) ? children.GetInt32() : 0);
    }

    /// <summary>Sets a row of the variables tree (a frame's local, a field or an element) to an expression's value.</summary>
    public async Task<JvmDebugVariable> SetVariableAsync(int reference, string name, string value)
    {
        var body = await Active().RequestAsync<JsonElement>("setVariable", new { variablesReference = reference, name, value }, stopping.Token).ConfigureAwait(false);
        return new JvmDebugVariable(name, body.GetProperty("value").GetString() ?? "",
            body.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
            body.TryGetProperty("variablesReference", out var children) ? children.GetInt32() : 0);
    }

    /// <summary>Completions at a caret (0-based) in the evaluate box: members after a dot, otherwise the names in scope.</summary>
    public async Task<IReadOnlyList<JvmDebugCompletion>> CompletionsAsync(string text, int caret, int frameId)
    {
        var body = await Active().RequestAsync<JsonElement>("completions", new { text, column = caret + 1, frameId }, stopping.Token).ConfigureAwait(false);
        return [.. body.GetProperty("targets").EnumerateArray().Select(item => new JvmDebugCompletion(
            item.GetProperty("label").GetString() ?? "",
            item.TryGetProperty("text", out var insert) ? insert.GetString() ?? "" : item.GetProperty("label").GetString() ?? "",
            item.TryGetProperty("type", out var kind) ? kind.GetString() ?? "" : "",
            item.TryGetProperty("detail", out var detail) ? detail.GetString() ?? "" : "",
            item.TryGetProperty("start", out var start) ? start.GetInt32() : caret,
            item.TryGetProperty("length", out var length) ? length.GetInt32() : 0))];
    }

    private DapConnection Active() => connection ?? throw new InvalidOperationException("The debugger is not connected.");

    private async Task InspectAsync(DapConnection dap, JsonElement stopped)
    {
        try
        {
            CancellationToken cancel = stopping.Token;
            long id = stopped.GetProperty("threadId").GetInt64();
            CurrentThread = id;
            IReadOnlyList<JvmDebugThread> threads = await ThreadsAsync().ConfigureAwait(false);
            IReadOnlyList<JvmDebugFrame> frames = id == 0 ? [] : await FramesAsync(id).ConfigureAwait(false);
            IReadOnlyList<JvmDebugVariable> variables = frames.Count == 0 ? [] : await VariablesAsync(frames[0].Id).ConfigureAwait(false);
            if (!cancel.IsCancellationRequested)
            {
                Paused?.Invoke(new(frames, [.. variables.Select(v => $"{v.Name} = {v.Value}")])
                {
                    ThreadId = id,
                    Reason = stopped.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "" : "",
                    Threads = threads,
                    Variables = variables,
                });
            }
        }
        catch (Exception ex) when (ex is DapException or OperationCanceledException or IOException or InvalidOperationException)
        {
            if (!stopping.IsCancellationRequested) Output?.Invoke(ex.Message);
        }
    }

    private async Task ReadAdapterErrorsAsync(StreamReader errors)
    {
        while (await errors.ReadLineAsync().ConfigureAwait(false) is { } line) Output?.Invoke(line);
    }

    private async Task ObserveEndAsync(Task<int> targetExit, Task adapterExit)
    {
        if (await Task.WhenAny(targetExit, adapterExit).ConfigureAwait(false) == targetExit)
            ExitCode = targetExit.Result;
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
        if (target is { } program)
        {
            program.Stop();
            await program.DisposeAsync().ConfigureAwait(false);
        }
        if (adapter is { } process)
        {
            ChildProcesses.Kill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        await adapterErrors.ConfigureAwait(false);
        if (connection != null) await connection.DisposeAsync().ConfigureAwait(false);
        connection = null;
        adapter?.Dispose();
        target = null;
        adapter = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
