using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell.Languages;
using SyntaxLanguage = WayfarerWanderer.App.Shell.Languages.Language;

namespace WayfarerWanderer.App;

/// <summary>What each open file has from its language before any language server answers: the colours of its TextMate
/// grammar and the folds of XML's or JSON's scan, made once per open text and kept with the text's own decorations while
/// it stays open, so switching between files shows their colours at once and keeps what was folded.</summary>
public sealed partial class EditorWindow
{
    private readonly Dictionary<TextDocument, Shown> shown = [];

    /// <summary>Shows a file's text with what the language its path says gives it: a renamed file (Notes.txt to Notes.md)
    /// takes its new language's.</summary>
    private void ShowDocument(string path, TextDocument document, SelectionSet? selections = null, Avalonia.Vector scroll = default)
    {
        Shown held = Hold(path, document);
        Editor.Show(document, selections ?? SelectionSet.Single(0), scroll, decorated: held.Decorations);
        Editor.Highlighter = held.Colours;
    }

    /// <summary>The decorations an open text is drawn with, which hold its change bars.</summary>
    private DocumentDecorations? Decorated(TextDocument document) => shown.GetValueOrDefault(document)?.Decorations;

    /// <summary>A renamed file's colours and folds by its new name, when it is the one shown.</summary>
    private void FollowLanguage(string path, TextDocument document)
    {
        Shown held = Hold(path, document);
        if (ReferenceEquals(Editor.Document, document))
        {
            Editor.Highlighter = held.Colours;
        }
    }

    /// <summary>Lets go of a closed file's colours and folds, which then stop following its text.</summary>
    private void ForgetLanguage(TextDocument document)
    {
        if (shown.Remove(document, out Shown? held))
        {
            if (ReferenceEquals(Editor.Highlighter, held.Colours))
            {
                Editor.Highlighter = null;
            }
            held.Release();
        }
    }

    /// <summary>Lets go of every file's colours and folds, as the window closes.</summary>
    private void ForgetAllLanguages()
    {
        Editor.Highlighter = null;
        foreach (Shown held in shown.Values)
        {
            held.Release();
        }
        shown.Clear();
    }

    private Shown Hold(string path, TextDocument document)
    {
        SyntaxLanguage language = SyntaxLanguage.ForPath(path);
        if (!shown.TryGetValue(document, out Shown? held))
        {
            shown[document] = held = new Shown(new DocumentDecorations(document));
            // An address written in a comment or string is underlined, and Go to Declaration follows it.
            held.Decorations.SetAddresses(WrittenLink);
        }
        if (held.Language != language)
        {
            held.Speak(language, Highlighters.For(language, document), Highlighters.FoldsFor(language, document));
        }
        return held;
    }

    /// <summary>One open text's decorations, which every showing of it draws, and what its language gives it.</summary>
    private sealed class Shown(DocumentDecorations decorations)
    {
        public DocumentDecorations Decorations { get; } = decorations;

        public SyntaxLanguage? Language { get; private set; }

        public IHighlighter? Colours { get; private set; }

        private IFoldFinder? folds;

        /// <summary>Takes a language's colours and fold scan in place of the last language's, whose folds go with it.</summary>
        public void Speak(SyntaxLanguage language, IHighlighter? colours, IFoldFinder? finder)
        {
            Release();
            Language = language;
            Colours = colours;
            folds = finder;
            Decorations.SetFoldRegions(FoldList.Empty);
            if (finder is not null)
            {
                finder.FoldsChanged += OnFoldsChanged;
            }
        }

        public void Release()
        {
            (Colours as IDisposable)?.Dispose();
            if (folds is not null)
            {
                folds.FoldsChanged -= OnFoldsChanged;
                (folds as IDisposable)?.Dispose();
            }
            Colours = null;
            folds = null;
        }

        /// <summary>Regions found for the text as it stands, keeping the folds of the ones they replace, matched by where they
        /// start.</summary>
        private void OnFoldsChanged(object? sender, EventArgs e)
        {
            if (!ReferenceEquals(sender, folds) || folds is null)
            {
                return;
            }
            IReadOnlyList<FoldRegion> found = folds.Folds;
            List<int>? folded = null;
            foreach (FoldRegion region in Decorations.FoldRegions)
            {
                if (region.Folded)
                {
                    (folded ??= []).Add(region.Start);
                }
            }
            Decorations.SetFoldRegions(folded is null ? found : FoldList.Of(found).FoldedAt(folded));
        }
    }
}
