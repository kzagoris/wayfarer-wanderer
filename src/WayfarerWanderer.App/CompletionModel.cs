using System.Text.Json;
using Passenger.Core;
using Passenger.Text;

namespace WayfarerWanderer.App;

/// <summary>One suggestion a language server offered: the item as sent (so it can be resolved and applied), what the
/// list shows, and what typing narrows and orders it by.</summary>
public sealed record CompletionChoice(JsonElement Item, string Label, string Detail, string Filter, string Sort, int Kind, int Index)
{
    /// <summary>The short mark the list shows before a suggestion for its LSP kind.</summary>
    public string Badge => Kind switch
    {
        2 or 3 => "m",          // method, function
        4 => "c",               // constructor
        5 or 10 => "f",         // field, property
        6 or 18 => "v",         // variable, reference
        7 => "C",               // class
        8 => "I",               // interface
        9 or 19 => "p",         // module, folder (a package)
        13 => "E",              // enum
        20 => "e",              // enum member
        21 => "K",              // constant
        14 => "k",              // keyword
        15 => "s",              // snippet
        22 => "S",              // struct (a record)
        25 => "T",              // type parameter
        _ => "·",
    };
}

/// <summary>The language-neutral part of completion: reading a server's answer, narrowing it as the word grows, and
/// turning the chosen item into edits of the current text.</summary>
public static class CompletionModel
{
    public static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>Where the identifier the caret stands at the end of begins.</summary>
    public static int WordStart(TextSnapshot text, int caret)
    {
        int start = caret;
        while (start > 0 && IsWordCharacter(text[start - 1]))
        {
            start--;
        }
        return start;
    }

    /// <summary>The items of a <c>textDocument/completion</c> answer (a list or a <c>CompletionList</c>), snippets left
    /// out since they are inserted as plain text; <paramref name="incomplete"/> says the server wants asking again as
    /// the word grows.</summary>
    public static IReadOnlyList<CompletionChoice> Parse(JsonElement result, out bool incomplete)
    {
        incomplete = false;
        JsonElement items = result;
        if (result.ValueKind == JsonValueKind.Object)
        {
            incomplete = result.TryGetProperty("isIncomplete", out JsonElement more) && more.ValueKind == JsonValueKind.True;
            if (!result.TryGetProperty("items", out items))
            {
                return [];
            }
        }
        if (items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        List<CompletionChoice> choices = [];
        int index = 0;
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.TryGetProperty("insertTextFormat", out JsonElement format) && format.ValueKind == JsonValueKind.Number && format.GetInt32() == 2)
            {
                continue;
            }
            string label = Text(item, "label") ?? "";
            if (label.Length == 0)
            {
                continue;
            }
            string? described = null;
            if (item.TryGetProperty("labelDetails", out JsonElement details) && details.ValueKind == JsonValueKind.Object)
            {
                label += Text(details, "detail") ?? "";
                described = Text(details, "description");
            }
            string detail = described ?? Text(item, "detail") ?? "";
            if (detail.Length > 0 && label.Contains(detail, StringComparison.Ordinal))
            {
                detail = "";
            }
            int kind = item.TryGetProperty("kind", out JsonElement k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : 0;
            string filter = Text(item, "filterText") ?? Bare(Text(item, "label")!);
            choices.Add(new(item.Clone(), label, detail, filter, Text(item, "sortText") ?? label, kind, index++));
        }
        return choices;
    }

    /// <summary>The choices that match what was typed of the word, best first: the word's start in its own case, then
    /// in any case, then its humps (<c>sout</c>, <c>gS</c> for <c>getSize</c>), then anywhere; the server's order breaks ties.</summary>
    public static IReadOnlyList<CompletionChoice> Narrow(IReadOnlyList<CompletionChoice> choices, string typed)
    {
        if (typed.Length == 0)
        {
            return [.. choices.OrderBy(c => c.Sort, StringComparer.Ordinal).ThenBy(c => c.Index)];
        }
        return [.. choices
            .Select(choice => (choice, rank: Rank(choice.Filter, typed)))
            .Where(pair => pair.rank >= 0)
            .OrderBy(pair => pair.rank)
            .ThenBy(pair => pair.choice.Sort, StringComparer.Ordinal)
            .ThenBy(pair => pair.choice.Index)
            .Select(pair => pair.choice)];
    }

