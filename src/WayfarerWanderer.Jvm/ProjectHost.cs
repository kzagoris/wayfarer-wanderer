using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

/// <summary>The JVM project host could not answer: it would not start (no host staged, no JVM new enough), it ended or
/// stopped answering while a request waited, or the request failed in it. The one failure a caller catches.</summary>
public sealed class ProjectHostFailed(string reason, Exception? inner = null, bool hostEnded = false) : Exception(reason, inner)
{
    public string Reason { get; } = reason;

    /// <summary>The host's process ended under the request (it crashed, was killed, or stopped answering): a host
    /// started anew may answer it.</summary>
    public bool HostEnded { get; } = hostEnded;
}

/// <summary>How a host is started and watched. The defaults are the product's; tests shorten them.</summary>
public sealed record ProjectHostOptions
{
    public static ProjectHostOptions Default { get; } = new();

    /// <summary>How long the host has to write its first line.</summary>
    public TimeSpan StartLimit { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How often a host with a request waiting is pinged.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many pings in a row may go unanswered before the host is taken to have hung and is ended.</summary>
    public int MissedPings { get; init; } = 3;

    /// <summary>Environment variables given to the host beyond the app's.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// One JVM project host (<c>jvm-host/host.jar</c>): build evaluation in a JVM process of its own, as Passenger's
/// project host is MSBuild's. Requests go out as lines of JSON and their answers, partial ones first, come back on the
/// reader's thread. While any request waits, the host is pinged; one that stops answering is ended, and every request
/// waiting fails as at its own end. Ending the host ends that process alone, never the Gradle daemon it talked to.
/// </summary>
public sealed class JvmProjectHost : IAsyncDisposable
{
    /// <summary>A line of the host's standard error, for the IDE's log.</summary>
    public static event Action<int, string>? ErrorLine;

    private readonly Process process;
    private readonly StreamWriter input;
    private readonly ProjectHostOptions options;
    private readonly ConcurrentDictionary<int, Pending> pending = new();
    private readonly object writeGate = new();
    private readonly CancellationTokenSource watching = new();
    private readonly Queue<string> recentErrors = new();
    private int nextId;
    private long lastHeard;
    private volatile bool gone;
    private string? crashed;
    private string? endedBecause;

    private JvmProjectHost(Process process, ProjectHostOptions options, HostReady ready, ToolingJvm jvm)
    {
        this.process = process;
        this.options = options;
        input = new StreamWriter(ChildProcesses.Input(process).BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        Ready = ready;
        Jvm = jvm;
        ProcessId = process.Id;
        lastHeard = Stopwatch.GetTimestamp();
    }

    public int ProcessId { get; }

    public HostReady Ready { get; }

    public ToolingJvm Jvm { get; }

    public bool HasExited => gone || HasEnded(process);

    /// <summary>The folders this host serves (<see cref="ProjectHosts"/>).</summary>
    internal HashSet<string> Served { get; } = new(PathNames.Comparer);

    /// <summary>The staged host's jar: beside the app (<c>jvm-host/host.jar</c>), else where <c>WAYFARER_JVM_HOST</c>
    /// says, else under a repository's <c>artifacts/jvm-host</c> above the app; null when none is found.</summary>
    public static string? Locate()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "jvm-host", "host.jar");
        if (File.Exists(beside))
        {
            return beside;
        }
        if (System.Environment.GetEnvironmentVariable("WAYFARER_JVM_HOST") is { Length: > 0 } named)
        {
            string jar = Directory.Exists(named) ? Path.Combine(named, "host.jar") : named;
            if (File.Exists(jar))
            {
                return jar;
            }
        }
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string staged = Path.Combine(folder.FullName, "artifacts", "jvm-host", "host.jar");
            if (File.Exists(staged))
            {
                return staged;
            }
        }
        return null;
    }

