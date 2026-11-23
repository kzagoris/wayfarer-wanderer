// From Passenger 8a1df7e4 src/Passenger.App/Workspace/FileSearch.cs and SearchScope.cs, MIT (notices/Passenger/LICENSE).
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Passenger.Core;
using Passenger.Text;

namespace WayfarerWanderer.App.Shell.Navigation;

/// <summary>A match: the file, where it is (line and column from zero, its length), and its line's text.</summary>
public sealed record SearchMatch(string Path, int Line, int Column, int Length, string LineText)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>A line with matches, as Find in Files lists it: once, however many it holds, its matches in the line's order.</summary>
public sealed record SearchLine(IReadOnlyList<SearchMatch> Matches)
{
    public SearchMatch First => Matches[0];

    public string Path => First.Path;

    public int Line => First.Line;

    public string LineText => First.LineText;

    public string Name => First.Name;

    /// <summary>Matches as a search gives them, file by file in each file's order, gathered into their lines.</summary>
    public static IReadOnlyList<SearchLine> Of(IReadOnlyList<SearchMatch> matches)
    {
        List<SearchLine> lines = [];
        List<SearchMatch>? on = null;
        foreach (SearchMatch match in matches)
        {
            if (on is null || on[0].Line != match.Line || on[0].Path != match.Path)
            {
                on = [];
                lines.Add(new SearchLine(on));
            }
            on.Add(match);
        }
        return lines;
    }
}

/// <summary>What Find in Files found: the matches, and the files a regex ran too long in, whose search stopped there.</summary>
public sealed record FilesFound(IReadOnlyList<SearchMatch> Matches, IReadOnlyList<string> Stopped)
{
    public static readonly FilesFound None = new([], []);

    /// <summary>The matches Replace in Files replaces: those in the files whose search ended.</summary>
    public IReadOnlyList<SearchMatch> Replaceable => Stopped.Count == 0 ? Matches : [.. Matches.Where(m => !Stopped.Contains(m.Path))];

    /// <summary>Where the search stopped, each file named as <paramref name="name"/> gives it; null when it stopped nowhere.</summary>
    public string? StoppedIn(Func<string, string> name) =>
        Stopped.Count == 0 ? null : Refusals.RegexStoppedIn(string.Join(", ", Stopped.Select(name)));

    /// <summary>How many matches in how many files.</summary>
    public static string Said(IReadOnlyCollection<SearchMatch> matches)
    {
        int files = matches.Select(m => m.Path).Distinct().Count();
        return $"{Plural.Of(matches.Count, "match", "matches")} in {Plural.Of(files, "file")}";
    }
}

/// <summary>Where Find in Files looks: the whole project folder, one module, a directory, or a named scope (open files,
/// the file shown, the files viewed lately).</summary>
public enum SearchScopeKind
{
    Project,
    Module,
    Directory,
    Scope,
}

/// <summary>A search's scope as the popup has it: its kind, what each kind names (the module's folder, the directory and
/// whether its subdirectories are searched, the named scope), and the file mask, null while it is off.</summary>
public sealed record SearchScope(SearchScopeKind Kind, string? Module, string? Directory, bool Recursive, string? Named, string? Mask)
{
    /// <summary>Why the scope cannot be searched as it stands, or null.</summary>
    public string? Problem() => Kind switch
    {
        SearchScopeKind.Module when string.IsNullOrEmpty(Module) => "No module to search",
        SearchScopeKind.Directory when string.IsNullOrWhiteSpace(Directory) || !System.IO.Directory.Exists(Directory) => "Directory is not found",
        SearchScopeKind.Scope when string.IsNullOrEmpty(Named) => "No scope to search",
        _ => Mask is null ? null : FileMask.Problem(Mask),
    };
}

/// <summary>A module the Module scope offers: its name and folder.</summary>
public sealed record SearchModule(string Name, string Folder)
{
    public override string ToString() => Name;
}

/// <summary>A file mask: wildcards (<c>*</c>, <c>?</c>) separated by commas that a file's name must match, letter case
/// aside; one after a <c>!</c> leaves the files it matches out, and exclusions alone leave the rest in.</summary>
public sealed class FileMask
{
    private readonly Regex? included;
    private readonly Regex? excluded;

    private FileMask(Regex? included, Regex? excluded)
    {
        this.included = included;
        this.excluded = excluded;
    }

    /// <summary>Why a mask does not read, or null.</summary>
    public static string? Problem(string mask) =>
        mask.Trim().Length == 0 ? "Empty file mask"
        : mask.Contains(';', StringComparison.Ordinal) ? "File masks should be comma-separated"
        : null;

