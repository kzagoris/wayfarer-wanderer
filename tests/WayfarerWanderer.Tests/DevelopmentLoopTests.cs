using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

public class DevelopmentLoopTests
{
    [Fact]
    public void ReadsNestedJUnitSuitesAndPreservesRunnerNames()
    {
        using var xml = new StringReader("""
            <testsuites><testsuite name="outer"><testsuite name="Example">
              <testcase classname="demo.Example" name="greets visitor()" />
              <testcase classname="demo.Example" name="broken"><failure message="expected 42">at demo.Example.broken(Example.java:42)</failure></testcase>
              <testcase classname="demo.Example" name="ignored"><skipped/></testcase>
              <testcase classname="demo.Example" name="setup"><error message="setup failed"/></testcase>
            </testsuite></testsuite></testsuites>
            """);
        var results = JunitResults.Read(xml);
        Assert.Equal(4, results.Count);
        Assert.Equal(new JunitCase("demo.Example", "greets visitor()", TestOutcome.Passed, null), results[0]);
        Assert.Equal(TestOutcome.Failed, results[1].Outcome);
        Assert.Equal("expected 42\nat demo.Example.broken(Example.java:42)", results[1].Detail);
        Assert.Equal("Example.java", results[1].SourcePath);
        Assert.Equal(42, results[1].Line);
        Assert.Equal(TestOutcome.Skipped, results[2].Outcome);
        Assert.Equal("setup failed", results[3].Detail);
        Assert.Equal(TestOutcome.Failed, results[3].Outcome);
    }

    [Fact]
    public void CompileFailureKeepsEarlierPassOutOfCurrentRun()
    {
        var parent = Directory.CreateTempSubdirectory();
        try
        {
            var first = new TestInvocation(parent.FullName);
            File.WriteAllText(Path.Combine(first.ReportDirectory, "TEST-example.xml"),
                """<testsuite><testcase classname="Example" name="passes"/></testsuite>""");
            Assert.Single(first.Complete(0).Cases);

            var next = new TestInvocation(parent.FullName);
            var failed = next.Complete(1);
            Assert.NotEqual(first.Id, failed.InvocationId);
            Assert.Equal(1, failed.ExitCode);
            Assert.Empty(failed.Cases);
            Assert.Single(Directory.GetFiles(first.ReportDirectory));
        }
        finally
        {
            parent.Delete(true);
        }
    }
}
