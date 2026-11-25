using Passenger.Editor;
using Tint;
using WayfarerWanderer.App.Shell.Languages;
using SyntaxLanguage = WayfarerWanderer.App.Shell.Languages.Language;

namespace WayfarerWanderer.App;

/// <summary>Synchronous TextMate colouring for short code snippets shown in a Quick Documentation card: uses the same
/// grammars and <see cref="TokenKind"/> mapping as the editor so signatures and code examples match the active colour
/// scheme immediately on first layout.</summary>
public static class QuickDocSyntax
{
    private const int MaxLines = 200;
    private const int MaxLineLength = 2_000;
    private static readonly TimeSpan LineBudget = TimeSpan.FromMilliseconds(100);

    /// <summary>The language a Markdown fence info word or LSP language id names, or null when no grammar applies.</summary>
    public static SyntaxLanguage? LanguageOf(string? info)
    {
        if (string.IsNullOrWhiteSpace(info))
        {
            return null;
        }
        string word = info.Trim().Split([' ', '\t'], 2)[0].Trim('{', '}', '.').ToLowerInvariant();
        string? file = word switch
        {
            "" => null,
            "java" => "code.java",
            "kotlin" or "kt" or "kts" => "code.kt",
            "groovy" or "gradle" => "code.groovy",
            "xml" or "html" or "xhtml" or "svg" or "pom" => "code.xml",
            "json" or "jsonc" => "code.json",
            "yaml" or "yml" => "code.yaml",
            "properties" or "ini" => "code.properties",
            "markdown" or "md" => "code.md",
            "bash" or "sh" or "shell" or "zsh" => "code.sh",
            _ when word.All(c => char.IsLetterOrDigit(c) || c is '-' or '_') => $"code.{word}",
            _ => null,
        };
        if (file is null)
        {
            return null;
        }
        SyntaxLanguage resolved = SyntaxLanguage.ForPath(file);
        return resolved == SyntaxLanguage.PlainText ? null : resolved;
    }

    /// <summary>Tokenizes <paramref name="code"/> (LF-separated) using <paramref name="language"/>'s TextMate grammar,
    /// returning sorted non-overlapping spans over the full string.</summary>
    public static IReadOnlyList<SyntaxSpan> Tokenize(string code, SyntaxLanguage? language)
    {
        if (string.IsNullOrEmpty(code) || language?.Grammar is not { } grammarFile)
        {
            return [];
        }
        Grammar grammar = Grammar.For(grammarFile);
        List<SyntaxSpan> spans = [];
        string[] lines = code.Split('\n');
        int count = Math.Min(lines.Length, MaxLines);
        int offset = 0;

        lock (grammar.Gate)
        {
            Tokenizer rules = grammar.Rules;
            rules.TimeBudget = LineBudget;
            StateStack state = StateStack.Initial;

            for (int row = 0; row < count; row++)
            {
                string line = lines[row];
                if (line.Length > 0 && line.Length <= MaxLineLength)
                {
                    ReadOnlySpan<EncodedToken> tokens = rules.TokenizeLineEncoded(line.AsSpan(), state, out StateStack next);
                    if (rules.StoppedEarly)
                    {
                        tokens = rules.TokenizeLineEncoded(line.AsSpan(), state, out next);
                    }
                    state = rules.StoppedEarly ? (row == 0 ? grammar.FirstLineEnd : state) : next;
                    for (int i = 0; i < tokens.Length; i++)
                    {
                        int start = tokens[i].Start;
                        int end = Math.Min(i + 1 < tokens.Length ? tokens[i + 1].Start : line.Length, line.Length);
                        TokenKind kind = grammar.KindOf(EncodedTokenAttributes.GetForeground(tokens[i].Metadata));
                        if (end <= start || kind == TokenKind.Text)
                        {
                            continue;
                        }
                        int absolute = offset + start;
                        if (spans.Count > 0 && spans[^1].Kind == kind && spans[^1].End == absolute)
                        {
                            spans[^1] = spans[^1] with { Length = (offset + end) - spans[^1].Start };
                        }
                        else
                        {
                            spans.Add(new SyntaxSpan(absolute, end - start, kind));
                        }
                    }
                }
                else if (row == 0)
                {
                    state = grammar.FirstLineEnd;
                }
                offset += line.Length + 1;
            }
        }
        return spans;
    }

