// From Passenger 2044e49b src/Passenger.App/Workspace/FileIndent.cs, MIT (notices/Passenger/LICENSE): the language's
// default is the caller's, since Passenger.Languages is not here.
using Passenger.Core;
using Passenger.Text;

namespace WayfarerWanderer.App.Shell.Workspace;

/// <summary>Where a file's indentation comes from: the person's choice on the indent widget, else the first of these that
/// says: an .editorconfig, the file's own lines, the default.</summary>
public enum IndentSource
{
    EditorConfig,
    Detected,
    Default,
    Chosen,
}

/// <summary>How a file indents and why, as the status bar's indent widget names it.</summary>
public sealed record FileIndent(IndentOptions Options, IndentSource Source, string? EditorConfig = null)
{
    /// <summary>Four spaces a level, as Java and Kotlin's conventions and IntelliJ IDEA's defaults indent.</summary>
    public static IndentOptions Default { get; } = new(4, false, 4);

    /// <summary>A file's indentation: the one chosen for it, else its .editorconfig's, else what its own lines showed, else
    /// the default.</summary>
    public static FileIndent Choose(EditorConfig conventions, IndentOptions? detected, IndentOptions fallback, IndentOptions? chosen = null) =>
        chosen is { } person ? new FileIndent(person, IndentSource.Chosen, conventions.IndentFrom)
        : conventions.Indent(fallback) is { } configured ? new FileIndent(configured, IndentSource.EditorConfig, conventions.IndentFrom)
        : detected is { } own ? new FileIndent(own, IndentSource.Detected)
        : new FileIndent(fallback, IndentSource.Default);

    /// <summary>The widget's words for the indentation: "Tab", "4 spaces".</summary>
    public string Name => NameOf(Options);

    /// <summary>The widget's text: the indentation, and where it comes from when that is an .editorconfig or a choice, as
    /// IntelliJ IDEA words it ("4 spaces, from .editorconfig").</summary>
    public string Shown => Source switch
    {
        IndentSource.EditorConfig => $"{Name}, from .editorconfig",
        IndentSource.Chosen => $"{Name}, chosen",
        _ => Name,
    };

    /// <summary>The widget's tip, naming the .editorconfig that gives the indentation.</summary>
    public string Tip(string language) => Source switch
    {
        IndentSource.EditorConfig => $"Indent: {Name}, from {EditorConfig}",
        IndentSource.Detected => $"Indent: {Name}, detected from the file's own lines",
        IndentSource.Chosen => EditorConfig is null ? $"Indent: {Name}, chosen for this file" : $"Indent: {Name}, chosen over {EditorConfig}",
        _ => $"Indent: {Name}, {language}'s default",
    };

    /// <summary>What the indent widget's menu offers: a tab, then two, four and eight spaces, at the tab width in force.</summary>
    public static IReadOnlyList<IndentOptions> Offered(IndentOptions now) =>
        [new IndentOptions(now.TabWidth, true, now.TabWidth), .. ((int[])[2, 4, 8]).Select(size => new IndentOptions(size, false, now.TabWidth))];

    /// <summary>The words an indentation is offered in: "Tab", "4 spaces".</summary>
    public static string NameOf(IndentOptions options) => options.UseTabs ? "Tab" : Plural.Of(options.Size, "space");
}
