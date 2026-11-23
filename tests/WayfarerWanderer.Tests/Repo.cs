namespace WayfarerWanderer.Tests;

/// <summary>The checkout the tests run from, found by walking up from the test binary to <c>WayfarerWanderer.slnx</c>. A
/// build under another artifacts path (<c>--artifacts-path</c>, <c>-p:ArtifactsPath=</c>), as when a running Wayfarer locks
/// <c>artifacts/bin</c>, still finds the sources and fixtures. <see cref="RepoPathRuleTests"/> keeps tests on it.</summary>
internal static class Repo
{
    public static string Root { get; } = Find();

    /// <summary>A path under the checkout, from parts that may hold forward slashes ("tests/fixtures/jvm").</summary>
    public static string At(params string[] parts) => Path.GetFullPath(Path.Combine([Root, .. parts]));

    private static string Find()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "WayfarerWanderer.slnx")))
            {
                return folder.FullName;
            }
        }
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }
}