    /// <summary>Breaks a single-line method or constructor signature across multiple lines (IDEA style: opening parenthesis
    /// at the end of the first line, each parameter indented on its own line, closing parenthesis on its own line) when it
    /// has a non-empty parameter list; returns null when the code is already multiline or has no parameters.</summary>
    public static FormattedSignature? BreakParameters(string code)
    {
        if (code.Contains('\n', StringComparison.Ordinal) || FindParamOpen(code) is not int open)
        {
            return null;
        }
        List<(int At, int Spaces, bool Closing)> breaks = [(open + 1, 0, false)];
        int depth = 0;
        char? quote = null;
        int close = -1;
        for (int i = open + 1; i < code.Length; i++)
        {
            char c = code[i];
            if (quote is { } endQuote)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == endQuote)
                {
                    quote = null;
                }
                continue;
            }
            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '(' or '[' or '{' or '<':
                    depth++;
                    break;
                case '>' when i > 0 && code[i - 1] == '=':
                    break;
                case ')' or ']' or '}' or '>' when depth > 0:
                    depth--;
                    break;
                case ')':
                    close = i;
                    break;
                case ',' when depth == 0:
                    int spaces = 0;
                    while (i + 1 + spaces < code.Length && code[i + 1 + spaces] == ' ')
                    {
                        spaces++;
                    }
                    breaks.Add((i + 1, spaces, false));
                    break;
            }
            if (close >= 0)
            {
                break;
            }
        }
        if (close <= open + 1 || string.IsNullOrWhiteSpace(code[(open + 1)..close]))
        {
            return null;
        }
        // Trim any trailing spaces right before the closing parenthesis and break ')' onto its own line.
        int beforeClose = close;
        while (beforeClose > open + 1 && code[beforeClose - 1] == ' ')
        {
            beforeClose--;
        }
        breaks.Add((beforeClose, close - beforeClose, true));

        const string Indent = "    ";
        System.Text.StringBuilder laid = new(code.Length + (breaks.Count * (1 + Indent.Length)));
        int from = 0;
        foreach ((int at, int spaces, bool closing) in breaks)
        {
            laid.Append(code, from, at - from).Append('\n');
            if (!closing)
            {
                laid.Append(Indent);
            }
            from = at + spaces;
        }
        laid.Append(code, from, code.Length - from);
        return new FormattedSignature(laid.ToString(), [.. breaks]);
    }

    private static int? FindParamOpen(string code)
    {
        for (int i = 1; i < code.Length; i++)
        {
            char before = code[i - 1];
            if (code[i] == '(' && (char.IsLetterOrDigit(before) || before is '_' or '$' or '>' or ']'))
            {
                return i;
            }
        }
        return null;
    }

    public sealed class FormattedSignature(string text, (int At, int Spaces, bool Closing)[] breaks)
    {
        private const int IndentLength = 4;

        public string Text { get; } = text;

        public SyntaxSpan[] Map(IReadOnlyList<SyntaxSpan> spans)
        {
            SyntaxSpan[] moved = new SyntaxSpan[spans.Count];
            for (int i = 0; i < spans.Count; i++)
            {
                int start = Moved(spans[i].Start, end: false);
                moved[i] = new SyntaxSpan(start, Math.Max(0, Moved(spans[i].End, end: true) - start), spans[i].Kind);
            }
            return moved;
        }

        private int Moved(int offset, bool end)
        {
            int shift = 0;
            foreach ((int at, int spaces, bool closing) in breaks)
            {
                if (offset < at || (end && offset == at))
                {
                    break;
                }
                int inserted = 1 + (closing ? 0 : IndentLength);
                shift += inserted - Math.Min(spaces, offset - at);
            }
            return offset + shift;
        }
    }
}