    private static int Rank(string candidate, string typed)
    {
        if (candidate.StartsWith(typed, StringComparison.Ordinal))
        {
            return 0;
        }
        if (candidate.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        if (Humps(candidate, typed))
        {
            return 2;
        }
        return candidate.Contains(typed, StringComparison.OrdinalIgnoreCase) ? 3 : -1;
    }

    /// <summary>Whether the typed letters follow the candidate's start and word starts in order: each letter continues the
    /// part being matched or begins a later part (an upper-case letter, or one after <c>_</c> or a digit).</summary>
    private static bool Humps(string candidate, string typed)
    {
        if (char.ToLowerInvariant(candidate[0]) != char.ToLowerInvariant(typed[0]))
        {
            return false;
        }
        int at = 1;
        for (int i = 1; i < typed.Length; i++)
        {
            char wanted = typed[i];
            if (at < candidate.Length && char.ToLowerInvariant(candidate[at]) == char.ToLowerInvariant(wanted))
            {
                at++;
                continue;
            }
            int next = at;
            while (next < candidate.Length && !(IsPartStart(candidate, next) && char.ToLowerInvariant(candidate[next]) == char.ToLowerInvariant(wanted)))
            {
                next++;
            }
            if (next >= candidate.Length)
            {
                return false;
            }
            at = next + 1;
        }
        return true;
    }

    private static bool IsPartStart(string text, int index) =>
        char.IsUpper(text[index]) || text[index - 1] is '_' or '$' || (char.IsDigit(text[index - 1]) && !char.IsDigit(text[index]));

    /// <summary>
    /// The edits that take a choice into the text, and where the caret goes. The server's edit was measured when it was
    /// asked; the word has grown since, so the edit replaces from its start to the caret now. Without an edit the word
    /// typed is replaced. The extra edits (an import) stand before the word and apply as given.
    /// </summary>
    public static (IReadOnlyList<TextEdit> Edits, int Caret) Edits(TextSnapshot text, JsonElement item, int wordStart, int caret)
    {
        TextEdit main;
        if (item.TryGetProperty("textEdit", out JsonElement edit) && edit.ValueKind == JsonValueKind.Object)
        {
            JsonElement range = edit.TryGetProperty("range", out JsonElement r) ? r : edit.GetProperty("replace");
            TextPosition from = JvmSemanticEdits.Position(range.GetProperty("start"));
            if (from.Line < 0 || from.Line >= text.LineCount || from.Column < 0 || from.Column > text.GetLine(from.Line).Length)
            {
                throw new InvalidDataException("The server returned an invalid source position.");
            }
            int start = Math.Min(text.GetOffset(from), caret);
            main = new(start, caret - start, Text(edit, "newText") ?? "");
        }
        else
        {
            string insert = Text(item, "insertText") ?? Bare(Text(item, "label") ?? "");
            main = new(wordStart, caret - wordStart, insert);
        }
        List<TextEdit> edits = [main];
        if (item.TryGetProperty("additionalTextEdits", out JsonElement extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement more in extra.EnumerateArray())
            {
                TextEdit added = JvmSemanticEdits.Edit(text, more.GetProperty("range"), Text(more, "newText") ?? "");
                if (added.Start + added.Length <= main.Start || added.Start >= main.Start + main.Length)
                {
                    edits.Add(added);
                }
            }
        }
        int after = main.Start + main.Text.Length + edits.Skip(1).Where(e => e.Start < main.Start).Sum(e => e.Text.Length - e.Length);
        return (edits, after);
    }

    /// <summary>A label without what JDT LS appends for the list: <c>System - java.lang</c>, <c>size() : int</c>.</summary>
    private static string Bare(string label)
    {
        int end = 0;
        while (end < label.Length && IsWordCharacter(label[end]))
        {
            end++;
        }
        return end == 0 ? label : label[..end];
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
