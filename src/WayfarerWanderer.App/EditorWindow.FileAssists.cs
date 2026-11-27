using System.Text.Json;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Vcs;
using WayfarerWanderer.App.Shell.Workspace;

namespace WayfarerWanderer.App;

/// <summary>The help Wayfarer gives files no language server reads (<see cref="IFileAssist"/>), as Passenger gives a
/// .gitignore and a .gitattributes: completion as typing opens it or Complete Code asks, the hover card and Quick
/// Documentation, and marks on the text, checked as the file opens, once typing pauses, as it is saved and as the working
/// tree changes.</summary>
public sealed partial class EditorWindow
{
    /// <summary>How long typing pauses before a file's marks are checked again.</summary>
    private static readonly TimeSpan MarksSettle = TimeSpan.FromMilliseconds(250);

    private IFileAssist[]? fileAssists;

    /// <summary>The pending check of each open file's marks, while typing has not paused.</summary>
    private readonly Dictionary<OpenDocument, DispatcherTimer> marksDue = [];

    private IReadOnlyList<IFileAssist> FileAssists => fileAssists ??= [new GitIgnoreAssist(() => Vcs.Repository), new GitAttributesAssist(() => Vcs.Repository)];

    /// <summary>The help of the file at <paramref name="path"/>, if Wayfarer gives it any.</summary>
    private IFileAssist? AssistFor(string? path) => path is null ? null : FileAssists.FirstOrDefault(a => a.Owns(path));

    /// <summary>The help of the file shown, if any.</summary>
    private IFileAssist? ShownAssist => Editor.IsVisible ? AssistFor(SourcePath) : null;

    // Completion.

    /// <summary>Typing in a file of its own help: with the list open, a character of a name narrows it and anything else
    /// closes it; then whatever the help says opens it (the start of a pattern, an attribute's <c>=</c>) asks.</summary>
    private void OnTypedForAssist(IFileAssist assist, string text)
    {
        char last = text[^1];
        if (completing is { Assist: not null })
        {
            if (char.IsLetterOrDigit(last) || assist.WordCharacters.Contains(last))
            {
                NarrowCompletion();
                if (completing is not null)
                {
                    return;
                }
            }
            CloseCompletion();
        }
        if (assist.Opens(Editor.Document.Snapshot.ToString(), Editor.Selections.Primary.Caret, last))
        {
            UnawaitedWork.Observe(AskAssistCompletionAsync(assist, explicitly: false));
        }
    }