    /// <summary>Starts a host on <paramref name="jvm"/> and waits for its first line; a <see cref="ProjectHostFailed"/>
    /// when it cannot start or says why it will not.</summary>
    public static async Task<JvmProjectHost> StartAsync(ToolingJvm jvm, ProjectHostOptions? options = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(jvm);
        options ??= ProjectHostOptions.Default;
        string jar = Locate() ?? throw new ProjectHostFailed("The JVM project host is missing from Wayfarer's folder (jvm-host/host.jar).");
        // Run in the host's own folder, never the workspace's, which may be deleted under it.
        ProcessStartInfo start = ChildProcesses.Command(jvm.Java, ["-Xss4m", "-XX:+UseSerialGC", "-jar", jar], Path.GetDirectoryName(jar));
        foreach (KeyValuePair<string, string> variable in options.Environment)
        {
            start.Environment[variable.Key] = variable.Value;
        }
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        Process process;
        try
        {
            process = await ChildProcesses.StartAsync(start).ConfigureAwait(false);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            throw new ProjectHostFailed($"The JVM project host could not start on {jvm.Home}: {error.Message}", error);
        }
        TextReader output = ChildProcesses.Output(process);
        string? first;
        try
        {
            first = await output.ReadLineAsync(cancel).AsTask().WaitAsync(options.StartLimit, cancel).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            End(process);
            process.Dispose();
            throw error is TimeoutException ? new ProjectHostFailed($"The JVM project host did not start within {options.StartLimit.TotalSeconds:0} seconds.", error) : error;
        }
        HostReady ready;
        try
        {
            ready = ReadReady(first ?? throw new ProjectHostFailed($"The JVM project host ended as it started ({Exited(process)})."));
        }
        catch
        {
            End(process);
            process.Dispose();
            throw;
        }
        JvmProjectHost host = new(process, options, ready, jvm);
        _ = Task.Run(() => host.ReadAsync(output));
        _ = Task.Run(() => host.ReadErrorsAsync(ChildProcesses.Errors(process)));
        _ = Task.Run(() => host.WatchAsync(host.watching.Token));
        return host;
    }

    private static HostReady ReadReady(string line)
    {
        HostReply reply = HostReply.Read(line) ?? throw new ProjectHostFailed("The JVM project host's first line does not read.");
        if (reply.Error is { } error)
        {
            throw new ProjectHostFailed($"The JVM project host would not start: {error}.");
        }
        HostReady ready = reply.Result?.Deserialize<HostReady>(HostProtocol.Json)
            ?? throw new ProjectHostFailed("The JVM project host did not say which JVM it runs on.");
        return ready.Protocol == HostProtocol.Version
            ? ready
            : throw new ProjectHostFailed($"The JVM project host speaks protocol {ready.Protocol}, and Wayfarer speaks {HostProtocol.Version}.");
    }

