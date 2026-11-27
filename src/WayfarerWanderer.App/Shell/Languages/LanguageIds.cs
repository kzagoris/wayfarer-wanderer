namespace WayfarerWanderer.App.Shell.Languages;

/// <summary>The languages Wayfarer knows by id, as VS Code and Passenger name them, so a theme's per-language colours
/// (<c>languages</c> in its file) mean the same here. The syntax colouring of each comes with its grammar.</summary>
public static class LanguageIds
{
    public static IReadOnlyList<string> All { get; } =
    [
        "plaintext", "java", "kotlin", "groovy", "xml", "yaml", "json", "properties", "markdown", "shellscript", "dockerfile", "ignore",
        "gitattributes",
    ];
}
