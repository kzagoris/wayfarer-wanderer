namespace WayfarerWanderer.Jvm;

public enum TestSelectorScope
{
    Module,
    Class,
    Method,
}

public sealed record TestSelector(
    JvmBuildSystem BuildSystem,
    string Module,
    string ClassName,
    string? MethodName = null)
{
    public TestSelectorScope Scope => MethodName is null
        ? ClassName.Length == 0 ? TestSelectorScope.Module : TestSelectorScope.Class
        : TestSelectorScope.Method;

    public string BuildArgument => MethodName is null ? ClassName : $"{ClassName}.{MethodName}";

    public IReadOnlyList<string> Arguments() => Scope == TestSelectorScope.Module
        ? []
        : BuildSystem == JvmBuildSystem.Gradle
            ? ["--tests", BuildArgument]
            : ["-Dtest=" + BuildArgument];
}

public sealed record TestRunResult(
    Guid InvocationId,
    string WorkspaceRoot,
    TestSelector Selector,
    int ExitCode,
    IReadOnlyList<JunitCase> Cases,
    bool WasCancelled);

public sealed class TestInvocation
{
    private readonly string resultsRoot;

    public Guid Id { get; } = Guid.NewGuid();

    public string ReportDirectory { get; }

    public TestSelector Selector { get; }

    public string WorkspaceRoot { get; }

    public TestInvocation(string resultsRoot, string workspaceRoot, TestSelector selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        this.resultsRoot = Path.GetFullPath(resultsRoot);
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        Selector = selector ?? throw new ArgumentNullException(nameof(selector));
        ReportDirectory = Directory.CreateDirectory(Path.Combine(this.resultsRoot, Id.ToString("N"))).FullName;
    }

    public TestInvocation(string resultsRoot)
        : this(resultsRoot, resultsRoot, new TestSelector(JvmBuildSystem.Gradle, string.Empty, string.Empty))
    {
    }

    public TestRunResult Complete(int exitCode, bool wasCancelled = false)
    {
        List<JunitCase> cases = [];
        foreach (string path in Directory.EnumerateFiles(ReportDirectory, "TEST-*.xml", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            using TextReader report = File.OpenText(path);
            cases.AddRange(JunitResults.Read(report));
        }
        return new TestRunResult(Id, WorkspaceRoot, Selector, exitCode, cases, wasCancelled);
    }

    public JvmBuildRequest BuildRequest(string task = "test")
    {
        List<string> arguments = [.. Selector.Arguments()];
        if (Selector.BuildSystem == JvmBuildSystem.Gradle)
        {
            string reportsScript = Path.Combine(AppContext.BaseDirectory, "Gradle", "test-reports.gradle");
            arguments.AddRange(["-I", reportsScript, "-Dwayfarer.testReports=" + ReportDirectory]);
        }
        else
        {
            arguments.Add("-Dsurefire.reportsDirectory=" + ReportDirectory);
        }
        return new JvmBuildRequest(
            WorkspaceRoot,
            Selector.Module,
            task,
            arguments);
    }

    public void Delete()
    {
        if (Directory.Exists(ReportDirectory))
        {
            Directory.Delete(ReportDirectory, recursive: true);
        }
    }
}
