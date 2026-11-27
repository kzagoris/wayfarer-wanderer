// From Passenger 494009d7 src/Passenger.App/CSharp/GitIgnoreAssist.cs and src/Passenger.App/Workspace/FileChecks.cs, MIT
// (notices/Passenger/LICENSE): the completion list, card and marks as answers of their own (IFileAssist) for Wayfarer's
// editor to show, the .gitignore's marks taken from FileChecks.
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;
using SyntaxLanguage = WayfarerWanderer.App.Shell.Languages.Language;

namespace WayfarerWanderer.App.Shell.Vcs;

/// <summary>A .gitignore as git reads it, every answer git's own (<see cref="Git.IgnoreReadingAsync"/>): completion lists
/// the untracked folders and files under its folder git does not ignore yet, by their paths from it, and a pattern for
/// each kind of file among them (<c>*.log</c>); the hover card says what a line ignores, or keeps, in the working tree,
/// with how many, the tracked files it matches, which git never ignores, and the later line taking back what it ignores.
/// Its marks, for the file as saved: a line taken back by a later one, or matching only tracked files, a warning; one
/// ignoring nothing in the working tree faded.</summary>
public sealed class GitIgnoreAssist(Func<Git?> repository) : IFileAssist
{
    /// <summary>The most paths a card names.</summary>
    private const int MostNamed = 8;

    public string WordCharacters => "/.-_*";

    /// <summary>Whether a file is a .gitignore of the repository's, which git reads.</summary>
    public static bool IsGitIgnore(string path) => SyntaxLanguage.ForPath(path) == SyntaxLanguage.Ignore && Path.GetFileName(path) == Git.IgnoreFileName;

    public bool Owns(string path) => IsGitIgnore(path);

    public bool Opens(string text, int caret, char typed) => Spot(text, caret) is { } spot && spot.Start == caret - 1 && !char.IsWhiteSpace(typed);

    public async Task<IReadOnlyList<AssistItem>> ItemsAsync(string path, string text, int caret, CancellationToken cancel)
    {
        if (Spot(text, caret) is null || repository() is not { } git || await git.IgnoreReadingAsync(path) is not { } reading)
        {
            return [];
        }
        cancel.ThrowIfCancellationRequested();
        List<AssistItem> items = [];
        foreach (string untracked in reading.Untracked.OrderBy(p => p.EndsWith('/') ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal))
        {
            bool folder = untracked.EndsWith('/');
            items.Add(new AssistItem(untracked, folder ? 19 : 17, folder ? "untracked folder" : "untracked file", $"{untracked}: untracked, which git does not ignore yet"));
        }
        foreach (IGrouping<string, string> kind in reading.Untracked.Where(p => !p.EndsWith('/')).GroupBy(p => Path.GetExtension(p).ToLowerInvariant())
            .Where(g => g.Key.Length > 1).OrderByDescending(g => g.Count()))
        {
            items.Add(new AssistItem("*" + kind.Key, 17, Plural.Of(kind.Count(), "untracked file"),
                $"*{kind.Key}: every {kind.Key} file under this folder, {Plural.Of(kind.Count(), "untracked one")} here now"));
        }
        return items;
    }

    /// <summary>What a line ignores or keeps in the working tree, as git answers, as the file stands on disk.</summary>
    public async Task<string?> HoverAsync(string path, TextSnapshot text, bool modified, int offset, CancellationToken cancel)
    {
        int line = text.GetPosition(Math.Min(offset, text.Length)).Line;
        string written = text.GetText(text.GetLine(line).Start, text.GetLine(line).Length).Trim();
        if (written.Length == 0 || written.StartsWith('#') || repository() is not { } git)
        {
            return null;
        }
        IgnoreReading? reading = modified ? null : await git.IgnoreReadingAsync(path);
        cancel.ThrowIfCancellationRequested();
        string said = reading is null ? "Git reads the file as saved: save it to see what this line ignores" : string.Join("\n\n", Said(written, reading.Of(line + 1)));
        return $"```gitignore\n{written}\n```\n\n{said}";
    }

    public async Task<IReadOnlyList<Marker>> MarksAsync(string path, TextSnapshot text, CancellationToken cancel)
    {
        string? saved = await Task.Run(() => FileText.Load(path)?.Text, cancel);
        if (repository() is not { } git || saved?.ReplaceLineEndings("\n") != text.ToString().ReplaceLineEndings("\n")
            || await git.IgnoreReadingAsync(path) is not { } reading)
        {
            return [];
        }
        List<Marker> marks = [];
        for (int line = 0; line < text.LineCount; line++)
        {
            TextRange written = TextRange.Trimmed(text, TextRange.FromBounds(text.GetLine(line).Start, text.GetLine(line).End));
            string pattern = text.GetText(written.Start, written.Length);
            if (pattern.Length == 0 || pattern.StartsWith('#'))
            {
                continue;
            }
            IgnoreLineAnswer? answer = reading.Of(line + 1);
            if (answer is { Decides.Count: > 0 })
            {
                continue;
            }
            (MarkerKind kind, string why) = answer switch
            {
                { UndoneBy: { } by } => (MarkerKind.Warning, $"Ignores nothing: line {by} takes back everything it ignores"),
                { Tracked.Count: > 0 } => (MarkerKind.Warning, string.Join(". ", Said(pattern, answer))),
                _ => (MarkerKind.Unnecessary, pattern.StartsWith('!') ? "Keeps nothing in the working tree" : "Ignores nothing in the working tree"),
            };
            marks.Add(new Marker(written, kind, why));
        }
        return marks;
    }

    /// <summary>What a line does in the working tree, as git answers, line by line of the card.</summary>
    public static IReadOnlyList<string> Said(string written, IgnoreLineAnswer? answer)
    {
        bool keeps = written.StartsWith('!');
        List<string> said = [];
        if (answer is { Decides.Count: > 0 } decided)
        {
            said.Add($"{(keeps ? "Keeps" : "Ignores")} {Plural.Of(decided.Decides.Count, "path")} here: {Named(decided.Decides)}");
        }
        else if (answer?.UndoneBy is { } undone)
        {
            said.Add($"Ignores nothing: line {undone} takes back everything it ignores");
        }
        else
        {
            said.Add(keeps ? "Keeps nothing in the working tree" : "Ignores nothing in the working tree");
        }
        if (answer is { Tracked.Count: > 0 } tracked)
        {
            said.Add($"Matches {Plural.Of(tracked.Tracked.Count, "tracked file")}, which git never ignores: {Named(tracked.Tracked)}");
        }
        return said;

        static string Named(IReadOnlyList<string> paths) =>
            Plural.Listed([.. paths.Take(MostNamed), .. paths.Count > MostNamed ? [$"{paths.Count - MostNamed} more"] : Array.Empty<string>()]);
    }

    /// <summary>Where a path or pattern put at an offset goes: the line's pattern, after any <c>!</c> and leading <c>/</c>;
    /// null on a comment.</summary>
    public (int Start, int End)? Spot(string text, int offset)
    {
        int start = TextNavigation.LineStart(text, offset);
        int end = TextNavigation.LineEnd(text, offset);
        while (start < end && text[start] is ' ' or '\t')
        {
            start++;
        }
        if (start < end && text[start] == '#')
        {
            return null;
        }
        while (start < offset && text[start] is '!' or '/')
        {
            start++;
        }
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }
        return offset < start ? null : (start, Math.Max(end, offset));
    }
}
