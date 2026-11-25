using System.Text.Json;
using Avalonia;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>The hover card of an inlay hint, as Passenger shows one: the pointer resting on a hint asks its server what to
/// say of it, and the card hangs under the hint's line. Passenger says what the server's resolved hint says
/// (<c>inlayHint/resolve</c>'s tooltip); neither JVM server gives one (JDT LS resolves nothing, the Kotlin server only says
/// where a parameter is declared), so where it says nothing a parameter name's card is the signature of the call at its
/// argument (<c>textDocument/signatureHelp</c>), else the hover where the parameter is declared, and a type's card is the
/// hover of the name it types. The card goes as the pointer leaves the hint or the text changes, and a hint's server is
/// never started for it.</summary>
public sealed partial class EditorWindow
{
    private HintCard? hintCard;
    private CancellationTokenSource? hintCardAsk;

    /// <summary>The card a hovered hint shows: for tests.</summary>
    public HintCard HintCard => hintCard ??= new HintCard(Editor);

    private void FollowHintCard()
    {
        Editor.Hovered += (_, e) =>
        {
            if (e.Hint is { } hint)
            {
                IdeErrors.Observe(ShowHintCardAsync(hint, e.Point));
            }
            else
            {
                CloseHintCard();
            }
        };
        Editor.HoverEnded += (_, _) => CloseHintCard();
    }

    private void CloseHintCard()
    {
        hintCardAsk?.Cancel();
        hintCardAsk = null;
        hintCard?.Close();
    }

    /// <summary>Asks the shown file's server what to say of a hint the pointer rests on at <paramref name="point"/>, and shows
    /// it under the hint's line while the pointer still rests there and the text is the one the server has.</summary>
    public async Task ShowHintCardAsync(InlayHint hint, Point point)
    {
        CloseHintCard();
        CancellationTokenSource ask = CancellationTokenSource.CreateLinkedTokenSource(semanticLifetime.Token);
        hintCardAsk = ask;
        TextDocument document = Editor.Document;
        try
        {
            if (SourcePath is not { } path || Language(path) is not { } language || RunningServerFor(path) is null)
            {
                return;
            }
            JvmLanguageClient client = await LanguageClientAsync(path, ask.Token);
            // The server's hints and positions are of the text sent last.
            if (!sentSources.TryGetValue(path, out (int Version, TextSnapshot Text) sent) || !ReferenceEquals(sent.Text, document.Snapshot)
                || hint.Offset > sent.Text.Length)
            {
                return;
            }
            string words = await HintWordsAsync(client, path, language, hint, sent.Text, ask.Token);
            if (ask.IsCancellationRequested || !ReferenceEquals(document.Snapshot, sent.Text) || words.Length == 0)
            {
                return;
            }
            // Under the hint's own row, where soft wrap breaks its line.
            double lineTop = Editor.PointOf(hint.Offset).Y;
            HintCard.Show(words, new Point(point.X, lineTop + Editor.LineHeight + 2), lineTop);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or JsonException or TimeoutException)
        {
            // No card: a server that no longer has the hint (the file changed since) says nothing of it.
        }
        finally
        {
            if (ReferenceEquals(hintCardAsk, ask))
            {
                hintCardAsk = null;
            }
            ask.Dispose();
        }
    }

    /// <summary>What the card says of a hint, as Markdown; empty where the server says nothing.</summary>
    private async Task<string> HintWordsAsync(JvmLanguageClient client, string path, string language, InlayHint hint, TextSnapshot text, CancellationToken cancel)
    {
        JsonElement? server = hint.Data is JsonElement sent ? sent : null;
        if (server is { } unresolved && client.ResolvesInlayHints)
        {
            try
            {
                server = await client.RequestAsync("inlayHint/resolve", unresolved, cancel);
            }
            catch (InvalidOperationException)
            {
                // A server that cannot resolve the hint now still has the call and the name to say of.
            }
            if (JvmInlayHints.Tooltip(server.Value) is { Length: > 0 } tooltip)
            {
                return tooltip;
            }
        }
        if (hint.Kind == InlayHintKind.Parameter)
        {
            JsonElement help = await client.RequestAsync("textDocument/signatureHelp",
                new { textDocument = DocumentId(path), position = Point(text.GetPosition(hint.Offset)) }, cancel);
            if (JvmInlayHints.Signature(help, language) is { Length: > 0 } signature)
            {
                return signature;
            }
            if (server is { } resolved && JvmInlayHints.Declared(resolved) is { } declared)
            {
                return JvmInlayHints.Hover(await client.RequestAsync("textDocument/hover", new { textDocument = new { uri = declared.Uri }, position = declared.Position }, cancel));
            }
            return string.Empty;
        }
        // A type ends the name it types; the server's hover of that name says it.
        return hint.Offset == 0 ? string.Empty
            : JvmInlayHints.Hover(await client.RequestAsync("textDocument/hover", new { textDocument = DocumentId(path), position = Point(text.GetPosition(hint.Offset - 1)) }, cancel));
    }
}
