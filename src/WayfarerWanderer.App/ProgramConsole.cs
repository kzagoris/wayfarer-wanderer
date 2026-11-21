using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Passenger.Core;
using Passenger.Terminal;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>The console Run and Debug show a program in, as IntelliJ's Console tab: the program runs in a pseudo-terminal,
/// so what it prints shows as it comes and what is typed here is its standard input (a <c>Scanner</c> reads it). The last
/// program's output stays until the next starts, ended with its exit code.</summary>
public sealed class ProgramConsole : DockPanel, IJvmProgramHost
{
    private readonly TerminalView view = new();
    private TerminalSession? session;

    public ProgramConsole()
    {
        Children.Add(view);
        Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Terminal_Background));
    }

    public TerminalView View => view;

    /// <summary>Raised on the window's thread as a program starts in the console.</summary>
    public event EventHandler? Started;

    /// <summary>Whether a program runs in the console now.</summary>
    public bool IsRunning => session?.IsRunning == true;

    /// <summary>Everything the console shows, its scrollback first, each line's trailing blanks dropped.</summary>
    public string Text
    {
        get
        {
            if (session is not { } shown) return string.Empty;
            StringBuilder text = new();
            foreach (TerminalLine line in shown.Screen.Scrollback) text.Append(line.Text().TrimEnd()).Append('\n');
            foreach (TerminalLine line in shown.Screen.Lines) text.Append(line.Text().TrimEnd()).Append('\n');
            return text.ToString().TrimEnd('\n');
        }
    }

    /// <summary>Clears the console of the last program's output, ending it if it still runs.</summary>
    public void Reset()
    {
        TerminalSession? old = session;
        session = null;
        view.Session = null;
        if (old is not null) IdeErrors.Observe(old.DisposeAsync().AsTask());
    }

    public Task<IJvmProgram> StartAsync(ProcessStartInfo start, CancellationToken cancellationToken = default) =>
        UiThread.InvokeAsync(() => StartHereAsync(start, cancellationToken));

    private async Task<IJvmProgram> StartHereAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int columns = view.Session?.Screen.Columns ?? 120, rows = view.Session?.Screen.Rows ?? 30;
        Reset();
        TerminalSession next = new(columns, rows);
        session = next;
        view.Session = next;
        ConsoleProgram program = new(next, start);
        next.Exited += (_, code) => program.End(code);
        string folder = string.IsNullOrEmpty(start.WorkingDirectory) ? Environment.CurrentDirectory : start.WorkingDirectory;
        try
        {
            await next.StartAsync(folder, start.FileName, [.. start.ArgumentList], Variables(start));
        }
        catch (Exception failed) when (failed is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            next.Say($"Cannot start {Path.GetFileName(start.FileName)}: {failed.Message}\r\n");
            throw new InvalidOperationException($"Cannot start {Path.GetFileName(start.FileName)}: {failed.Message}", failed);
        }
        if (next.ProcessId is int id)
        {
            program.Adopted(id);
        }
        Started?.Invoke(this, EventArgs.Empty);
        return program;
    }

    /// <summary>The variables the program's start sets over the IDE's own, and those it takes away (empty).</summary>
    private static Dictionary<string, string> Variables(ProcessStartInfo start)
    {
        StringComparer names = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        Dictionary<string, string> given = new(names);
        foreach ((string name, string? value) in start.Environment)
        {
            if (Environment.GetEnvironmentVariable(name) != value) given[name] = value ?? string.Empty;
        }
        foreach (System.Collections.DictionaryEntry inherited in Environment.GetEnvironmentVariables())
        {
            if (inherited.Key is string name && !start.Environment.ContainsKey(name)) given[name] = string.Empty;
        }
        return given;
    }

    private sealed class ConsoleProgram(TerminalSession session, ProcessStartInfo start) : IJvmProgram
    {
        private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stopwatch time = Stopwatch.StartNew();
        private int? id;

        public Task<int> Exited => exited.Task;

        public void Adopted(int processId)
        {
            id = processId;
            ChildProcesses.Adopt(processId, start);
        }

        /// <summary>The program's end, told once on the window's thread, after its last output.</summary>
        public void End(int code)
        {
            if (!exited.TrySetResult(code)) return;
            session.Say($"\x1b[0m\r\nProcess finished with exit code {code}\r\n");
            if (id is int processId) ChildProcesses.Exited(processId, start, code, time.Elapsed);
        }

        public void Stop() => UiThread.Post(() =>
        {
            if (exited.Task.IsCompleted) return;
            Task ending = session.DisposeAsync().AsTask();
            IdeErrors.Observe(ending.ContinueWith(_ => UiThread.Post(() => End(OperatingSystem.IsWindows() ? -1 : 130)),
                TaskScheduler.Default));
        });

        public async ValueTask DisposeAsync()
        {
            Stop();
            await exited.Task.ConfigureAwait(false);
        }
    }
}
