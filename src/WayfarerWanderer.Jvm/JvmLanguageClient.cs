using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

/// <summary>One project window's stdio conversation with one language server.</summary>
public sealed class JvmLanguageClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly Stream input;
    private readonly Stream output;
    private readonly string state;
    private readonly object[] folders;
    private readonly SemaphoreSlim writes = new(1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly Task reader;
    private readonly Task errors;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WorkspaceSnapshot? snapshot;
    private string lastError = "";
    private int nextId;

    private readonly string language;
    private string[] bundles = [];

    private JvmLanguageClient(Process process, string state, string root, string language, WorkspaceSnapshot? snapshot)
    {
        this.process = process;
        this.language = language;
        this.state = state;
        this.snapshot = snapshot;
        folders = [new { uri = new Uri(root).AbsoluteUri, name = Path.GetFileName(root) }];
        input = process.StandardInput.BaseStream;
        output = new BufferedStream(process.StandardOutput.BaseStream);
        // Servers that never wait for readiness must not leave its failure unobserved.
        ready.Task.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        errors = ReadErrorsAsync();
        reader = ReadAsync();
    }

    public static async Task<JvmLanguageClient> StartAsync(string root, string language, CancellationToken cancel, WorkspaceSnapshot? snapshot = null)
    {
        WorkspaceTrust.Check(root);
        cancel.ThrowIfCancellationRequested();
        string state = Directory.CreateTempSubdirectory("wayfarer-lsp-").FullName;
        JvmLanguageClient? client = null;
        try
        {
            ProcessStartInfo start = JvmLanguageServers.Command(language, root, state);
            // JDT LS loads Wayfarer's extension for Kotlin class folders. Without it Java still works; Kotlin stays invisible on Maven.
            string[] bundles = [];
            string? bundleProblem = null;
            if (language == "java")
            {
                try { bundles = [await JdtlsKotlinBundle.EnsureAsync(JvmLanguageServers.JdtHome(), cancel).ConfigureAwait(false)]; }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    bundleProblem = "Java cannot see Kotlin classes on Maven: " + error.Message;
                }
            }
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            Process process = await ChildProcesses.StartAsync(start).ConfigureAwait(false);
            ChildProcesses.HoldFamily(process);
            client = new(process, state, root, language, snapshot) { BundleProblem = bundleProblem, bundles = bundles };
            object initializationOptions = InitializationOptions(bundles, snapshot);
            await client.RequestAsync("initialize", new
            {
                processId = Environment.ProcessId, rootUri = new Uri(root).AbsoluteUri, rootPath = root,
                workspaceFolders = client.folders,
                initializationOptions,
                capabilities = new { workspace = new { configuration = true, workspaceFolders = true, workspaceEdit = new { documentChanges = true } },
                    textDocument = new { completion = new { completionItem = new { snippetSupport = false } },
                        documentSymbol = new { hierarchicalDocumentSymbolSupport = true } } }
            }, cancel).ConfigureAwait(false);
            await client.NotifyAsync("initialized", new { }, cancel).ConfigureAwait(false);
            return client;
        }
        catch
        {
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            else Directory.Delete(state, true);
            throw;
        }
    }

    public bool HasExited => reader.IsCompleted;

    /// <summary>Why the JDT LS Kotlin class extension is unavailable, or null.</summary>
    public string? BundleProblem { get; private init; }

    /// <summary>Completes when the server reports that its import finished.</summary>
    public Task WaitUntilReadyAsync(CancellationToken cancel) => ready.Task.WaitAsync(TimeSpan.FromMinutes(5), cancel);

    private static object InitializationOptions(string[] bundles, WorkspaceSnapshot? snapshot) => snapshot switch
    {
        { BuildSystem: JvmBuildSystem.Idea } => new { bundles, settings = IdeaLanguageSettings.Document(snapshot) },
        not null => new { bundles, settings = BuildLanguageSettings.Document(snapshot) },
        null => new { bundles },
    };

    /// <summary>
    /// Tells the server the build model changed after a reimport: the settings the snapshot decides (JDKs, or an IntelliJ
    /// module's roots and libraries), then each build file the evaluation read as changed. JDT LS reimports the projects
    /// of the build files named (<c>java/projectConfigurationsUpdate</c>). The Kotlin server watches no build file
    /// itself: it imports again on <c>intellij/reloadWorkspace</c>, as JetBrains' own editor extension asks when a build
    /// file is saved, and the request ends once the new workspace is loaded.
    /// </summary>
    public async Task ReloadModelAsync(WorkspaceSnapshot next, CancellationToken cancel)
    {
        snapshot = next;
        if (language == "kotlin")
        {
            await RequestAsync("intellij/reloadWorkspace", new { initializationOptions = InitializationOptions(bundles, next) }, cancel).ConfigureAwait(false);
            return;
        }
        object settings = next.BuildSystem == JvmBuildSystem.Idea ? IdeaLanguageSettings.Document(next) : BuildLanguageSettings.Document(next);
        await NotifyAsync("workspace/didChangeConfiguration", new { settings }, cancel).ConfigureAwait(false);
        object[] changes = [.. BuildFiles(next).Select(path => new { uri = new Uri(path).AbsoluteUri, type = 2 })];
        if (changes.Length == 0)
        {
            return;
        }
        await NotifyAsync("workspace/didChangeWatchedFiles", new { changes }, cancel).ConfigureAwait(false);
        if (language == "java" && next.BuildSystem != JvmBuildSystem.Idea)
        {
            object[] identifiers = [.. BuildFiles(next).Select(path => new { uri = new Uri(path).AbsoluteUri })];
            await NotifyAsync("java/projectConfigurationsUpdate", new { identifiers }, cancel).ConfigureAwait(false);
        }
    }

    /// <summary>The build files among what the evaluation read that exist: poms and Gradle scripts, the wrapper and
    /// property files left out, since neither server imports from them.</summary>
    private static IEnumerable<string> BuildFiles(WorkspaceSnapshot snapshot) =>
        snapshot.BuildInputs.Select(input => input.Path)
            .Where(path => Path.GetFileName(path) is "pom.xml" || path.EndsWith(".gradle", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".gradle.kts", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .Distinct(PathNames.Comparer);

    public Task NotifyAsync(string method, object parameters, CancellationToken cancel) =>
        SendAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancel);

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancel)
    {
        int id = Interlocked.Increment(ref nextId);
        TaskCompletionSource<JsonElement> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = answer;
        try
        {
            if (HasExited) throw new IOException("Language server exited. " + lastError);
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancel).ConfigureAwait(false);
            return await answer.Task.WaitAsync(TimeSpan.FromMinutes(3), cancel).ConfigureAwait(false);
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task SendAsync(object message, CancellationToken cancel)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message);
        byte[] header = Encoding.ASCII.GetBytes(FormattableString.Invariant($"Content-Length: {body.Length}\r\n\r\n"));
        await writes.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await input.WriteAsync(header).ConfigureAwait(false);
            await input.WriteAsync(body).ConfigureAwait(false);
            await input.FlushAsync().ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }

    private async Task ReadErrorsAsync()
    {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            if (!string.IsNullOrWhiteSpace(line)) lastError = line;
    }

    private async Task ReadAsync()
    {
        try
        {
            byte[] single = new byte[1];
            while (true)
            {
                StringBuilder headers = new();
                int tail = 0;
                while (tail != 0x0d0a0d0a)
                {
                    await output.ReadExactlyAsync(single).ConfigureAwait(false);
                    headers.Append((char)single[0]);
                    if (headers.Length > 8192) throw new InvalidDataException("Invalid LSP headers.");
                    tail = (tail << 8) | single[0];
                }
                string length = headers.ToString().Split("\r\n").Single(s => s.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))[15..];
                int count = int.Parse(length, CultureInfo.InvariantCulture);
                if (count is < 0 or > 64 * 1024 * 1024) throw new InvalidDataException("Invalid LSP message length.");
                byte[] body = new byte[count];
                await output.ReadExactlyAsync(body).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement message = doc.RootElement;
                if (!message.TryGetProperty("id", out var id))
                {
                    if (message.TryGetProperty("method", out var notice) && notice.GetString() == "language/status" &&
                        message.GetProperty("params").TryGetProperty("type", out var status) && status.GetString() is "ServiceReady" or "Started")
                        ready.TrySetResult();
                    continue;
                }
                if (message.TryGetProperty("method", out var method))
                {
                    await RespondAsync(id, method.GetString(), message).ConfigureAwait(false);
                    continue;
                }
                if (!id.TryGetInt32(out int number) || !pending.TryRemove(number, out var answer)) continue;
                if (message.TryGetProperty("error", out var error)) answer.TrySetException(new InvalidOperationException(error.ToString()));
                else answer.TrySetResult(message.GetProperty("result").Clone());
            }
        }
        catch (Exception error)
        {
            ready.TrySetException(new IOException("Language server connection ended. " + lastError, error));
            foreach (var answer in pending.Values)
                answer.TrySetException(new IOException("Language server connection ended. " + lastError, error));
        }
    }

    private Task RespondAsync(JsonElement id, string? method, JsonElement message)
    {
        object? result;
        switch (method)
        {
            case "workspace/configuration":
                result = message.GetProperty("params").GetProperty("items").EnumerateArray().Select(item =>
                {
                    string? section = item.TryGetProperty("section", out JsonElement name) ? name.GetString() : null;
                    return snapshot switch
                    {
                        { BuildSystem: JvmBuildSystem.Idea } => IdeaLanguageSettings.Value(snapshot, section),
                        not null => BuildLanguageSettings.Value(snapshot, section),
                        null => null,
                    };
                }).ToArray(); break;
            case "workspace/workspaceFolders": result = folders; break;
            case "workspace/applyEdit": result = new { applied = false, failureReason = "Edits require a reviewed preview." }; break;
            case "client/registerCapability": case "client/unregisterCapability": case "window/workDoneProgress/create":
            case "workspace/semanticTokens/refresh": case "workspace/diagnostic/refresh": result = null; break;
            default: return SendAsync(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Unsupported client method: " + method } }, default);
        }
        return SendAsync(new { jsonrpc = "2.0", id, result }, default);
    }

    public async ValueTask DisposeAsync()
    {
        ChildProcesses.Kill(process);
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(errors, reader).ConfigureAwait(false);
        process.Dispose();
        try { Directory.Delete(state, true); } catch (IOException) { }
    }
}