    /// <summary>The mask written, or null for one with nothing in it.</summary>
    public static FileMask? Parse(string? mask)
    {
        if (mask is null || Problem(mask) is not null)
        {
            return null;
        }
        List<string> included = [];
        List<string> excluded = [];
        foreach (string part in mask.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith('!'))
            {
                excluded.Add(Pattern(part[1..].Trim()));
            }
            else
            {
                included.Add(Pattern(part));
            }
        }
        return new FileMask(Compiled(included), Compiled(excluded));
    }

    /// <summary>Whether a file's name passes the mask.</summary>
    public bool Matches(string name) => (included?.IsMatch(name) ?? true) && !(excluded?.IsMatch(name) ?? false);

    private static Regex? Compiled(List<string> patterns) =>
        patterns.Count == 0 ? null : new Regex($"^(?:{string.Join('|', patterns)})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static string Pattern(string wildcard)
    {
        StringBuilder pattern = new();
        foreach (char c in wildcard)
        {
            pattern.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }
        return pattern.ToString();
    }
}

/// <summary>Find in Files over the files a scope names, matching as the editor's Find bar does (the same query and
/// options): an open file as the editor has it, unsaved changes and all; binary and very large files passed over. Replace
/// All changes an open file as an edit (so it undoes) and writes a closed one in its own encoding.</summary>
public static class FileSearch
{
    /// <summary>The most matches listed.</summary>
    public const int MostMatches = TextSearch.MaxMatches;

    private const long LargestFile = 4 * 1024 * 1024;

    /// <summary>Why a file written since it was searched is left as it was.</summary>
    public const string ChangedWhy = "edited while it was searched";

    /// <summary>Why a read-only file is left as it was.</summary>
    public const string ReadOnlyWhy = "read-only";

    /// <summary>Why a file a regex ran too long in is left as it was.</summary>
    public const string StoppedWhy = Refusals.RegexStopped;

    /// <summary>Why a link, or a file a link in the folder leads out of it to, is left as it was.</summary>
    public const string OutsideWhy = "a link, or outside the folder where a link leads";

    /// <summary>What is wrong with a query (a regex that does not read), or null.</summary>
    public static string? Problem(SearchQuery query)
    {
        if (query.IsEmpty)
        {
            return null;
        }
        try
        {
            _ = SearchPattern.For(query, TextSearch.Apart);
            return null;
        }
        catch (ArgumentException error)
        {
            return error.Message;
        }
    }

    /// <summary>The matches in the files given, in their order, at most <paramref name="most"/>, each with its line's text;
    /// an open file's text (<paramref name="open"/>) is searched as the editor has it.</summary>
    public static Task<FilesFound> FindAsync(IEnumerable<string> files, SearchQuery query, Func<string, TextSnapshot?> open,
        CancellationToken cancellation, int most = MostMatches) => BackgroundWork.Run(() =>
    {
        List<SearchMatch> found = [];
        List<string> stopped = [];
        if (query.IsEmpty || Problem(query) is not null)
        {
            return FilesFound.None;
        }
        SearchPattern pattern = SearchPattern.For(query, TextSearch.Apart);
        MatchFilter? filter = MatchFilter.For(query);
        byte[] bytes = [];
        foreach (string path in files)
        {
            cancellation.ThrowIfCancellationRequested();
            TextSnapshot? shown = open(path);
            if (shown is null && filter is not null && !(ReadBytes(path, ref bytes) is int length && filter.MayMatch(bytes.AsSpan(0, length))))
            {
                // A closed file whose bytes hold no match is never read as text.
                continue;
            }
            SearchResult searched;
            if (shown is not null)
            {
                searched = TextSearch.FindAll(shown, pattern, most - found.Count);
                foreach (TextRange match in searched.Matches)
                {
                    TextPosition at = shown.GetPosition(match.Start);
                    int width = shown.GetLine(at.Line).Length;
                    found.Add(new SearchMatch(path, at.Line, at.Column, Math.Min(match.Length, Math.Max(0, width - at.Column)), shown.GetLineText(at.Line)));
                }
            }
            else if (Read(path) is { } text)
            {
                searched = TextSearch.FindAll(text, pattern, most - found.Count);
                AddMatches(found, path, text, searched.Matches);
            }
            else
            {
                continue;
            }
            if (searched.Stopped)
            {
                stopped.Add(path);
            }
            if (found.Count >= most)
            {
                break;
            }
        }
        return new FilesFound(found, stopped);
    }, cancellation);

