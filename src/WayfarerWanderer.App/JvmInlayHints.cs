using System.Text.Json;
using Passenger.Editor;
using Passenger.Text;

namespace WayfarerWanderer.App;

/// <summary>The inlay hints JDT LS and the Kotlin server give (<c>textDocument/inlayHint</c>), placed in the text they were
/// asked for. The Kotlin server says each hint's kind; JDT LS says none, so its words tell: a parameter's name ends with a
/// colon (<c>left:</c>), a <c>var</c>'s type starts with one (<c>: int</c>), and a lambda parameter's type comes before
/// the name it types (<c>String s</c>), which is moved after the name (<c>s: String</c>) to read as the other types do
/// and to go with that name through an edit. A type is drawn against the name it types, as written it would be.</summary>
public static class JvmInlayHints
{
    /// <summary>The hints of an answer in <paramref name="text"/>, the one they were asked for; those off its lines left out.</summary>
    public static HintList Of(JsonElement result, TextSnapshot text)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return HintList.Empty;
        }
        List<InlayHint> hints = [];
        foreach (JsonElement item in result.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("position", out JsonElement position)
                || !item.TryGetProperty("label", out JsonElement label) || Offset(text, position) is not int offset)
            {
                continue;
            }
            string words = Label(label).Trim();
            if (words.Length == 0)
            {
                continue;
            }
            int? kind = item.TryGetProperty("kind", out JsonElement said) && said.TryGetInt32(out int number) ? number : null;
            hints.Add(Shaped(text, offset, words, kind, Flag(item, "paddingLeft"), Flag(item, "paddingRight")));
        }
        return HintList.Of(hints);
    }

    /// <summary>A hint as the editor draws it: of the kind the server said (1 a type, 2 a parameter) or its words tell.</summary>
    private static InlayHint Shaped(TextSnapshot text, int offset, string words, int? kind, bool padLeft, bool padRight)
    {
        if (kind == 2 || kind is null && words.EndsWith(':'))
        {
            return new InlayHint(offset, words, padLeft, padRight, InlayHintKind.Parameter);
        }
        if (kind is null && !words.StartsWith(':') && padRight && NameEnd(text, offset) is int end)
        {
            return new InlayHint(end, ": " + words, Kind: InlayHintKind.Type);
        }
        // A type that starts with its colon sits against its name (max: int), as typed it would.
        return new InlayHint(offset, words, padLeft && !words.StartsWith(':'), padRight, InlayHintKind.Type);
    }

    /// <summary>Where the name starting at <paramref name="offset"/> ends, or null when no name starts there.</summary>
    private static int? NameEnd(TextSnapshot text, int offset)
    {
        int end = offset;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '$'))
        {
            end++;
        }
        return end > offset && !char.IsDigit(text[offset]) ? end : null;
    }

    /// <summary>A label sent whole or in parts.</summary>
    private static string Label(JsonElement label) => label.ValueKind switch
    {
        JsonValueKind.String => label.GetString()!,
        JsonValueKind.Array => string.Concat(label.EnumerateArray()
            .Select(part => part.ValueKind == JsonValueKind.Object && part.TryGetProperty("value", out JsonElement value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null)),
        _ => string.Empty,
    };

    private static bool Flag(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement flag) && flag.ValueKind == JsonValueKind.True;

    /// <summary>A server position's offset in the text, or null when it is off the text's lines.</summary>
    private static int? Offset(TextSnapshot text, JsonElement position)
    {
        if (position.ValueKind != JsonValueKind.Object || !position.TryGetProperty("line", out JsonElement l) || !l.TryGetInt32(out int line)
            || !position.TryGetProperty("character", out JsonElement c) || !c.TryGetInt32(out int column)
            || line < 0 || line >= text.LineCount || column < 0 || column > text.GetLine(line).Length)
        {
            return null;
        }
        return text.GetOffset(new TextPosition(line, column));
    }
}
