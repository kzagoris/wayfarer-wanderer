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
    private string lastError = "";
    private int nextId;

    private JvmLanguageClient(Process process, string state, string root)
    {
        this.process = process;
        this.state = state;
        folders = [new { uri = new Uri(root).AbsoluteUri, name = Path.GetFileName(root) }];
        input = process.StandardInput.BaseStream;
        output = new BufferedStream(process.StandardOutput.BaseStream);
        errors = ReadErrorsAsync();
        reader = ReadAsync();
    }

    public static async Task<JvmLanguageClient> StartAsync(string root, string language, CancellationToken cancel)
    {
        WorkspaceTrust.Check(root);
        cancel.ThrowIfCancellationRequested();
        string state = Directory.CreateTempSubdirectory("wayfarer-lsp-").FullName;
        JvmLanguageClient? client = null;
        try
        {
            ProcessStartInfo start = JvmLanguageServers.Command(language, root, state);
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            Process process = await ChildProcesses.StartAsync(start).ConfigureAwait(false);
            ChildProcesses.HoldFamily(process);
            client = new(process, state, root);
            await client.RequestAsync("initialize", new
            {
                processId = Environment.ProcessId, rootUri = new Uri(root).AbsoluteUri, rootPath = root,
                workspaceFolders = client.folders,
                capabilities = new { workspace = new { configuration = true, workspaceFolders = true, workspaceEdit = new { documentChanges = true } },
                    textDocument = new { completion = new { completionItem = new { snippetSupport = false } } } }
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
                if (!message.TryGetProperty("id", out var id)) continue;
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
                result = message.GetProperty("params").GetProperty("items").EnumerateArray().Select(_ => (object?)null).ToArray(); break;
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

