namespace WayfarerWanderer.Jvm;

public sealed record TestRunResult(Guid InvocationId, int ExitCode, IReadOnlyList<JunitCase> Cases);

public sealed class TestInvocation
{
    public Guid Id { get; } = Guid.NewGuid();
    public string ReportDirectory { get; }

    public TestInvocation(string resultsRoot)
    {
        ReportDirectory = Directory.CreateDirectory(Path.Combine(resultsRoot, Id.ToString("N"))).FullName;
    }

    public TestRunResult Complete(int exitCode)
    {
        var cases = new List<JunitCase>();
        foreach (var path in Directory.EnumerateFiles(ReportDirectory, "TEST-*.xml", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            using var report = File.OpenText(path);
            cases.AddRange(JunitResults.Read(report));
        }
        return new TestRunResult(Id, exitCode, cases);
    }
}
