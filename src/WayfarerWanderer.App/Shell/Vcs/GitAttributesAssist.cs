// From Passenger 494009d7 src/Passenger.App/CSharp/GitAttributesAssist.cs and src/Passenger.App/Workspace/FileChecks.cs,
// MIT (notices/Passenger/LICENSE): the completion list, card and marks as answers of their own (IFileAssist) for
// Wayfarer's editor to show, the .gitattributes' marks taken from FileChecks.
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;
using SyntaxLanguage = WayfarerWanderer.App.Shell.Languages.Language;

namespace WayfarerWanderer.App.Shell.Vcs;

/// <summary>A .gitattributes as git reads it (<see cref="GitAttributeNames"/>): completion lists the attributes git and
/// GitHub's Linguist read, with the file's own macros, after a pattern, and an attribute's values after its <c>=</c> (a
/// diff, merge or filter driver git has built in or its configuration declares); the hover card says what an attribute
/// or a value does. Its marks: a value git reads by nothing (an eol other than lf or crlf, a driver neither built in nor
/// declared), a warning on the value.</summary>
public sealed class GitAttributesAssist(Func<Git?> repository) : IFileAssist
{
    public string WordCharacters => "-_.";

    public bool Owns(string path) => SyntaxLanguage.ForPath(path) == SyntaxLanguage.GitAttributes;

    public bool Opens(string text, int caret, char typed) =>
        Where(text, caret) is { } spot && (typed == '=' || spot.Start == caret - 1 && !char.IsWhiteSpace(typed) && typed is not ('-' or '!'));

    public (int Start, int End)? Spot(string text, int caret) => Where(text, caret) is { } spot ? (spot.Start, spot.End) : null;

    public async Task<IReadOnlyList<AssistItem>> ItemsAsync(string path, string text, int caret, CancellationToken cancel)
    {
        if (Where(text, caret) is not { } spot)
        {
            return [];
        }
        List<AssistItem> items = [];
        if (spot.Attribute is { } named)
        {
            IReadOnlySet<(string Kind, string Name)>? declared = repository() is { } git ? await git.DriversAsync() : null;
            cancel.ThrowIfCancellationRequested();
            items.AddRange(ValuesOf(named, declared).Select(v => new AssistItem(v.Value, 20, null, v.Said)));
        }
        else
        {
            items.AddRange(GitAttributeNames.All.Select(a => new AssistItem(a.Name, 10, a.From, a.Said)));
            items.AddRange(MacrosIn(text).Select(m => new AssistItem(m, 10, "this file's macro", $"A macro this file defines ([attr]{m})")));
        }
        return items;
    }

    /// <summary>What an attribute does and the values it takes, or what a value of it does.</summary>
    public async Task<string?> HoverAsync(string path, TextSnapshot snapshot, bool modified, int offset, CancellationToken cancel)
    {
        string text = snapshot.ToString();
        offset = Math.Clamp(offset, 0, text.Length);
        int start = TextNavigation.LineStart(text, offset);
        int end = TextNavigation.LineEnd(text, offset);
        if (GitAttributeNames.Read(text, start, end) is not { } line
            || line.Attributes.Cast<GitAttributeNames.Written?>().FirstOrDefault(a => a!.Value.Start <= offset && offset <= Math.Max(a.Value.End, a.Value.ValueEnd)) is not { } written
            || written.End <= written.Start)
        {
            return null;
        }
        string name = text[written.Start..written.End];
        string code = $"```gitattributes\n{text[start..end].Trim()}\n```\n\n";
        if (written.Valued && offset >= written.ValueStart)
        {
            string value = text[written.ValueStart..written.ValueEnd];
            IReadOnlySet<(string Kind, string Name)>? declared = repository() is { } git ? await git.DriversAsync() : null;
            cancel.ThrowIfCancellationRequested();
            return code + (GitAttributeNames.Problem(name, value, declared)
                ?? ValuesOf(name, declared).FirstOrDefault(v => v.Value == value).Said ?? GitAttributeNames.Of(name)?.Said ?? "An attribute neither git nor Linguist reads");
        }
        if (GitAttributeNames.Of(name) is { } attribute)
        {
            List<string> said = [attribute.Said];
            if (attribute.Values.Count > 0 && attribute.Driver is null)
            {
                said.Add($"Takes {Plural.Either([.. attribute.Values.Select(v => v.Value)])}");
            }
            said.Add($"Read by {attribute.From}");
            if (written.Mark switch { '-' => "Unset here (-)", '!' => "Left unspecified here (!)", _ => null } is { } mark)
            {
                said.Add(mark);
            }
            return code + string.Join("\n\n", said);
        }
        return code + (MacrosIn(text).Contains(name, StringComparer.Ordinal) ? $"A macro this file defines ([attr]{name})" : "An attribute neither git nor Linguist reads");
    }

