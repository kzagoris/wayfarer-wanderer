using System.Text.RegularExpressions;

namespace WayfarerWanderer.Tests;

/// <summary>Tests reach the checkout through <see cref="Repo"/>, never by climbing a fixed number of folders up from the
/// test binary: that count holds only for the default artifacts path, and a build under another one (the way round a
/// running Wayfarer locking <c>artifacts/bin</c>) then fails on paths, not on what the test checks. Climbing three folders
/// to the artifacts root's own <c>test-results</c> stays inside the artifacts path and is allowed.</summary>
public sealed partial class RepoPathRuleTests
{
    [GeneratedRegex(@"BaseDirectory,\s*""(\.\./){4}")]
    private static partial Regex ClimbsOutOfArtifacts();

    [Fact]
    public void TestsFindTheCheckoutThroughRepo()
    {
        List<string> climbs = [];
        foreach (string file in Directory.EnumerateFiles(Repo.At("tests", "WayfarerWanderer.Tests"), "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (ClimbsOutOfArtifacts().IsMatch(lines[i]))
                {
                    climbs.Add($"{Path.GetRelativePath(Repo.Root, file)}:{i + 1}: use Repo.At(...)");
                }
            }
        }
        Assert.Empty(climbs);
    }
}
