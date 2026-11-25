using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell.Languages;
using WayfarerWanderer.App.Shell.Settings;
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
        Editor.Show(document, selections ?? SelectionSet.Single(0), scroll, wrapped: held.SoftWrap, decorated: held.Decorations);
        Editor.Highlighter = held.Colours;
        Editor.ShowWhitespaces = held.ShowWhitespaces;
        Editor.Markup = MarkupOf(held.Language);
    }

    /// <summary>The markup whose tags Enter indents by and typing closes (Settings' Close tags): XML's for XML.</summary>
    private static MarkupKind MarkupOf(SyntaxLanguage? language) => language == SyntaxLanguage.Xml ? MarkupKind.Xml : MarkupKind.None;

    /// <summary>The shown file's own view of it, while a file is shown.</summary>
    private Shown? ShownView => Editor.IsVisible && SourcePath is not null ? shown.GetValueOrDefault(Editor.Document) : null;

    /// <summary>The decorations an open text is drawn with, which hold its change bars.</summary>
    private DocumentDecorations? Decorated(TextDocument document) => shown.GetValueOrDefault(document)?.Decorations;

    /// <summary>A renamed file's colours and folds by its new name, when it is the one shown.</summary>
    private void FollowLanguage(string path, TextDocument document)
    {
        Shown held = Hold(path, document);
        if (ReferenceEquals(Editor.Document, document))
        {
            Editor.Highlighter = held.Colours;
            Editor.Markup = MarkupOf(held.Language);
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
            // A file opens wrapped where Settings' masks match its name, its whitespace shown where Settings shows it.
            AppSettings settings = SettingsKept.Effective;
            shown[document] = held = new Shown(new DocumentDecorations(document), kind => SettingsKept.Value.FoldsByDefault(kind))
            {
                SoftWrap = settings.WrapsFile(path),
                ShowWhitespaces = settings.ShowWhitespaces,
            };
            // An address written in a comment or string is underlined, and Go to Declaration follows it.
            held.Decorations.SetAddresses(WrittenLink);
        }
        if (held.Language != language)
        {
            held.Speak(language, Highlighters.For(language, document), Highlighters.FoldsFor(language, document));
        }
        return held;
    }

    /// <summary>One open text's decorations, which every showing of it draws, what its language gives it, and whether it is
    /// shown wrapped and with its whitespace (View, Active Editor). Its first folds fold as Settings' Code Folding says.</summary>
    private sealed class Shown(DocumentDecorations decorations, Func<FoldKind, bool> foldsByDefault)
    {
        public DocumentDecorations Decorations { get; } = decorations;

        public bool SoftWrap { get; set; }

        public bool ShowWhitespaces { get; set; }

        /// <summary>Whether the text's first folds came, which alone fold by default: a region made later stays open.</summary>
        private bool foldedByDefault;

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
            if (!foldedByDefault)
            {
                foldedByDefault = true;
                List<int> starts = [.. found.Where(r => foldsByDefault(r.Kind)).Select(r => r.Start)];
                if (starts.Count > 0)
                {
                    Decorations.SetFoldRegions(FoldList.Of(found).FoldedAt(starts));
                    return;
                }
            }
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
