using System.Text.Json;
using Passenger.Editor;
using Passenger.Text;

namespace WayfarerWanderer.App;

/// <summary>The inlay hints JDT LS and the Kotlin server give (<c>textDocument/inlayHint</c>), placed in the text they were
/// asked for. The Kotlin server says each hint's kind; JDT LS says none, so its words tell: a parameter's name ends with a
/// colon (<c>left:</c>), a <c>var</c>'s type starts with one (<c>: int</c>), and a lambda parameter's type comes before
/// the name it types (<c>String s</c>), which is moved after the name (<c>s: String</c>) to read as the other types do
/// and to go with that name through an edit. A type is drawn against the name it types, as written it would be. Each
/// hint keeps the server's own as its <see cref="InlayHint.Data"/>, which its hover card sends back to resolve it.</summary>
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
            hints.Add(Shaped(text, offset, words, kind, Flag(item, "paddingLeft"), Flag(item, "paddingRight")) with { Data = item });
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

    /// <summary>What a resolved hint's tooltips say, as Markdown: its own, then its label parts', a paragraph each; empty
    /// where it has none.</summary>
    public static string Tooltip(JsonElement hint)
    {
        if (hint.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }
        List<string> said = [];
        foreach (JsonElement owner in hint.TryGetProperty("label", out JsonElement label) && label.ValueKind == JsonValueKind.Array
            ? [hint, .. label.EnumerateArray()] : (JsonElement[])[hint])
        {
            if (owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("tooltip", out JsonElement tooltip) && Markdown(tooltip) is { } words)
            {
                said.Add(words);
            }
        }
        return string.Join("\n\n", said);
    }

    /// <summary>Where the first of a hint's label parts that says where it is declared points (a Kotlin parameter's name,
    /// once resolved): the file's URI and the LSP position there; null where none does.</summary>
    public static (string Uri, JsonElement Position)? Declared(JsonElement hint)
    {
        if (hint.ValueKind != JsonValueKind.Object || !hint.TryGetProperty("label", out JsonElement label) || label.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (JsonElement part in label.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("location", out JsonElement location)
                && location.ValueKind == JsonValueKind.Object
                && location.TryGetProperty("uri", out JsonElement uri) && uri.ValueKind == JsonValueKind.String
                && location.TryGetProperty("range", out JsonElement range) && range.ValueKind == JsonValueKind.Object
                && range.TryGetProperty("start", out JsonElement start) && start.ValueKind == JsonValueKind.Object)
            {
                return (uri.GetString()!, start);
            }
        }
        return null;
    }

    /// <summary>A parameter name hint's card from the signature help at its argument, as Markdown: the call's signature as
    /// code in a fence naming <paramref name="language"/>, then what the server says of the parameter; empty where it gives
    /// no signature.</summary>
    public static string Signature(JsonElement help, string language)
    {
        if (help.ValueKind != JsonValueKind.Object || !help.TryGetProperty("signatures", out JsonElement signatures)
            || signatures.ValueKind != JsonValueKind.Array || signatures.GetArrayLength() == 0)
        {
            return string.Empty;
        }
        JsonElement signature = signatures[Index(help, "activeSignature") is int active && active < signatures.GetArrayLength() ? active : 0];
        if (signature.ValueKind != JsonValueKind.Object || !signature.TryGetProperty("label", out JsonElement label)
            || label.ValueKind != JsonValueKind.String || label.GetString()?.Trim() is not { Length: > 0 } words)
        {
            return string.Empty;
        }
        string card = Fenced(words, language);
        // The signature's own active parameter wins over the answer's.
        if ((Index(signature, "activeParameter") ?? Index(help, "activeParameter")) is int parameter
            && signature.TryGetProperty("parameters", out JsonElement parameters) && parameters.ValueKind == JsonValueKind.Array
            && parameter < parameters.GetArrayLength() && parameters[parameter] is { ValueKind: JsonValueKind.Object } chosen
            && chosen.TryGetProperty("documentation", out JsonElement documentation) && Markdown(documentation) is { } said)
        {
            card += "\n\n" + said;
        }
        return card;
    }

    /// <summary>A hover answer as Markdown, a paragraph a part: markup as it is (plain text escaped to read as written), a
    /// marked string with a language as code in a fence naming it, one without as Markdown; empty where it says nothing.</summary>
    public static string Hover(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("contents", out JsonElement contents))
        {
            return string.Empty;
        }
        List<string> said = [];
        foreach (JsonElement part in contents.ValueKind == JsonValueKind.Array ? [.. contents.EnumerateArray()] : (JsonElement[])[contents])
        {
            string? words = part.ValueKind switch
            {
                JsonValueKind.String => part.GetString()?.Trim(),
                JsonValueKind.Object when part.TryGetProperty("language", out JsonElement language) && language.ValueKind == JsonValueKind.String
                    && part.TryGetProperty("value", out JsonElement code) && code.ValueKind == JsonValueKind.String =>
                    code.GetString()?.Trim() is { Length: > 0 } written ? Fenced(written, language.GetString()!) : null,
                _ => Markdown(part),
            };
            if (words is { Length: > 0 })
            {
                said.Add(words);
            }
        }
        return string.Join("\n\n", said);
    }

    private static int? Index(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement index) && index.TryGetInt32(out int at) && at >= 0 ? at : null;

    /// <summary>A tooltip or documentation as Markdown: a string, or markup of the kind <c>plaintext</c>, is plain text, its
    /// punctuation escaped so it reads as written; null where it says nothing.</summary>
    private static string? Markdown(JsonElement said)
    {
        (string? words, bool plain) = said.ValueKind switch
        {
            JsonValueKind.String => (said.GetString(), true),
            JsonValueKind.Object when said.TryGetProperty("value", out JsonElement value) && value.ValueKind == JsonValueKind.String =>
                (value.GetString(), said.TryGetProperty("kind", out JsonElement kind) && kind.ValueEquals("plaintext")),
            _ => (null, false),
        };
        return words?.Trim() is not { Length: > 0 } trimmed ? null : plain ? Escaped(trimmed) : trimmed;
    }

    /// <summary>Plain text as Markdown that reads as it: a backslash before each ASCII punctuation mark.</summary>
    private static string Escaped(string plain)
    {
        System.Text.StringBuilder markdown = new(plain.Length + 8);
        foreach (char c in plain)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is ' ' or '\n' or '\r' or '\t' || !char.IsAscii(c) || char.IsControl(c))
            {
                markdown.Append(c);
            }
            else
            {
                markdown.Append('\\').Append(c);
            }
        }
        return markdown.ToString();
    }

    /// <summary>Text in a Markdown fence long enough for any run of backticks it holds.</summary>
    private static string Fenced(string text, string language)
    {
        int longest = 0;
        for (int at = 0, run = 0; at < text.Length; at++)
        {
            run = text[at] == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        string fence = new('`', Math.Max(3, longest + 1));
        return $"{fence}{language}\n{text}\n{fence}";
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
