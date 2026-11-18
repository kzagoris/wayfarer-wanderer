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
              <testcase classname="demo.Example" name="broken"><failure message="expected 42">stack trace</failure></testcase>
              <testcase classname="demo.Example" name="ignored"><skipped/></testcase>
              <testcase classname="demo.Example" name="setup"><error message="setup failed"/></testcase>
            </testsuite></testsuite></testsuites>
            """);
        var results = JunitResults.Read(xml);
        Assert.Equal(4, results.Count);
        Assert.Equal(new JunitCase("demo.Example", "greets visitor()", TestOutcome.Passed, null), results[0]);
        Assert.Equal(TestOutcome.Failed, results[1].Outcome);
        Assert.Equal("expected 42\nstack trace", results[1].Detail);
        Assert.Equal(TestOutcome.Skipped, results[2].Outcome);
        Assert.Equal("setup failed", results[3].Detail);
        Assert.Equal(TestOutcome.Failed, results[3].Outcome);
    }
}
