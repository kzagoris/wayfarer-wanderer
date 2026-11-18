using System.Diagnostics;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

public enum ProcessOutputStream
{
    StandardOutput,
    StandardError,
}

public sealed record ProcessOutput(ProcessOutputStream Stream, string Line);

public sealed record ProcessResult(int ExitCode, IReadOnlyList<ProcessOutput> Output, TimeSpan Elapsed);

public interface IChildProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessStartInfo start,
        Action<ProcessOutput>? output = null,
        CancellationToken cancellationToken = default);
}

public sealed class ChildProcessRunner : IChildProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        ProcessStartInfo start,
        Action<ProcessOutput>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        cancellationToken.ThrowIfCancellationRequested();
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        List<ProcessOutput> outputLines = [];
        object gate = new();
        Action<string> standardOutput = line => Publish(
            new ProcessOutput(ProcessOutputStream.StandardOutput, line),
            outputLines,
            gate,
            output);
        Action<string> standardError = line => Publish(
            new ProcessOutput(ProcessOutputStream.StandardError, line),
            outputLines,
            gate,
            output);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using Process process = await ChildProcesses.StartAsync(start).ConfigureAwait(false);
        ChildProcesses.HoldFamily(process);
        Task heard = ChildProcesses.Hear(process, standardOutput, standardError);
        Task exited = process.WaitForExitAsync();
        try
        {
            await Task.WhenAll(heard, exited).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ChildProcesses.Kill(process);
            await CleanupAsync(process, heard).ConfigureAwait(false);
            throw;
        }
        catch
        {
            ChildProcesses.Kill(process);
            await CleanupAsync(process, heard).ConfigureAwait(false);
            throw;
        }
        stopwatch.Stop();
        ProcessOutput[] collected;
        lock (gate)
        {
            collected = [.. outputLines];
        }
        return new ProcessResult(process.ExitCode, collected, stopwatch.Elapsed);
    }

    private static void Publish(
        ProcessOutput item,
        List<ProcessOutput> destination,
        object gate,
        Action<ProcessOutput>? output)
    {
        lock (gate)
        {
            destination.Add(item);
        }
        output?.Invoke(item);
    }

    private static async Task CleanupAsync(Process process, Task heard)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            await heard.ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
    }
}
