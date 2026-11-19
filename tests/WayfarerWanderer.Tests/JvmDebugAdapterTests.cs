using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;
using Passenger.Dap;

namespace WayfarerWanderer.Tests;

/// <summary>The debuggee never listens for JDWP. The adapter listens and accepts one JVM, then stops listening.</summary>
public sealed class JvmDebugAdapterTests
{
    private const string Program = """
        import java.nio.file.*;
        class Target {
            public static void main(String[] args) throws Exception { Files.writeString(Path.of(args[0]), "ran"); }
        }
        """;

    [Fact]
    public async Task AJvmThatConnectsSecondIsRefusedAndNeverRuns()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string folder = Directory.CreateTempSubdirectory("wayfarer jdwp ").FullName;
        try
        {
            string program = Path.Combine(folder, "Target.java");
            File.WriteAllText(program, Program);
            await using var adapter = await Adapter.StartAsync(cancel);
            int port = await adapter.ListenAsync(cancel);
            using Process first = StartTarget(program, port, Path.Combine(folder, "first"));
            await adapter.AttachAsync(cancel);

            string marker = Path.Combine(folder, "second");
            using Process second = StartTarget(program, port, marker);
            await second.WaitForExitAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.NotEqual(0, second.ExitCode);
            Assert.False(File.Exists(marker));
            first.Kill();
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task TheConnectedTargetStaysSuspendedUntilConfigurationDone()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string folder = Directory.CreateTempSubdirectory("wayfarer jdwp ").FullName;
        try
        {
            string program = Path.Combine(folder, "Target.java");
            File.WriteAllText(program, Program);
            await using var adapter = await Adapter.StartAsync(cancel);
            int port = await adapter.ListenAsync(cancel);
            string marker = Path.Combine(folder, "target");
            using Process target = StartTarget(program, port, marker);
            await adapter.AttachAsync(cancel);
            Assert.False(File.Exists(marker), "The target ran before configurationDone.");
            await adapter.Dap.RequestAsync("configurationDone", new { }, cancel);
            await target.WaitForExitAsync(cancel).WaitAsync(TimeSpan.FromSeconds(60), cancel);
            Assert.Equal(0, target.ExitCode);
            Assert.Equal("ran", File.ReadAllText(marker));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static string Java() => Path.Combine(NewProjectTests.JdkHome(), "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");

    private static Process StartTarget(string program, int port, string marker)
    {
        var start = new ProcessStartInfo(Java())
        {
            ArgumentList = { $"-agentlib:jdwp=transport=dt_socket,server=n,suspend=y,address=127.0.0.1:{port}", program, marker },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Process process = Process.Start(start)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private sealed class Adapter(Process process, DapConnection dap) : IAsyncDisposable
    {
        public DapConnection Dap => dap;

        public static async Task<Adapter> StartAsync(CancellationToken cancel)
        {
            string source = Path.Combine(AppContext.BaseDirectory, "Debug", "WayfarerDebugAdapter.java");
            var start = new ProcessStartInfo(Java())
            {
                ArgumentList = { "--add-modules", "jdk.jdi", source },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            Process process = Process.Start(start)!;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            var dap = new DapConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, _ => { });
            dap.Start();
            await dap.RequestAsync("initialize", new { adapterID = "wayfarer-jdi" }, cancel);
            return new Adapter(process, dap);
        }

        public async Task<int> ListenAsync(CancellationToken cancel) =>
            (await dap.RequestAsync<JsonElement>("listen", new { }, cancel)).GetProperty("port").GetInt32();

        public Task AttachAsync(CancellationToken cancel) =>
            dap.RequestAsync("attach", new { sourcePaths = Array.Empty<string>() }, cancel).WaitAsync(TimeSpan.FromSeconds(60), cancel);

        public async ValueTask DisposeAsync()
        {
            await dap.DisposeAsync();
            ChildProcesses.Kill(process);
            await process.WaitForExitAsync();
            process.Dispose();
        }
    }
}
