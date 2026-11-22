using Passenger.Text;
using WayfarerWanderer.App;

namespace WayfarerWanderer.Tests;

/// <summary>The expression a hover reads in Java, and the inline values written after the stopped method's lines.</summary>
public sealed class JavaDebugTextTests
{
    private const string Source = """
        class App {
            int count = 1;

            int run(int factor, String name) {
                int local = factor * 2; // factor is not "factor" here
                if (local > 0) {
                    String text = name.trim() + "local";
                    /* local
                       factor */ local++;
                }
                return local + count;
            }
        }
        """;

    private static TextSnapshot Text => new TextDocument(Source.ReplaceLineEndings("\n")).Snapshot;

    [Theory]
    [InlineData("name.trim", 3, "name")]
    [InlineData("this.count", 0, null)]
    [InlineData("local + count", 0, "local")]
    [InlineData("return local", 2, null)]
    public void ExpressionAtReadsTheChainEndingAtTheWord(string near, int into, string? expected)
    {
        TextSnapshot text = Text;
        int at = Source.ReplaceLineEndings("\n").IndexOf(near, StringComparison.Ordinal);
        if (near == "this.count")
        {
            TextSnapshot member = new TextDocument("return this.count.value;").Snapshot;
            Assert.Equal("this.count", JavaDebugText.ExpressionAt(member, 13));
            Assert.Equal(7, JavaDebugText.ExpressionStart(member, 13));
            return;
        }
        Assert.Equal(expected, JavaDebugText.ExpressionAt(text, at + into));
    }

    [Fact]
    public void AMethodsNameIsNoExpression()
    {
        TextSnapshot text = new TextDocument("name.trim().length").Snapshot;
        Assert.Null(JavaDebugText.ExpressionAt(text, 6));
        Assert.Equal("name", JavaDebugText.ExpressionAt(text, 1));
    }

    [Fact]
    public void InlineValuesFollowTheLinesOfTheStoppedMethodThatNameTheVariables()
    {
        Dictionary<string, string> values = new() { ["factor"] = "3", ["name"] = "\"Ada\"", ["local"] = "6", ["count"] = "9" };
        var shown = JavaDebugText.InlineValues(Text, stopLine: 10, values).Select(a => $"{a.Line}:{a.Text}").ToList();
        Assert.Equal(
        [
            "3:factor: 3", "3:name: \"Ada\"",
            "4:local: 6", "4:factor: 3",
            "5:local: 6",
            "6:name: \"Ada\"",
            "8:local: 6",
            "10:local: 6", "10:count: 9",
        ], shown);
    }
}