    /// <summary>A closed file's text as the editor reads it; null for one that is binary, too large or unreadable.</summary>
    private static string? Read(string path)
    {
        try
        {
            return new FileInfo(path) is { Exists: true, Length: <= LargestFile } ? FileText.Load(path)?.Text : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Adds the matches in a closed file's text, each on its line as the editor's lines are read (to a '\n', a '\r'
    /// before it the break's), its column at most the line's length; the lines are walked once, in the matches' order.</summary>
    private static void AddMatches(List<SearchMatch> found, string path, string text, IReadOnlyList<TextRange> matches)
    {
        int line = 0;
        int start = 0;
        int next = text.IndexOf('\n', StringComparison.Ordinal) + 1;
        string? shown = null;
        foreach (TextRange match in matches)
        {
            while (next > 0 && next <= match.Start)
            {
                line++;
                start = next;
                next = text.IndexOf('\n', start) + 1;
                shown = null;
            }
            int end = next == 0 ? text.Length : next - 1 > start && text[next - 2] == '\r' ? next - 2 : next - 1;
            shown ??= text[start..end];
            int column = Math.Min(match.Start - start, end - start);
            found.Add(new SearchMatch(path, line, column, Math.Min(match.Length, Math.Max(0, end - start - column)), shown));
        }
    }

    /// <summary>A closed file's bytes read into the search's buffer, grown as one needs: how many there are, or null for a
    /// file not there, too large or unreadable.</summary>
    private static int? ReadBytes(string path, ref byte[] buffer)
    {
        try
        {
            using SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long size = RandomAccess.GetLength(file);
            if (size > LargestFile)
            {
                return null;
            }
            if (buffer.Length < size)
            {
                buffer = new byte[Math.Max(size, Math.Min(LargestFile, buffer.Length * 2L))];
            }
            int read = 0;
            while (read < size)
            {
                int got = RandomAccess.Read(file, buffer.AsSpan(read, (int)size - read), read);
                if (got == 0)
                {
                    break;
                }
                read += got;
            }
            return read;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What replacing a query's matches in one file comes to, worked out off the UI thread: an open file's edits
    /// of the text searched, or a closed file as read and its text replaced, with its stamp as it was read; or why it is
    /// left as it was.</summary>
    public sealed record Replacing(string Path, TextSnapshot? Searched, IReadOnlyList<TextEdit> Edits, FileText? Closed, string? After, string? Why)
    {
        public FileStamp? Read { get; init; }
    }

    /// <summary>Replaces every match in a file, an open one's text as the editor has it (<paramref name="open"/>), else its
    /// own read from disk, a regex's groups ($1, ${name}) filled in from each match; a file whose search a regex ran too
    /// long in is left, never replaced in part, as is one whose replaced text would be too long.</summary>
    public static Replacing Plan(string path, TextSnapshot? open, SearchPattern pattern, string replacement)
    {
        if (open is not null)
        {
            SearchResult found = TextSearch.FindAll(open, pattern, int.MaxValue);
            string? why = Unreplaced(found, replacement);
            return new Replacing(path, open, why is null ? found.Replacements(replacement) : [], null, null, why);
        }
        try
        {
            // Taken before the read: a write as it reads moves it.
            FileStamp? read = FileStamp.Of(path);
            if (FileText.Load(path) is not { } file)
            {
                return new Replacing(path, null, [], null, null, null);
            }
            SearchResult found = TextSearch.FindAll(file.Text, pattern, int.MaxValue);
            return Unreplaced(found, replacement) is { } why ? new Replacing(path, null, [], null, null, why)
                : new Replacing(path, null, [], file, Applied(file.Text, found.Replacements(replacement)), null) { Read = read };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new Replacing(path, null, [], null, null, Refusal(error));
        }
    }

    /// <summary>Writes a closed file's replaced text in its own encoding: null when the replace leaves it as it was, else
    /// why it could not be written (null on success) and the save's word of a text written as UTF-8.</summary>
    public static (string? Refused, string? Converted)? Write(Replacing plan)
    {
        if (plan is not { Closed: { } file, After: { } after } || after == file.Text)
        {
            return null;
        }
        try
        {
            if (FileStamp.Of(plan.Path) != plan.Read)
            {
                return (ChangedWhy, null);
            }
            return (null, FileText.SaveOver(plan.Path, file, after).Converted);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (Refusal(error), null);
        }
    }

    private static string? Unreplaced(SearchResult found, string replacement) =>
        found.Stopped ? StoppedWhy
        : found.ReplacedLength(replacement) is var length && found.TooLong(length)
            ? string.Create(CultureInfo.InvariantCulture, $"the text replaced would be {length:N0} characters long, more than {SearchResult.MostReplaced:N0}")
            : null;

    /// <summary>Why a file the system would not let be read or written is left as it was.</summary>
    public static string Refusal(Exception error) => error switch
    {
        ReadOnlyFileException => ReadOnlyWhy,
        UnauthorizedAccessException => "access denied",
        FileNotFoundException or DirectoryNotFoundException => "gone",
        _ => error.Message,
    };

    private static string Applied(string text, IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0)
        {
            return text;
        }
        StringBuilder replaced = new(text.Length);
        int at = 0;
        foreach (TextEdit edit in edits)
        {
            replaced.Append(text, at, edit.Start - at);
            replaced.Append(edit.Text);
            at = edit.End;
        }
        replaced.Append(text, at, text.Length - at);
        return replaced.ToString();
    }
}
