using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Passenger.Core;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

[Collection(JvmTrustCollection.Name)]
public sealed class JvmDebugTests
{
    public static bool RunDebugAcceptance => Environment.GetEnvironmentVariable("WAYFARER_DEBUG_ACCEPTANCE") == "1";

    [Theory(SkipUnless = nameof(RunDebugAcceptance), Skip = "Set WAYFARER_DEBUG_ACCEPTANCE=1 to run the Boot wrappers and JVMs.")]
    [InlineData("gradle", "java", "Greeting.java", 5)]
    [InlineData("maven", "kotlin", "Greeting.kt", 3)]
    public async Task DebugHttpHandlerAndRestartOnTheSamePort(string buildTool, string language, string filename, int line)
    {
        string fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../tests/fixtures/jvm", buildTool));
        string root = Directory.CreateTempSubdirectory("wayfarer boot debug ").FullName;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(6));
        CancellationToken cancel = deadline.Token;
        try
        {
            CopyFixture(fixture, root);
            WorkspaceTrust.Trust(root);
            var snapshot = await new ProjectHostImporter().ImportAsync(root, 1, cancel);
            var config = new JvmLaunchConfiguration { Module = "service", MainClass = "example.Service", Arguments = ["--server.port=0"] };
            string handler = Path.Combine(root, "service", "src", "main", "java", "example", "Service.java");
            string callee = Path.Combine(root, "library", "src", "main", language, "example", filename);
            await using var debug = new JvmInteractiveDebugger();
            var stops = Channel.CreateUnbounded<JvmDebugPause>();
            debug.Paused += pause => stops.Writer.TryWrite(pause);
            Task<int> boundPort = BoundPortAsync(debug);
            await debug.StartAsync(snapshot, config, new Dictionary<string, int[]>(), cancel);
            int port = await boundPort.WaitAsync(cancel);
            await debug.SetBreakpointsAsync(handler, [18]);
            using var http = new HttpClient();
            string endpoint = $"http://127.0.0.1:{port}/hello?name=Wayfarer";
            Task<string> request = http.GetStringAsync(endpoint, cancel);
            var paused = await stops.Reader.ReadAsync(cancel);
            Assert.Equal((handler, 18), (paused.Frames[0].Path, paused.Frames[0].Line));
            Assert.Contains("name = \"Wayfarer\"", paused.Locals);

            await debug.StepIntoAsync();
            paused = await stops.Reader.ReadAsync(cancel);
            Assert.Equal((callee, line), (paused.Frames[0].Path, paused.Frames[0].Line));
            Assert.Contains(paused.Frames, frame => frame.Path == handler && frame.Line == 18);
            Assert.Contains("name = \"Wayfarer\"", paused.Locals);
            await debug.ContinueAsync();
            Assert.Equal("Hello, Wayfarer", await request);
            await debug.StopAsync();
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync(endpoint, cancel));

            await using var restart = new JvmInteractiveDebugger();
            Task<int> reboundPort = BoundPortAsync(restart);
            await restart.StartAsync(snapshot, config with { Arguments = [$"--server.port={port}"] }, new Dictionary<string, int[]>(), cancel);
            Assert.Equal(port, await reboundPort.WaitAsync(cancel));
            Assert.Equal("Hello, Wayfarer", await http.GetStringAsync(endpoint, cancel));
            await restart.StopAsync();
        }
        finally
        {
            WorkspaceTrust.TakeBack(root);
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<int> BoundPortAsync(JvmInteractiveDebugger debug)
    {
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Output += line =>
        {
            var match = Regex.Match(line, @"Tomcat started on port (\d+)");
            if (match.Success) result.TrySetResult(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        };
        debug.Ended += () => result.TrySetException(new InvalidOperationException("The Boot process ended before binding its port."));
        return result.Task;
    }

    private static void CopyFixture(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from))
        {
            string copy = Path.Combine(to, Path.GetFileName(file));
            File.Copy(file, copy);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(copy, File.GetUnixFileMode(file));
        }
        foreach (string folder in Directory.EnumerateDirectories(from))
        {
            string name = Path.GetFileName(folder);
            if (name is "build" or "target" or ".gradle" or ".kotlin") continue;
            string copy = Directory.CreateDirectory(Path.Combine(to, name)).FullName;
            CopyFixture(folder, copy);
        }
    }
}
