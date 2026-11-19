using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Passenger.Core;
using WayfarerWanderer.App;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

[Collection(JvmTrustCollection.Name)]
public sealed class JvmProcessOwnershipTests
{
    public static bool RunDebugAcceptance => Environment.GetEnvironmentVariable("WAYFARER_DEBUG_ACCEPTANCE") == "1";

    private const string GradleDaemon = "org.gradle.launcher.daemon.bootstrap.GradleDaemon";

    [AvaloniaTheory(SkipUnless = nameof(RunDebugAcceptance), Skip = "Set WAYFARER_DEBUG_ACCEPTANCE=1 to run the Boot wrappers and JVMs.")]
    [InlineData("gradle", "stop")]
    [InlineData("gradle", "close")]
    [InlineData("gradle", "switch")]
    [InlineData("maven", "close")]
    [InlineData("maven", "switch")]
    public async Task EndingTheSessionStopsOnlyItsOwnJvms(string buildTool, string ending)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string root = Directory.CreateTempSubdirectory("wayfarer ownership ").FullName;
        string other = Directory.CreateTempSubdirectory("wayfarer-other-").FullName;
        Process? unrelated = null;
        var window = EditorApplication.CreateWindow(null);
        try
        {
            JvmFixtures.Copy(buildTool, root);
            WorkspaceTrust.Trust(root);
            unrelated = await StartUnrelatedJvmAsync(other, cancel);

            window.Show();
            await window.OpenWorkspaceAsync(root, cancel);
            ProjectSession session = window.Session!;
            WorkspaceSnapshot snapshot = session.Snapshot!;
            string jps = Jps(snapshot);
            var before = await JvmsAsync(jps, cancel);
            var config = new JvmLaunchConfiguration { Module = "service", MainClass = "example.Service", Arguments = ["--server.port=0"] };
            Task debugging = session.DebugAsync(config, new Dictionary<string, int[]>());
            int port = await Until(() => BoundPort(window.BuildTool.Output), () => session.IsRunning, "the service to bind its port");
            using var http = new HttpClient();
            string endpoint = $"http://127.0.0.1:{port}/hello?name=Wayfarer";
            Assert.Equal("Hello, Wayfarer", await http.GetStringAsync(endpoint, cancel));

            var running = await JvmsAsync(jps, cancel);
            int[] owned = [.. running.Where(jvm => !before.ContainsKey(jvm.Key) &&
                (jvm.Value.Contains("example.Service", StringComparison.Ordinal) ||
                 jvm.Value.Contains("WayfarerDebugAdapter", StringComparison.Ordinal))).Select(jvm => jvm.Key)];
            Assert.Equal(2, owned.Length);
            int[] daemons = [.. running.Where(jvm => jvm.Value.Contains(GradleDaemon, StringComparison.Ordinal)).Select(jvm => jvm.Key)];
            if (buildTool == "gradle") Assert.NotEmpty(daemons);

            switch (ending)
            {
                case "stop":
                    Assert.True(window.Actions.TryExecute(EditorActions.Stop));
                    break;
                case "close":
                    window.Close();
                    break;
                case "switch":
                    await window.OpenWorkspaceAsync(other, cancel);
                    break;
            }
            await debugging.WaitAsync(TimeSpan.FromMinutes(1), cancel);
            await Until<int>(() => owned.All(Exited) ? 1 : 0, () => true, "the owned service and adapter to exit");
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync(endpoint, cancel));
            Assert.False(unrelated.HasExited, "The unrelated JVM was stopped.");
            Assert.All(daemons, daemon => Assert.False(Exited(daemon), $"Gradle daemon {daemon} was stopped."));

