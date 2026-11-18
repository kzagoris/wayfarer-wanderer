using System.Xml;
using System.Xml.Linq;

namespace WayfarerWanderer.Jvm;

public enum TestOutcome { Passed, Failed, Skipped }

public sealed record JunitCase(string ClassName, string DisplayName, TestOutcome Outcome, string? Detail);

public static class JunitResults
{
    public static IReadOnlyList<JunitCase> Read(TextReader report)
    {
        using var reader = XmlReader.Create(report, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var root = XElement.Load(reader);
        if (root.Name.LocalName is not ("testsuite" or "testsuites"))
            throw new InvalidDataException("Expected a JUnit test suite.");

        return root.Descendants().Where(e => e.Name.LocalName == "testcase" &&
            e.Parent?.Name.LocalName == "testsuite").Select(ReadCase).ToArray();
    }

    private static JunitCase ReadCase(XElement test)
    {
        var result = test.Elements().FirstOrDefault(e => e.Name.LocalName is "failure" or "error" or "skipped");
        var outcome = result?.Name.LocalName switch
        {
            null => TestOutcome.Passed,
            "skipped" => TestOutcome.Skipped,
            _ => TestOutcome.Failed
        };
        var detail = result == null ? null : string.Join("\n", new[] { (string?)result.Attribute("message"), result.Value }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        return new JunitCase((string?)test.Attribute("classname") ?? "",
            (string?)test.Attribute("name") ?? throw new InvalidDataException("Test case has no name."), outcome, detail);
    }
}
