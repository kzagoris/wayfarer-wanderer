using System.Diagnostics;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

/// <summary>A program Run or Debug started: its end, with its exit code, and a way to end it first.</summary>
public interface IJvmProgram : IAsyncDisposable
{
    /// <summary>Completes with the exit code once the program has ended and its last output is shown.</summary>
    Task<int> Exited { get; }

    /// <summary>Ends the program and all it started.</summary>
    void Stop();
}

/// <summary>Where a program Run or Debug starts runs: the IDE's console (a pseudo-terminal the person types into), or
/// pipes whose lines go to a callback.</summary>
public interface IJvmProgramHost
{
    Task<IJvmProgram> StartAsync(ProcessStartInfo start, CancellationToken cancellationToken = default);
}

/// <summary>Runs a program with its output read line by line and its input closed: for headless runs and tests.</summary>
public sealed class PipedProgramHost(Action<string> output) : IJvmProgramHost
{
    public async Task<IJvmProgram> StartAsync(ProcessStartInfo start, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        Process process = await ChildProcesses.StartAsync(start).ConfigureAwait(false);
        ChildProcesses.HoldFamily(process);
        Task heard = ChildProcesses.Hear(process, output, output);
        return new Piped(process, heard);
    }

    private sealed class Piped : IJvmProgram
    {
        private readonly Process process;
        private readonly Task heard;

        public Piped(Process process, Task heard)
        {
            this.process = process;
            this.heard = heard;
            Exited = WaitAsync();
        }

        public Task<int> Exited { get; }

        private async Task<int> WaitAsync()
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            try { await heard.ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or InvalidOperationException) { }
            return process.ExitCode;
        }

        public void Stop() => ChildProcesses.Kill(process);

        public async ValueTask DisposeAsync()
        {
            Stop();
            try { await Exited.ConfigureAwait(false); }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
    }
}
