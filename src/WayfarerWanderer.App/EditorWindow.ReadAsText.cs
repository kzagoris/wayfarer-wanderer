// From Passenger 8a1df7e4 src/Passenger.App/MainWindow.ReadAsText.cs, MIT (notices/Passenger/LICENSE): in the editor, what
// the Java or Kotlin server's hover says of the caret (Wayfarer has no problems list or card of its own yet); the Tests
// window's and Problems' cut, and no agent asks.
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.Text;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Vcs;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>Read as Text: what the thing with the keys says, opened as plain text in the one view a screen reader reads
/// line by line (<see cref="PlainTextView"/>), Escape giving the keys back to where they were: in the terminal its last
/// command and output, in a diff its changes, in the status bar its last message, in the editor what the language server
/// says of the caret.</summary>
public sealed partial class EditorWindow
{
    /// <summary>A thing's text to read: what it is, how it is read, and what is said where it says nothing.</summary>
    private sealed record Readable(string Title, Func<CancellationToken, Task<string>> Text, string None);

    /// <summary>The status bar along the window's foot.</summary>
    private Border? statusBarFrame;

    /// <summary>Says what the language says of a place in a file in place of the language servers: for tests, which run none.</summary>
    public Func<string, TextPosition, CancellationToken, Task<string?>>? HoverFinder { get; set; }

    private void AddReadAsText()
    {
        Actions.Registry.Add(new IdeAction(AppActions.ReadAsText, "Read as Text", "View", context => IdeErrors.Observe(ReadAsTextAsync(context)))
        {
            WhyNot = context => Reading(context.Focused) is not null ? Refusals.ReadingAlready
                : ReadableAt(context) is null ? Refusals.NothingToRead
                : null,
            Words = ["Accessible View", "Plain Text", "Screen Reader", "Read Aloud"],
        });
    }

    /// <summary>The text view open, while one is.</summary>
    public PlainTextView? PlainText => SearchShown as PlainTextView;

    /// <summary>Opens what the thing with the keys says as plain text (<see cref="ReadableAt"/>), or says why it cannot.</summary>
    private async Task ReadAsTextAsync(ActionContext context)
    {
        if (ReadableAt(context) is not { } readable)
        {
            StatusText = Refusals.NothingToRead;
            return;
        }
        string text = await readable.Text(semanticLifetime.Token);
        if (text.Length == 0)
        {
            StatusText = readable.None;
            return;
        }
        ShowAsText(readable.Title, text, context.Focused as InputElement);
    }

    /// <summary>Text shown in the view to read, which has the keys until Escape or Close gives them back to
    /// <paramref name="back"/> (the editor where it is none).</summary>
    public PlainTextView ShowAsText(string title, string text, InputElement? back = null)
    {
        PlainTextView view = new(title, text);
        ShowSearch(view, view, _ =>
        {
            if (back is { IsEffectivelyVisible: true } were && !ReferenceEquals(were, Editor))
            {
                were.Focus();
            }
        });
        return view;
    }

    /// <summary>The view an element is in, while it is in one.</summary>
    private static PlainTextView? Reading(object? focused) =>
        (focused as Visual)?.FindAncestorOfType<PlainTextView>(includeSelf: true);

    /// <summary>What the element with the keys says as text, by where it is: the terminal, a diff, the status bar, the
    /// editor; else the status bar's last message; null with none.</summary>
    private Readable? ReadableAt(ActionContext context)
    {
        Visual? focused = context.Focused as Visual;
        if (focused?.FindAncestorOfType<Passenger.Terminal.TerminalView>(includeSelf: true) is { Session: { } session })
        {
            return new("Terminal: last command and its output", _ => Task.FromResult(session.LastCommandText()), Refusals.NothingToRead);
        }
        if (focused?.FindAncestorOfType<DiffView>(includeSelf: true) is { } diff)
        {
            return new($"Changes: {diff.Subject}", _ => Task.FromResult(diff.ChangesAsText()), Refusals.NothingToRead);
        }
        if (focused is not null && statusBarFrame is { } statusBar && (focused == statusBar || statusBar.IsVisualAncestorOf(focused)))
        {
            return LastMessage();
        }
        if (Editor.IsVisible && SourcePath is { } path && (focused is null || focused == Editor))
        {
            TextPosition caret = Editor.CaretPosition;
            return new($"{Path.GetFileName(path)}, line {caret.Line + 1}, column {caret.Column + 1}", cancel => WordsAtCaretAsync(path, caret, cancel), Refusals.NothingAtCaret);
        }
        return LastMessage();
    }

    /// <summary>The status bar's last message; null while it says nothing.</summary>
    private Readable? LastMessage() =>
        string.IsNullOrEmpty(StatusText) ? null : new("Last message", _ => Task.FromResult(StatusText ?? string.Empty), Refusals.NothingToRead);

    /// <summary>What the language server says of a place in a file (its hover), as plain text; empty where it says nothing,
    /// or no server runs for the file.</summary>
    private async Task<string> WordsAtCaretAsync(string path, TextPosition at, CancellationToken cancel)
    {
        if (HoverFinder is { } finder)
        {
            return await finder(path, at, cancel) ?? string.Empty;
        }
        if (Language(path) is null || RunningServerFor(path) is null)
        {
            return string.Empty;
        }
        try
        {
            JvmLanguageClient client = await LanguageClientAsync(path, cancel);
            JsonElement answer = await client.RequestAsync("textDocument/hover", new { textDocument = DocumentId(path), position = Point(at) }, cancel);
            return HoverText(answer);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>A hover answer's contents as text: its markup's, or each of its marked strings', a paragraph each.</summary>
    public static string HoverText(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("contents", out JsonElement contents))
        {
            return string.Empty;
        }
        StringBuilder text = new();
        foreach (JsonElement part in contents.ValueKind == JsonValueKind.Array ? [.. contents.EnumerateArray()] : (List<JsonElement>)[contents])
        {
            string? said = part.ValueKind == JsonValueKind.String ? part.GetString()
                : part.ValueKind == JsonValueKind.Object && part.TryGetProperty("value", out JsonElement value) ? value.GetString() : null;
            if (said?.Trim() is { Length: > 0 } words)
            {
                text.Append(text.Length > 0 ? "\n\n" : string.Empty).Append(words);
            }
        }
        return text.ToString();
    }
}
