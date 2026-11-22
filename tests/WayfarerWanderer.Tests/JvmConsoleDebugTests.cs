using System.Diagnostics;
using System.Threading.Channels;
using Avalonia.Headless.XUnit;
using Passenger.Core;
using Passenger.Dap;
using WayfarerWanderer.App;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>Debugging and running a plain IntelliJ module: a breakpoint stops with the frame's variables (javac -g),
/// objects and arrays open, expressions evaluate, Pause stops a running program, and a program run in the console reads
/// what is typed there.</summary>
[Collection(JvmTrustCollection.Name)]
public sealed class JvmConsoleDebugTests
{
    private const string Program = """
        package com.example.console;

        import java.util.Scanner;

        public class App {
            private final String greeting = "Hello";

            public static void main(String[] args) throws Exception {
                int answer = 42;
                int[] primes = {2, 3, 5};
                App app = new App();
                System.out.println(app.greet("Wayfarer") + " " + answer + " " + primes.length);
                if (args.length > 0 && args[0].equals("spin")) {
                    while (true) Thread.sleep(50);
                }
                if (args.length > 0 && args[0].equals("ask")) {
                    Scanner in = new Scanner(System.in);
                    System.out.println("Name?");
                    String name = in.nextLine();
                    System.out.println("Typed " + name);
                }
            }

            String greet(String name) {
                return greeting + ", " + name;
            }
        }
        """;

    [Fact]
    public async Task BreakpointShowsVariablesThatOpenAndEvaluate()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await using Module module = await Module.CreateAsync(cancel);
        await using var debug = new JvmInteractiveDebugger();
        var stops = Channel.CreateUnbounded<JvmDebugPause>();
        debug.Paused += pause => stops.Writer.TryWrite(pause);
        await debug.StartAsync(module.Snapshot, module.Launch(), new Dictionary<string, int[]> { [module.Source] = [12] }, cancel);

        JvmDebugPause paused = await NextAsync(stops, cancel);
        Assert.Equal("breakpoint", paused.Reason);
        Assert.Equal((module.Source, 12), (paused.Frames[0].Path, paused.Frames[0].Line));
        Assert.Equal("main:12, App (com.example.console)", paused.Frames[0].Display);
        Assert.Contains("answer = 42", paused.Locals);
        Assert.Contains(paused.Locals, local => local.StartsWith("args = {String[0]@", StringComparison.Ordinal) && local.EndsWith("} []", StringComparison.Ordinal));
        Assert.Contains(paused.Threads, thread => thread.Id == paused.ThreadId && thread.Display.StartsWith("\"main\"@", StringComparison.Ordinal));

        JvmDebugVariable primes = Assert.Single(paused.Variables, variable => variable.Name == "primes");
        Assert.EndsWith("} [2, 3, 5]", primes.Value);
        Assert.Equal(["[0] = 2", "[1] = 3", "[2] = 5"], (await debug.VariablesAsync(primes.Reference)).Select(v => $"{v.Name} = {v.Value}"));
        JvmDebugVariable app = Assert.Single(paused.Variables, variable => variable.Name == "app");
        Assert.Contains(await debug.VariablesAsync(app.Reference), field => field.Name == "greeting" && field.Value == "\"Hello\"");

        int frame = paused.Frames[0].Id;
        Assert.Equal("42", (await debug.EvaluateAsync("answer", frame)).Value);
        Assert.Equal("5", (await debug.EvaluateAsync("primes[2]", frame)).Value);
        Assert.Equal("3", (await debug.EvaluateAsync("primes.length", frame)).Value);
        Assert.Equal("\"Hello\"", (await debug.EvaluateAsync("app.greeting", frame)).Value);
        Assert.Equal("\"Hello, Ada\"", (await debug.EvaluateAsync("app.greet(\"Ada\")", frame)).Value);
        Assert.Equal("6", (await debug.EvaluateAsync("Math.max(6, 1)", frame)).Value);
        await Assert.ThrowsAsync<DapException>(() => debug.EvaluateAsync("missing", frame));

        Assert.Equal("4", (await debug.EvaluateAsync("answer=4", frame)).Value);
        Assert.Equal("4", (await debug.EvaluateAsync("answer", frame)).Value);
        Assert.Equal("7", (await debug.EvaluateAsync("primes[0] = 7", frame)).Value);
        Assert.Equal("7", (await debug.EvaluateAsync("primes[0]", frame)).Value);
        Assert.Equal("5", (await debug.EvaluateAsync("answer = primes[2]", frame)).Value);
        Assert.Equal("5", (await debug.EvaluateAsync("answer", frame)).Value);
        await Assert.ThrowsAsync<DapException>(() => debug.EvaluateAsync("Math.max(6, 1) = 3", frame));
        await Assert.ThrowsAsync<DapException>(() => debug.EvaluateAsync("answer = \"text\"", frame));

