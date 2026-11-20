// From Passenger 2044e49b src/Passenger.Languages/Highlighters.cs, MIT (notices/Passenger/LICENSE), its C# parse cut.
using Passenger.Editor;
using Passenger.Text;

namespace WayfarerWanderer.App.Shell.Languages;

/// <summary>The colours and folds a language has before any language server answers: its TextMate grammar's colours and
/// XML's or JSON's scan for folds, or none.</summary>
public static class Highlighters
{
    public static IHighlighter? For(Language language, TextDocument document) =>
        language.Grammar is { } grammar ? TextMateHighlighter.For(grammar, document) : null;

    /// <summary>The colours of a file by the language its name says it is in.</summary>
    public static IHighlighter? ForPath(string path, TextDocument document) => For(Language.ForPath(path), document);

    /// <summary>What finds a file's folds, by its language's scan; null for a language with none.</summary>
    public static IFoldFinder? FoldsFor(Language language, TextDocument document) => language.Folds?.Invoke(document);
}
