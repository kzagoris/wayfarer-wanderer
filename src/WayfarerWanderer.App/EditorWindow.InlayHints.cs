using System.Text.Json;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>Parameter and type name hints in the Java or Kotlin file shown, from its language server: asked as the file
/// is shown, as editing pauses, as the server comes up or says its import is done, and when it says its hints changed
/// (JDT LS after a build; the Kotlin server as its import ends). Like the outline, they never start a server of their own accord. Both kinds are asked for; the
/// editor draws those Settings' Editor page shows, and nothing is asked while it shows neither. The hints are kept with
/// the file's decorations, carried through each edit until the next answer replaces them.</summary>
public sealed partial class EditorWindow
{
    private Debounce? hintsAgain;
    private TextDocument? hintedText;
    private int hinting;

    /// <summary>The shown file's server, and whether it was ready, when the hints were last asked for its sake.</summary>
    private (JvmLanguageClient? Client, bool Ready) hintsHeard;

    /// <summary>How long editing pauses before the hints are asked for again.</summary>
    private static readonly TimeSpan HintPause = TimeSpan.FromMilliseconds(400);

    private void AddInlayHints()
    {
        hintsAgain = new Debounce(HintPause, HintsAsync);
        FollowHintCard();
    }

    /// <summary>Whether the editor draws either kind of hint, so they are worth asking for.</summary>
    private bool ShowsHints => Editor.ShowParameterHints || Editor.ShowTypeHints;

    /// <summary>Another file shown, or none: its hints asked for at once, the last file's edits no longer followed.</summary>
    private void HintsFollowFile()
    {
        TextDocument? text = Editor.IsVisible && SourcePath is not null ? Editor.Document : null;
        if (ReferenceEquals(text, hintedText))
        {
            return;
        }
        if (hintedText is not null)
        {
            hintedText.Changed -= OnHintedTextChanged;
        }
        hintedText = text;
        hintsAgain?.Stop();
        CloseHintCard();
        if (text is not null)
        {
            text.Changed += OnHintedTextChanged;
            AskHintsNow();
        }
    }

    private void OnHintedTextChanged(object? sender, DocumentChangedEventArgs e)
    {
        CloseHintCard();
        if (ShowsHints && SourcePath is { } path && RunningServerFor(path) is not null)
        {
            hintsAgain?.Restart();
        }
    }

    /// <summary>The shown file's hints asked for now, when its server runs: as it comes up, says it is ready or that its hints
    /// changed, or as Settings show hints again.</summary>
    private void AskHintsNow()
    {
        if (ShowsHints && SourcePath is { } path && RunningServerFor(path) is { HasInlayHints: true })
        {
            IdeErrors.Observe(HintsAsync());
        }
    }

    /// <summary>The shown file's server came up or finished its import: its hints asked for again, once for each. The
    /// server's other words (its import's stages) ask nothing.</summary>
    private void HintsWhenServerChanged()
    {
        if (SourcePath is not { } path || RunningServerFor(path) is not { } client || hintsHeard == (client, client.IsReady))
        {
            return;
        }
        hintsHeard = (client, client.IsReady);
        AskHintsNow();
    }

    /// <summary>A started server's word that its hints changed, heard on the UI thread.</summary>
    private void FollowHints(JvmLanguageClient client) => client.HintsChanged += () => UiThread.Post(AskHintsNow);

    /// <summary>Asks for the shown file's hints over the whole text it has now, and shows them if that is still its text when
    /// they come; a later ask supersedes this one.</summary>
    private async Task HintsAsync()
    {
        int asked = ++hinting;
        if (!ShowsHints || SourcePath is not { } path || RunningServerFor(path) is not { HasInlayHints: true } || !Editor.IsVisible)
        {
            return;
        }
        TextDocument document = Editor.Document;
        try
        {
            JvmLanguageClient client = await LanguageClientAsync(path, semanticLifetime.Token);
            // The server has the text sent last; one edited since asks again as its editing pauses.
            if (!sentSources.TryGetValue(path, out (int Version, TextSnapshot Text) sent) || !ReferenceEquals(sent.Text, document.Snapshot))
            {
                return;
            }
            TextSnapshot text = sent.Text;
            TextPosition end = text.GetPosition(text.Length);
            JsonElement answer = await client.RequestAsync("textDocument/inlayHint", new
            {
                textDocument = DocumentId(path),
                range = new { start = Point(default), end = Point(end) },
            }, semanticLifetime.Token);
            if (asked != hinting || !ReferenceEquals(document.Snapshot, text) || Decorated(document) is not { } decorations)
            {
                return;
            }
            decorations.SetInlayHints(JvmInlayHints.Of(answer, text));
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or JsonException or TimeoutException)
        {
            // The hints shown stay; the next edit or the server's word asks again.
        }
    }
}