    /// <summary>Lists the help's suggestions at the caret. An ask made by typing gives way to the next one's; one asked for
    /// (Ctrl+Space) says why it lists nothing.</summary>
    private async Task AskAssistCompletionAsync(IFileAssist assist, bool explicitly)
    {
        completionAsk?.Cancel();
        using CancellationTokenSource ask = CancellationTokenSource.CreateLinkedTokenSource(semanticLifetime.Token);
        completionAsk = ask;
        CancellationToken token = ask.Token;
        try
        {
            if (SourcePath is not { } path)
            {
                return;
            }
            TextDocument document = Editor.Document;
            string text = document.Snapshot.ToString();
            int caret = Editor.Selections.Primary.Caret;
            if (assist.Spot(text, caret) is not { } spot)
            {
                if (explicitly)
                {
                    StatusText = "Nothing is completed here";
                }
                return;
            }
            IReadOnlyList<AssistItem> items = await assist.ItemsAsync(path, text, caret, token);
            token.ThrowIfCancellationRequested();
            // The answer is for the spot asked about; typing that grew it keeps it, anything else drops it.
            if (SourcePath != path || !ReferenceEquals(Editor.Document, document)
                || assist.Spot(document.Snapshot.ToString(), Editor.Selections.Primary.Caret)?.Start != spot.Start)
            {
                return;
            }
            List<CompletionChoice> choices = [.. items.Select((item, index) =>
                new CompletionChoice(JsonSerializer.SerializeToElement(new { label = item.Label }), item.Label, item.Detail ?? string.Empty,
                    item.Label, $"{index:D5}", item.Kind, index) { Described = item.Said })];
            completing = new(path, document, spot.Start, null, choices, false) { Assist = assist };
            NarrowCompletion();
            if (completionPopup is not { IsOpen: true } && explicitly)
            {
                StatusText = choices.Count == 0 ? "No suggestions here" : "No suggestions match";
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (completionAsk == ask)
            {
                completionAsk = null;
            }
        }
    }

    /// <summary>Where the open list's items go with the caret at <paramref name="caret"/>: the help's spot, or the word
    /// the caret ends; -1 where nothing goes.</summary>
    private static int CompletionStart(CompletionSession session, TextSnapshot snapshot, int caret) =>
        session.Assist is { } assist ? assist.Spot(snapshot.ToString(), caret)?.Start ?? -1 : CompletionModel.WordStart(snapshot, caret);

    /// <summary>A suggestion of the help's taken into the text, over all of its spot.</summary>
    private void AcceptAssistChoice(CompletionSession session, IFileAssist assist, CompletionChoice choice)
    {
        if (SourcePath != session.Path || !ReferenceEquals(Editor.Document, session.Document))
        {
            return;
        }
        TextSnapshot snapshot = session.Document.Snapshot;
        int caret = Editor.Selections.Primary.Caret;
        if (assist.Spot(snapshot.ToString(), caret) is not { } spot || spot.Start != session.WordStart)
        {
            return;
        }
        int end = Math.Max(spot.End, caret);
        int after = spot.Start + choice.Label.Length;
        session.Document.Edit([new TextEdit(spot.Start, end - spot.Start, choice.Label)], SelectionSet.Single(caret), SelectionSet.Single(after));
        Editor.GoTo(session.Document.Snapshot.GetPosition(after));
        Editor.Focus();
    }

    // Marks.

    /// <summary>An open file's marks followed: checked now, once typing pauses, and as it is saved.</summary>
    private void FollowFileMarks(OpenDocument open)
    {
        open.Document.Changed += (_, _) =>
        {
            if (!documents.ContainsValue(open) || AssistFor(open.Path) is null)
            {
                return;
            }
            if (!marksDue.TryGetValue(open, out DispatcherTimer? due))
            {
                due = new DispatcherTimer { Interval = MarksSettle };
                due.Tick += (_, _) =>
                {
                    due.Stop();
                    marksDue.Remove(open);
                    IdeErrors.Observe(CheckMarksAsync(open));
                };
                marksDue[open] = due;
            }
            due.Stop();
            due.Start();
        };
        open.Saved += (_, _) => IdeErrors.Observe(CheckMarksAsync(open));
        IdeErrors.Observe(CheckMarksAsync(open));
    }

    /// <summary>A closed file's marks followed no more.</summary>
    private void ForgetFileMarks(OpenDocument open)
    {
        if (marksDue.Remove(open, out DispatcherTimer? due))
        {
            due.Stop();
        }
    }

    /// <summary>Every open file of its own help checked again, as the working tree changed under what they say.</summary>
    private void CheckAllFileMarks()
    {
        foreach (OpenDocument open in documents.Values.Where(d => AssistFor(d.Path) is not null).ToList())
        {
            IdeErrors.Observe(CheckMarksAsync(open));
        }
    }

    /// <summary>The marks an open file's help gives its text, shown while the text is still the one checked.</summary>
    private async Task CheckMarksAsync(OpenDocument open)
    {
        if (AssistFor(open.Path) is not { } assist)
        {
            return;
        }
        TextSnapshot text = open.Document.Snapshot;
        IReadOnlyList<Marker> marks;
        try
        {
            marks = await assist.MarksAsync(open.Path, text, semanticLifetime.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (documents.TryGetValue(open.Path, out OpenDocument? still) && ReferenceEquals(still, open) && ReferenceEquals(open.Document.Snapshot, text))
        {
            Decorated(open.Document)?.SetMarkers(marks);
        }
    }
}