            await using var restart = new JvmInteractiveDebugger();
            var rebound = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            restart.Output += line => { if (BoundPort([line]) is int bound) rebound.TrySetResult(bound); };
            await restart.StartAsync(snapshot, config with { Arguments = [$"--server.port={port}"] }, new Dictionary<string, int[]>(), cancel);
            Assert.Equal(port, await rebound.Task.WaitAsync(TimeSpan.FromMinutes(2), cancel));
            Assert.Equal("Hello, Wayfarer", await http.GetStringAsync(endpoint, cancel));
            await restart.StopAsync();
            Assert.False(unrelated.HasExited, "The unrelated JVM was stopped.");
        }
        finally
        {
            if (window.Session != null) await window.Session.CancelAsync();
            window.Close();
            if (unrelated is { HasExited: false }) unrelated.Kill(entireProcessTree: true);
            unrelated?.Dispose();
            WorkspaceTrust.TakeBack(root);
            Directory.Delete(root, recursive: true);
            Directory.Delete(other, recursive: true);
        }

        async Task<T> Until<T>(Func<T?> value, Func<bool> alive, string what) where T : struct
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromMinutes(5))
            {
                if (value() is T result && !result.Equals(default(T))) return result;
                Assert.True(alive(), $"The session ended while waiting for {what}." + Environment.NewLine +
                    string.Join(Environment.NewLine, window.BuildTool.Output.TakeLast(20)));
                await Task.Delay(50, cancel);
            }
            throw new TimeoutException($"Timed out waiting for {what}.");
        }
    }

    [Theory(SkipUnless = nameof(RunDebugAcceptance), Skip = "Set WAYFARER_DEBUG_ACCEPTANCE=1 to run the wrappers and JVMs.")]
    [InlineData("gradle")]
    [InlineData("maven")]
    public async Task DirectLaunchMatchesTheBuildTask(string buildTool)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string root = Directory.CreateTempSubdirectory("wayfarer launch ").FullName;
        try
        {
            JvmFixtures.Copy(buildTool, root);
            WorkspaceTrust.Trust(root);
            WorkspaceSnapshot snapshot = await new WorkspaceImporter().ImportAsync(root, 1, cancel);
            JvmModule app = snapshot.Module("app")!;
            var config = new JvmLaunchConfiguration { Module = "app", MainClass = app.MainClasses.Single().ClassName };

            string task = buildTool == "maven" ? "exec:java" : "run";
            ProcessResult wrapper = await new JvmLaunch(adapter: new WrapperJvmLaunchAdapter())
                .RunAsync(snapshot, config with { BuildTask = task }, cancellationToken: cancel);
            string wrapperLog = string.Join(Environment.NewLine, wrapper.Output.TakeLast(30).Select(line => line.Line));
            if (buildTool == "maven")
            {
                // The reactor runs exec:java on the aggregator first, which has no classes.
                Assert.True(wrapper.ExitCode != 0, wrapperLog);
                Assert.Contains(wrapper.Output, line => line.Line.Contains("ClassNotFoundException: example.Main", StringComparison.Ordinal));
            }
            else
            {
                Assert.True(wrapper.ExitCode == 0, wrapperLog);
                Assert.Single(wrapper.Output, line => line.Line == "Hello, Wayfarer");
            }

            ProcessStartInfo direct = await DirectJvmLaunchAdapter.ResolvedCommandAsync(snapshot, config, cancel);
            string classpath = direct.ArgumentList[direct.ArgumentList.IndexOf("-cp") + 1];
            Assert.Contains("library", classpath, StringComparison.Ordinal);
            Assert.Contains("kotlin-stdlib", classpath, StringComparison.Ordinal);
            Assert.True(PathNames.Same(app.Root, direct.WorkingDirectory));
            Assert.Equal(WrapperJvmLaunchAdapter.LaunchesDirectly(snapshot, config), buildTool == "maven");

            ProcessResult launched = await new JvmLaunch().RunAsync(snapshot, config, cancellationToken: cancel);
            string[] program = [.. launched.Output.Select(line => line.Line).Where(line => line.Length > 0)];
            Assert.True(launched.ExitCode == 0, string.Join(Environment.NewLine, program.TakeLast(30)));
            if (buildTool == "maven") Assert.Equal(["Hello, Wayfarer"], program);
            else Assert.Single(program, line => line == "Hello, Wayfarer");
            ProcessResult directRun = await new ChildProcessRunner().RunAsync(direct, cancellationToken: cancel);
            Assert.Equal(["Hello, Wayfarer"], directRun.Output.Select(line => line.Line).Where(line => line.Length > 0));
        }
        finally
        {
            WorkspaceTrust.TakeBack(root);
            Directory.Delete(root, recursive: true);
        }
    }

    private static int? BoundPort(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            var match = Regex.Match(line, @"Tomcat started on port (\d+)");
            if (match.Success) return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static async Task<Process> StartUnrelatedJvmAsync(string folder, CancellationToken cancel)
    {
        string source = Path.Combine(folder, "Idle.java");
        await File.WriteAllTextAsync(source,
            "public class Idle { public static void main(String[] a) throws Exception { System.out.println(\"ready\"); Thread.sleep(900_000); } }",
            cancel);
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "java.exe" : "java", [source])
        { RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = folder };
        Process process = Process.Start(start)!;
        Assert.Equal("ready", await process.StandardOutput.ReadLineAsync(cancel));
        return process;
    }

    private static string Jps(WorkspaceSnapshot snapshot)
    {
        string jps = OperatingSystem.IsWindows() ? "jps.exe" : "jps";
        return snapshot.ToolingToolchain.Home is { Length: > 0 } home && File.Exists(Path.Combine(home, "bin", jps))
            ? Path.Combine(home, "bin", jps) : jps;
    }

    private static async Task<Dictionary<int, string>> JvmsAsync(string jps, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(jps, ["-lm"]) { RedirectStandardOutput = true, UseShellExecute = false };
        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync(cancel);
        await process.WaitForExitAsync(cancel);
        Dictionary<int, string> jvms = [];
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0 && int.TryParse(line.AsSpan(0, space), out int pid)) jvms[pid] = line[(space + 1)..];
        }
        return jvms;
    }

    private static bool Exited(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}

internal static class JvmFixtures
{
    public static void Copy(string buildTool, string destination)
    {
        string fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../tests/fixtures/jvm", buildTool));
        foreach (string file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(fixture, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is ".gradle" or ".kotlin" or "build" or "target")) continue;
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
    }
}