    public async Task<IReadOnlyList<Marker>> MarksAsync(string path, TextSnapshot snapshot, CancellationToken cancel)
    {
        IReadOnlySet<(string Kind, string Name)>? declared = repository() is { } git ? await git.DriversAsync() : null;
        cancel.ThrowIfCancellationRequested();
        string text = snapshot.ToString();
        List<Marker> marks = [];
        for (int at = 0; at < text.Length; at = TextNavigation.NextLineStart(text, at))
        {
            foreach (GitAttributeNames.Written written in GitAttributeNames.Read(text, at, TextNavigation.LineEnd(text, at))?.Attributes ?? [])
            {
                if (written is { Valued: true, Mark: '\0' } && GitAttributeNames.Problem(text[written.Start..written.End], text[written.ValueStart..written.ValueEnd], declared) is { } why)
                {
                    marks.Add(new Marker(TextRange.FromBounds(written.ValueStart, written.ValueEnd), MarkerKind.Warning, why));
                }
            }
        }
        return marks;
    }

    /// <summary>The values an attribute takes, with what each does: its own, and for a driver's attribute the drivers git's
    /// configuration declares.</summary>
    private static IEnumerable<(string Value, string Said)> ValuesOf(string name, IReadOnlySet<(string Kind, string Name)>? declared)
    {
        if (GitAttributeNames.Of(name) is not { } attribute)
        {
            return [];
        }
        IEnumerable<(string, string)> own = attribute.Values.Select(v => (v.Value, attribute.Driver is null ? v.Said : $"Git's own {attribute.Name} driver: {v.Said}"));
        return attribute.Driver is { } kind && declared is not null
            ? own.Concat(declared.Where(d => d.Kind == kind).Select(d => (d.Name, $"A {kind} driver git's configuration declares ([{kind} \"{d.Name}\"])")).Order())
            : own;
    }

    /// <summary>The macros a .gitattributes defines, by their <c>[attr]</c> lines.</summary>
    private static IEnumerable<string> MacrosIn(string text)
    {
        for (int at = 0; at < text.Length; at = TextNavigation.NextLineStart(text, at))
        {
            if (GitAttributeNames.Read(text, at, TextNavigation.LineEnd(text, at)) is { Macro: true } line && line.PatternEnd > line.PatternStart)
            {
                yield return text[line.PatternStart..line.PatternEnd];
            }
        }
    }

    /// <summary>Where an item put at an offset goes: an attribute's name (from after its <c>-</c> or <c>!</c>) or, after
    /// its <c>=</c>, its value, with the attribute's name; null on the pattern, a comment or a blank line.</summary>
    private static (int Start, int End, string? Attribute)? Where(string text, int offset)
    {
        int lineEnd = TextNavigation.LineEnd(text, offset);
        if (GitAttributeNames.Read(text, TextNavigation.LineStart(text, offset), lineEnd) is not { } line || offset <= line.PatternEnd)
        {
            return null;
        }
        if (line.Attributes.Cast<GitAttributeNames.Written?>().FirstOrDefault(a => a!.Value.Start - (a.Value.Mark == '\0' ? 0 : 1) <= offset && offset <= Math.Max(a.Value.End, a.Value.ValueEnd)) is { } written)
        {
            return written.Valued && offset >= written.ValueStart
                ? (written.ValueStart, written.ValueEnd, text[written.Start..written.End])
                : (written.Start, written.End, null);
        }
        return char.IsWhiteSpace(text[offset - 1]) ? (offset, offset, null) : null;
    }
}
