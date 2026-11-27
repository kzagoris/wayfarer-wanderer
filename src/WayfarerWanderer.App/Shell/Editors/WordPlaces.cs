// From Passenger 494009d7 src/Passenger.App/CSharp/UsageHighlighting.cs, MIT (notices/Passenger/LICENSE): a word's places
// alone, for Wayfarer's one editor; the language server's uses of a symbol and Highlight Usages in File are cut, as
// Wayfarer asks its Java and Kotlin servers for neither yet.
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace WayfarerWanderer.App.Shell.Editors;

/// <summary>A word's places, lit: a selection of one whole word, as a double click makes, lights that word's other
/// whole-word places in the file (<see cref="TextSearch.PlacesOfWord"/>), found off the window's thread once the selection
/// rests, drawn in the editor and marked on its stripe. They go as soon as the caret leaves them or the text changes, so
/// none is ever left on the wrong word, and come again once it rests on a word chosen.</summary>
public sealed class WordPlaces
{
    /// <summary>How long the selection rests before its word's places are looked for.</summary>
    public static TimeSpan Pause { get; set; } = TimeSpan.FromMilliseconds(300);

    private readonly CodeEditor editor;
    private readonly Debounce rest;
    private CancellationTokenSource? asking;

    public WordPlaces(CodeEditor editor)
    {
        this.editor = editor;
        rest = new Debounce(Pause, AskAsync);
        editor.SelectionChanged += (_, _) => Moved();
    }

    /// <summary>The text changed (which the editor takes the places away for): asked again once the selection rests.</summary>
    public void TextChanged()
    {
        Stop();
        AskOnceRested();
    }

    private void Moved()
    {
        UiThread.Check();
        if (editor.UsagesKept)
        {
            return;
        }
        int caret = editor.Selections.Primary.Caret;
        if (!editor.Selections.HasMultiple && OnOne(editor.UsageHighlights, caret))
        {
            // Still on a place of the word they are the places of.
            return;
        }
        Stop();
        if (editor.UsageHighlights.Count > 0)
        {
            editor.SetUsageHighlights([]);
        }
        AskOnceRested();
    }

    /// <summary>Waits for the selection to rest where a word is chosen; a caret moved with nothing selected waits for nothing.</summary>
    private void AskOnceRested()
    {
        if (!editor.Selections.HasMultiple && !editor.Selections.Primary.IsEmpty)
        {
            rest.Restart();
        }
    }

    private void Stop()
    {
        rest.Stop();
        asking?.Cancel();
        asking = null;
    }

    private async Task AskAsync()
    {
        if (editor.UsagesKept || editor.Selections.HasMultiple)
        {
            return;
        }
        asking?.Cancel();
        CancellationTokenSource cancel = asking = new CancellationTokenSource();
        if (await PlacesAsync(cancel.Token) is not { } places || asking != cancel || editor.UsagesKept)
        {
            return;
        }
        editor.SetUsageHighlights(places);
    }

    /// <summary>The places of the word the editor's selection is, the chosen one among them, found off the window's thread;
    /// none for a selection that is no whole word, and null when the text changed meanwhile.</summary>
    private async Task<List<UsageHighlight>?> PlacesAsync(CancellationToken cancel)
    {
        Selection chosen = editor.Selections.Primary;
        if (chosen.IsEmpty)
        {
            return [];
        }
        TextSnapshot text = editor.Document.Snapshot;
        IReadOnlyList<TextRange> places = await BackgroundWork.Run(() => TextSearch.PlacesOfWord(text, chosen.Range));
        if (cancel.IsCancellationRequested || !ReferenceEquals(editor.Document.Snapshot, text))
        {
            return null;
        }
        List<UsageHighlight> found = new(places.Count);
        foreach (TextRange place in places)
        {
            found.Add(new UsageHighlight(place, IsWrite: false, ByText: true));
        }
        return found;
    }

    /// <summary>Whether a caret stands on a range, at either end of it included.</summary>
    private static bool Covers(TextRange range, int caret) => caret >= range.Start && caret <= range.End;

    /// <summary>Whether a caret stands on one of the places (sorted, apart from each other), found by halving.</summary>
    private static bool OnOne(IReadOnlyList<UsageHighlight> uses, int caret)
    {
        int at = Sorted.FirstAtOrAfter(uses, static u => u.Range.End, caret);
        return at < uses.Count && Covers(uses[at].Range, caret);
    }
}