    /// <summary>Sends a request and waits for its result, handing each partial answer to <paramref name="partial"/> on the
    /// reader's thread. Cancelling sends the host <c>cancel</c> and ends the wait as cancelled.</summary>
    public async Task<JsonElement> RequestAsync(string method, object? parameters, Action<JsonElement>? partial = null, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        int id = Interlocked.Increment(ref nextId);
        Pending waiting = new(partial);
        pending[id] = waiting;
        if (gone)
        {
            pending.TryRemove(id, out _);
            throw new ProjectHostFailed(EndedReason(), hostEnded: true);
        }
        Send(new HostRequest(id, method, parameters ?? new { }));
        await using CancellationTokenRegistration registration = cancel.Register(() =>
        {
            TrySend(new HostRequest(Interlocked.Increment(ref nextId), HostProtocol.Cancel, new HostCancelParams(id)));
            waiting.Answer.TrySetCanceled(cancel);
        });
        try
        {
            return await waiting.Answer.Task.ConfigureAwait(false);
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    private void Send(HostRequest request)
    {
        string line = JsonSerializer.Serialize(request, HostProtocol.Json);
        lock (writeGate)
        {
            try
            {
                input.WriteLine(line);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
            {
                throw new ProjectHostFailed(EndedReason(), error, hostEnded: true);
            }
        }
    }

    private void TrySend(HostRequest request)
    {
        try
        {
            Send(request);
        }
        catch (ProjectHostFailed)
        {
            // Gone already: every request waiting fails as at its end.
        }
    }

    private async Task ReadAsync(TextReader output)
    {
        try
        {
            while (await output.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                Volatile.Write(ref lastHeard, Stopwatch.GetTimestamp());
                if (HostReply.Read(line) is not { } reply)
                {
                    ErrorLine?.Invoke(ProcessId, $"a line that does not read: {line}");
                    continue;
                }
                if (!pending.TryGetValue(reply.Id, out Pending? waiting))
                {
                    continue;
                }
                if (reply.Partial is { } value)
                {
                    try
                    {
                        waiting.Partial?.Invoke(value);
                    }
                    catch (Exception error)
                    {
                        waiting.Answer.TrySetException(error);
                    }
                }
                else if (reply.Error is { } error)
                {
                    if (error == "cancelled")
                    {
                        waiting.Answer.TrySetCanceled();
                    }
                    else
                    {
                        waiting.Answer.TrySetException(new ProjectHostFailed(error));
                    }
                }
                else
                {
                    waiting.Answer.TrySetResult(reply.Result ?? default);
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Its output closed with it.
        }
        await WaitForExitAsync().ConfigureAwait(false);
        Fail(EndedReason(), hostEnded: true);
    }

    private async Task ReadErrorsAsync(TextReader errors)
    {
        try
        {
            while (await errors.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }
                lock (recentErrors)
                {
                    recentErrors.Enqueue(line);
                    if (recentErrors.Count > 20)
                    {
                        recentErrors.Dequeue();
                    }
                    if (crashed is null && line.StartsWith("Unhandled exception", StringComparison.Ordinal))
                    {
                        crashed = line.Length > 300 ? line[..300] + "…" : line;
                    }
                }
                ErrorLine?.Invoke(ProcessId, line);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Closed with the process.
        }
    }

    /// <summary>Pings the host while a request waits; one silent for <see cref="ProjectHostOptions.MissedPings"/> intervals
    /// has hung, and is ended.</summary>
    private async Task WatchAsync(CancellationToken stop)
    {
        using PeriodicTimer timer = new(options.PingInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                if (gone)
                {
                    return;
                }
                if (pending.IsEmpty)
                {
                    Volatile.Write(ref lastHeard, Stopwatch.GetTimestamp());
                    continue;
                }
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref lastHeard)) > options.PingInterval * options.MissedPings)
                {
                    End($"it stopped answering for {(options.PingInterval * options.MissedPings).TotalSeconds:0} seconds");
                    return;
                }
                int id = Interlocked.Increment(ref nextId);
                TrySend(new HostRequest(id, HostProtocol.Ping, new { }));
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    /// <summary>Ends the host now: every request waiting fails as at the host's own end, so a host started anew may
    /// answer it. Only the host's own process ends; the Gradle daemon and anything else it talked to stay.</summary>
    public void End(string why)
    {
        endedBecause = why;
        Fail($"The JVM project host was ended: {why}.", hostEnded: true);
        End(process);
    }

    private void Fail(string why, bool hostEnded)
    {
        gone = true;
        watching.Cancel();
        foreach (KeyValuePair<int, Pending> waiting in pending)
        {
            waiting.Value.Answer.TrySetException(new ProjectHostFailed(why, hostEnded: hostEnded));
        }
    }

    private string EndedReason()
    {
        if (endedBecause is { } because)
        {
            return $"The JVM project host was ended: {because}.";
        }
        string how = Exited(process);
        string? said;
        lock (recentErrors)
        {
            said = crashed ?? recentErrors.LastOrDefault();
        }
        return said is null ? $"The JVM project host ended ({how})." : $"The JVM project host ended ({how}): {said}";
    }

    private async Task WaitForExitAsync()
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException)
        {
            // Still going, or not ours to wait for; its reason says so.
        }
    }

    private static string Exited(Process process)
    {
        try
        {
            return process.HasExited ? $"exit code {process.ExitCode}" : "still running";
        }
        catch (InvalidOperationException)
        {
            return "gone";
        }
    }

    private static bool HasEnded(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Kills the host's process alone. Never its tree: the Gradle daemon the Tooling API started is shared with
    /// the developer's own builds and outlives the host.</summary>
    private static void End(Process process)
    {
        ChildProcesses.Ending(process);
        try
        {
            process.Kill(entireProcessTree: false);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Ended already.
        }
    }

    /// <summary>Closes the host's input, which ends it, and ends it outright if it has not gone a moment later.</summary>
    public async ValueTask DisposeAsync()
    {
        Fail("The JVM project host has ended.", hostEnded: false);
        ChildProcesses.CloseInput(process);
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException)
        {
            End(process);
        }
        process.Dispose();
        watching.Dispose();
    }

    private sealed class Pending(Action<JsonElement>? partial)
    {
        public Action<JsonElement>? Partial { get; } = partial;

        public TaskCompletionSource<JsonElement> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>One reply line: its request's id, and its partial answer, result or error.</summary>
public sealed record HostReply(int Id, JsonElement? Partial, JsonElement? Result, string? Error)
{
    public static HostReply? Read(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out JsonElement id) || !id.TryGetInt32(out int number))
            {
                return null;
            }
            JsonElement? partial = root.TryGetProperty("partial", out JsonElement p) ? p.Clone() : null;
            JsonElement? result = root.TryGetProperty("result", out JsonElement r) ? r.Clone() : null;
            string? error = root.TryGetProperty("error", out JsonElement e) ? e.GetString() ?? "failed" : null;
            return partial is null && result is null && error is null ? null : new HostReply(number, partial, result, error);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The process's JVM project hosts, one per tooling JVM, shared by every window as Passenger's project hosts are shared
/// per SDK. A host serves folders: a window that lets its folder go releases it, and a host that serves none ends. A
/// host that ended is started again when next asked for. Trust is checked before any host starts.
/// </summary>
public static class ProjectHosts
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly List<JvmProjectHost> Running = [];

    /// <summary>Options for hosts started from now on; tests shorten the limits.</summary>
    public static ProjectHostOptions Options { get; set; } = ProjectHostOptions.Default;

    /// <summary>Finds the tooling JVM a host runs on; tests replace it.</summary>
    public static Func<ToolingJvm> FindJvm { get; set; } = () => ToolingJvms.Find();

    /// <summary>The host for a folder: one running on the tooling JVM already, else one started. A folder not trusted
    /// is refused (<see cref="UntrustedFolderException"/>) before anything starts.</summary>
    public static async Task<JvmProjectHost> ForAsync(string folder, CancellationToken cancel = default)
    {
        WorkspaceTrust.Check(folder);
        ToolingJvm jvm;
        try
        {
            jvm = FindJvm();
        }
        catch (ToolingJvmMissingException missing)
        {
            throw new ProjectHostFailed(missing.Message, missing);
        }
        await Gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            Running.RemoveAll(host => host.HasExited);
            JvmProjectHost? host = Running.FirstOrDefault(running => PathNames.Same(running.Jvm.Home, jvm.Home));
            if (host is null)
            {
                host = await JvmProjectHost.StartAsync(jvm, Options, cancel).ConfigureAwait(false);
                Running.Add(host);
            }
            host.Served.Add(folder);
            return host;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Lets a folder go: a host that then serves no folder ends.</summary>
    public static async Task ReleaseAsync(string folder)
    {
        List<JvmProjectHost> ending = [];
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (JvmProjectHost host in Running)
            {
                host.Served.Remove(folder);
                if (host.Served.Count == 0)
                {
                    ending.Add(host);
                }
            }
            Running.RemoveAll(ending.Contains);
        }
        finally
        {
            Gate.Release();
        }
        foreach (JvmProjectHost host in ending)
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Ends the hosts serving a folder or one under it (one whose trust was taken back): what each was asked
    /// fails as at a host's own end, so another window's asks go to a host started anew.</summary>
    public static async Task EndServingAsync(string folder, string why)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (JvmProjectHost host in Running.Where(h => h.Served.Any(served => PathNames.Within(served, folder))).ToList())
            {
                Running.Remove(host);
                host.End(why);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The hosts running now, for tests and the IDE's log.</summary>
    public static IReadOnlyList<JvmProjectHost> Current
    {
        get
        {
            Gate.Wait();
            try
            {
                return [.. Running.Where(host => !host.HasExited)];
            }
            finally
            {
                Gate.Release();
            }
        }
    }
}

/// <summary>The <see cref="IWorkspaceImporter"/> that asks the JVM project host: trust first, then the host for the
/// folder's tooling JVM, then one <c>import</c> whose partial answers report progress.</summary>
public sealed class ProjectHostImporter : IWorkspaceImporter
{
    /// <summary>Modules evaluated so far, raised on the host's reader thread.</summary>
    public event Action<ImportProgress>? Progress;

    public async Task<WorkspaceSnapshot> ImportAsync(string workspaceRoot, long generation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        string root = Path.GetFullPath(workspaceRoot);
        JvmProjectHost host = await ProjectHosts.ForAsync(root, cancellationToken).ConfigureAwait(false);
        int evaluated = 0;
        HostImportParams parameters = new(root, generation, ProjectJdk.Recorded(root), ProjectJdk.MavenProfiles(root), null);
        JsonElement answer = await host.RequestAsync(HostProtocol.Import, parameters, partial =>
        {
            string? module = partial.Deserialize<HostTargetEvaluated>(HostProtocol.Json)?.Target.Id;
            Progress?.Invoke(new ImportProgress(generation, Interlocked.Increment(ref evaluated), module));
        }, cancellationToken).ConfigureAwait(false);
        HostWorkspaceResult result = answer.Deserialize<HostWorkspaceResult>(HostProtocol.Json)
            ?? throw new ProjectHostFailed("The JVM project host answered the import with nothing.");
        return HostModel.ToSnapshot(result, host.Ready.Java);
    }
}