        await debug.StepOverAsync();
        paused = await NextAsync(stops, cancel);
        Assert.Equal(("step", 13), (paused.Reason, paused.Frames[0].Line));
        await debug.ContinueAsync();
    }

    [Fact]
    public async Task StepIntoShowsTheCalleesThisAndArguments()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await using Module module = await Module.CreateAsync(cancel);
        await using var debug = new JvmInteractiveDebugger();
        var stops = Channel.CreateUnbounded<JvmDebugPause>();
        debug.Paused += pause => stops.Writer.TryWrite(pause);
        await debug.StartAsync(module.Snapshot, module.Launch(), new Dictionary<string, int[]> { [module.Source] = [12] }, cancel);
        await NextAsync(stops, cancel);
        await debug.StepIntoAsync();
        JvmDebugPause paused = await NextAsync(stops, cancel);
        Assert.Equal("greet:25, App (com.example.console)", paused.Frames[0].Display);
        Assert.Equal("main:12, App (com.example.console)", paused.Frames[1].Display);
        Assert.Contains(paused.Locals, local => local.StartsWith("this = {App@", StringComparison.Ordinal));
        Assert.Contains("name = \"Wayfarer\"", paused.Locals);
        // The caller's frame keeps its own variables.
        Assert.Contains(await debug.VariablesAsync(paused.Frames[1].Id), local => local.Name == "answer" && local.Value == "42");
        await debug.ContinueAsync();
    }

    [Fact]
    public async Task PauseStopsARunningProgramAndResumeLetsItGo()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await using Module module = await Module.CreateAsync(cancel);
        await using var debug = new JvmInteractiveDebugger();
        var stops = Channel.CreateUnbounded<JvmDebugPause>();
        debug.Paused += pause => stops.Writer.TryWrite(pause);
        TaskCompletionSource spinning = new(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Output += line => { if (line.Contains("Hello, Wayfarer", StringComparison.Ordinal)) spinning.TrySetResult(); };
        await debug.StartAsync(module.Snapshot, module.Launch("spin"), new Dictionary<string, int[]>(), cancel);
        await spinning.Task.WaitAsync(TimeSpan.FromSeconds(60), cancel);

        await debug.PauseAsync();
        JvmDebugPause paused = await NextAsync(stops, cancel);
        Assert.Equal("pause", paused.Reason);
        Assert.Contains(paused.Frames, frame => frame.Path == module.Source && frame.Method == "main");
        Assert.All(paused.Threads, thread => Assert.True(thread.Suspended, thread.Display));
        await debug.ContinueAsync();
        Assert.Contains(await debug.ThreadsAsync(), thread => thread.Name == "main" && !thread.Suspended);
    }

    [AvaloniaFact]
    public async Task TheConsoleTakesTypedInput()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await using Module module = await Module.CreateAsync(cancel);
        JvmPreparedLaunch prepared = await new JvmLaunch().PrepareAsync(module.Snapshot, module.Launch("ask"), cancellationToken: cancel);
        ProgramConsole console = new();
        await using IJvmProgram program = await console.StartAsync(prepared.Start!, cancel);
        await Until(() => console.Text.Contains("Name?", StringComparison.Ordinal), console, cancel);
        console.View.Session!.Write("Ada\r");
        await Until(() => program.Exited.IsCompleted, console, cancel);
        Assert.Equal(0, await program.Exited);
        await Until(() => console.Text.Contains("Process finished with exit code 0", StringComparison.Ordinal), console, cancel);
        Assert.Contains("Typed Ada", console.Text);
    }

    /// <summary>A JDK 25 starter's App is a compact source file (an instance main in no package or class): a
    /// breakpoint in it stops with the frame shown, and the program runs to its end.</summary>
    [Fact]
    public async Task CompactSourceFileStopsAtBreakpoint()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string jdk = NewProjectTests.CompactJdkHome();
        string parent = Directory.CreateTempSubdirectory("wayfarer compact ").FullName;
        try
        {
            ProjectGenerationResult generated = await ProjectGenerator.GenerateAsync(new ProjectGenerationOptions
            {
                ProjectName = "Compact", Destination = Path.Combine(parent, "compact"), GroupId = "com.example",
                ArtifactId = "compact", PackageName = "com.example.compact", JdkHome = jdk,
                Kind = NewProjectKind.Application, Language = NewProjectLanguage.Java, Build = NewProjectBuild.IntelliJ,
                LaunchName = "Run",
            }, cancel);
            WorkspaceTrust.Trust(generated.Destination);
            try
            {
                string source = Path.Combine(generated.Destination, "src", "App.java");
                File.WriteAllText(source, "void main() {\n    int answer = 42;\n    IO.println(\"Answer \" + answer);\n}\n");
                WorkspaceSnapshot snapshot = await new ProjectHostImporter().ImportAsync(generated.Destination, 1, cancel);
                JvmModule module = Assert.Single(snapshot.Modules);
                Assert.Equal("App", module.MainClasses[0].ClassName);
                await using var debug = new JvmInteractiveDebugger();
                var stops = Channel.CreateUnbounded<JvmDebugPause>();
                debug.Paused += pause => stops.Writer.TryWrite(pause);
                var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                debug.Ended += () => ended.TrySetResult();
                List<string> output = [];
                debug.Output += line => { lock (output) output.Add(line); };
                await debug.StartAsync(snapshot,
                    new JvmLaunchConfiguration { Module = module.Id, MainClass = "App", WorkingDirectory = module.Root },
                    new Dictionary<string, int[]> { [source] = [3] }, cancel);

                JvmDebugPause paused = await NextAsync(stops, cancel);
                Assert.Equal((source, 3), (paused.Frames[0].Path, paused.Frames[0].Line));
                Assert.Equal("main:3, App", paused.Frames[0].Display);
                Assert.Contains("answer = 42", paused.Locals);
                JvmDebugVariable self = Assert.Single(paused.Variables, variable => variable.Name == "this");
                await debug.VariablesAsync(self.Reference);
                int frame = paused.Frames[0].Id;
                Assert.Equal("42", (await debug.EvaluateAsync("answer", frame)).Value);
                await debug.EvaluateAsync("this", frame);
                await debug.CompletionsAsync("ans", 3, frame);
                // IO.println is the JDK's, so Step Into stays in main.
                await debug.StepIntoAsync();
                paused = await NextAsync(stops, cancel);
                Assert.Equal("main:4, App", paused.Frames[0].Display);
                await debug.ContinueAsync();
                await ended.Task.WaitAsync(TimeSpan.FromSeconds(60), cancel);
                Assert.Equal(0, debug.ExitCode);
                Assert.Contains("Answer 42", output);
            }
            finally
            {
                WorkspaceTrust.TakeBack(generated.Destination);
            }
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task Until(Func<bool> condition, ProgramConsole console, CancellationToken cancel)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(60)) Assert.Fail("The console shows:\n" + console.Text);
            await Task.Delay(50, cancel);
        }
    }

    private static async Task<JvmDebugPause> NextAsync(Channel<JvmDebugPause> stops, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        return await stops.Reader.ReadAsync(timeout.Token);
    }

    internal sealed class Module : IAsyncDisposable
    {
        private Module(string root, WorkspaceSnapshot snapshot, JvmModule module)
        {
            Root = root;
            Snapshot = snapshot;
            Jvm = module;
            Source = module.MainClasses[0].SourcePath;
        }

        public string Root { get; }
        public WorkspaceSnapshot Snapshot { get; }
        public JvmModule Jvm { get; }
        public string Source { get; }

        public JvmLaunchConfiguration Launch(params string[] arguments) => new()
        {
            Module = Jvm.Id, MainClass = Jvm.MainClasses[0].ClassName, WorkingDirectory = Jvm.Root, Arguments = arguments,
        };

        public static async Task<Module> CreateAsync(CancellationToken cancel, string program = Program)
        {
            string parent = Directory.CreateTempSubdirectory("wayfarer console ").FullName;
            ProjectGenerationResult generated = await ProjectGenerator.GenerateAsync(new ProjectGenerationOptions
            {
                ProjectName = "Console", Destination = Path.Combine(parent, "console"), GroupId = "com.example",
                ArtifactId = "console", PackageName = "com.example.console", JdkHome = NewProjectTests.JdkHome(),
                Kind = NewProjectKind.Application, Language = NewProjectLanguage.Java, Build = NewProjectBuild.IntelliJ,
                LaunchName = "Run",
            }, cancel);
            WorkspaceTrust.Trust(generated.Destination);
            WorkspaceSnapshot snapshot = await new ProjectHostImporter().ImportAsync(generated.Destination, 1, cancel);
            JvmModule module = Assert.Single(snapshot.Modules);
            string source = module.MainClasses[0].SourcePath;
            // The program declares com.example.console, so it goes in that folder whatever form the starter's App took.
            string app = Path.Combine(generated.Destination, "src", "com", "example", "console", "App.java");
            Directory.CreateDirectory(Path.GetDirectoryName(app)!);
            if (!PathNames.Same(app, source)) File.Delete(source);
            File.WriteAllText(app, program);
            snapshot = await new ProjectHostImporter().ImportAsync(generated.Destination, 2, cancel);
            return new Module(parent, snapshot, Assert.Single(snapshot.Modules));
        }

        public ValueTask DisposeAsync()
        {
            WorkspaceTrust.TakeBack(Snapshot.Root);
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return ValueTask.CompletedTask;
        }
    }
}
