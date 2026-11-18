using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace WayfarerWanderer.Jvm;

public enum TestOutcome { Passed, Failed, Skipped }

public sealed record JunitCase(
    string ClassName,
    string DisplayName,
    TestOutcome Outcome,
    string? Detail,
    string? SourcePath = null,
    int? Line = null);

public static class JunitResults
{
    public static IReadOnlyList<JunitCase> Read(TextReader report)
    {
        using XmlReader reader = XmlReader.Create(report, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        XElement root = XElement.Load(reader);
        if (root.Name.LocalName is not ("testsuite" or "testsuites"))
            throw new InvalidDataException("Expected a JUnit test suite.");

        return root.Descendants().Where(e => e.Name.LocalName == "testcase" &&
            e.Parent?.Name.LocalName == "testsuite").Select(ReadCase).ToArray();
    }

    private static JunitCase ReadCase(XElement test)
    {
        XElement? result = test.Elements().FirstOrDefault(e => e.Name.LocalName is "failure" or "error" or "skipped");
        TestOutcome outcome = result?.Name.LocalName switch
        {
            null => TestOutcome.Passed,
            "skipped" => TestOutcome.Skipped,
            _ => TestOutcome.Failed
        };
        string? detail = result == null ? null : string.Join("\n", new[] { (string?)result.Attribute("message"), result.Value }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        string? file = (string?)test.Attribute("file");
        int? line = int.TryParse((string?)test.Attribute("line"), out int parsedLine) ? parsedLine : null;
        if (line is null && detail is not null)
        {
            Match location = Regex.Match(detail, @"(?<file>[\w.$/\\-]+\.(?:java|kt)):(?<line>\d+)");
            if (location.Success)
            {
                file ??= location.Groups["file"].Value;
                if (int.TryParse(location.Groups["line"].Value, out int detailLine))
                {
                    line = detailLine;
                }
            }
        }
        return new JunitCase(
            (string?)test.Attribute("classname") ?? "",
            (string?)test.Attribute("name") ?? throw new InvalidDataException("Test case has no name."),
            outcome,
            detail,
            file,
            line);
    }
}
