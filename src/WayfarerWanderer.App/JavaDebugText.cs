using Passenger.Editor;
using Passenger.Text;

namespace WayfarerWanderer.App;

/// <summary>What the debugger reads of Java source without a syntax tree: the expression under the mouse or the caret,
/// and the inline values written after the lines of the stopped method.</summary>
public static class JavaDebugText
{
    /// <summary>The longest value shown inline in full; a longer one is cut.</summary>
    private const int Longest = 60;

    /// <summary>The farthest back the stopped method's start is looked for.</summary>
    private const int LinesBack = 400;

    private static readonly HashSet<string> Keywords =
    [
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue", "default",
        "do", "double", "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if", "implements", "import",
        "instanceof", "int", "interface", "long", "native", "new", "package", "private", "protected", "public", "return",
        "short", "static", "strictfp", "super", "switch", "synchronized", "throw", "throws", "transient", "try", "void",
        "volatile", "while", "true", "false", "null", "var", "record", "yield", "sealed", "permits",
    ];

    private static readonly string[] Blocks =
        ["if", "else", "for", "while", "do", "try", "catch", "finally", "switch", "synchronized", "case", "default", "static"];

    private static bool IsPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>The field access chain ending at the word around an offset: "this.names" for "names" in
    /// "this.names.size()"; none on a keyword, a number or a method's name.</summary>
    public static string? ExpressionAt(TextSnapshot text, int offset)
    {
        if (offset >= text.Length || !IsPart(text[offset]))
        {
            if (offset == 0 || offset > text.Length || !IsPart(text[offset - 1])) return null;
            offset--;
        }
        int end = offset;
        while (end < text.Length && IsPart(text[end])) end++;
        int start = offset;
        while (true)
        {
            while (start > 0 && IsPart(text[start - 1])) start--;
            if (start > 1 && text[start - 1] == '.' && IsPart(text[start - 2]))
            {
                start--;
                continue;
            }
            break;
        }
        int after = end;
        while (after < text.Length && text[after] is ' ' or '\t') after++;
        if (after < text.Length && text[after] == '(') return null;
        string expression = text.GetText(start, end - start);
        if (expression.Length == 0 || char.IsDigit(expression[0]) || Keywords.Contains(expression)) return null;
        return expression;
    }

    /// <summary>Where <see cref="ExpressionAt"/>'s expression begins, or -1 with none.</summary>
    public static int ExpressionStart(TextSnapshot text, int offset)
    {
        if (ExpressionAt(text, offset) is not { } expression) return -1;
        int end = Math.Min(offset, text.Length);
        while (end < text.Length && IsPart(text[end])) end++;
        return Math.Max(0, end - expression.Length);
    }

    /// <summary>Inline values: after each line of the stopped method, from its first line to the one it stopped on, the
    /// value of each of the frame's variables that line names. A name after a dot (a field of something else) or before a
    /// parenthesis (a method) is not the variable.</summary>
    public static IReadOnlyList<LineAnnotation> InlineValues(TextSnapshot text, int stopLine, IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0 || stopLine < 0 || stopLine >= text.LineCount) return [];
        int first = MethodStart(text, stopLine);
        List<LineAnnotation> found = [];
        bool comment = false;
        for (int line = first; line <= stopLine; line++)
        {
            List<string> named = [];
            foreach (string name in Names(text.GetLineText(line), ref comment))
                if (values.ContainsKey(name) && !named.Contains(name)) named.Add(name);
            foreach (string name in named) found.Add(new LineAnnotation(line, $"{name}: {Cut(values[name])}", LineAnnotationLook.Muted));
        }
        return found;
    }

    /// <summary>The line the method enclosing a line declares itself on: going back over balanced braces, the first block
    /// opened that is no statement's (if, for, try…), nor a class'.</summary>
    private static int MethodStart(TextSnapshot text, int stopLine)
    {
        int depth = 0;
        for (int line = stopLine - 1; line >= Math.Max(0, stopLine - LinesBack); line--)
        {
            string code = Code(text.GetLineText(line));
            for (int i = code.Length - 1; i >= 0; i--)
            {
                if (code[i] == '}') depth++;
                else if (code[i] == '{')
                {
                    if (depth > 0)
                    {
                        depth--;
                        continue;
                    }
                    string header = code[..i].Trim();
                    if (header.Length == 0 && line > 0) header = Code(text.GetLineText(line - 1)).Trim();
                    if (IsClass(header)) return line + 1;
                    if (header.EndsWith("->", StringComparison.Ordinal) || IsMethod(header)) return line;
                }
            }
        }
        return Math.Max(0, stopLine - LinesBack);
    }

    private static bool IsClass(string header) =>
        System.Text.RegularExpressions.Regex.IsMatch(header, @"(^|\s)(class|interface|enum|record)\s+\w+") || header.EndsWith("new", StringComparison.Ordinal);

    private static bool IsMethod(string header)
    {
        if (!header.Contains(')')) return false;
        string first = new([.. header.TakeWhile(IsPart)]);
        if (header.StartsWith('}')) first = new([.. header.TrimStart('}', ' ').TakeWhile(IsPart)]);
        return !Blocks.Contains(first);
    }

    /// <summary>A line's code without its strings, characters and line comment, for counting braces.</summary>
    private static string Code(string line)
    {
        System.Text.StringBuilder code = new();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            if (c is '"' or '\'')
            {
                int j = i + 1;
                while (j < line.Length && line[j] != c) j += line[j] == '\\' ? 2 : 1;
                code.Append(' ', Math.Min(j, line.Length - 1) - i + 1);
                i = j;
                continue;
            }
            code.Append(c);
        }
        return code.ToString();
    }

    /// <summary>The names a line uses as variables, outside strings and comments; <paramref name="comment"/> carries a block
    /// comment from line to line.</summary>
    private static IEnumerable<string> Names(string line, ref bool comment)
    {
        List<string> names = [];
        int i = 0;
        while (i < line.Length)
        {
            if (comment)
            {
                int close = line.IndexOf("*/", i, StringComparison.Ordinal);
                if (close < 0) return names;
                comment = false;
                i = close + 2;
                continue;
            }
            char c = line[i];
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                comment = true;
                i += 2;
                continue;
            }
            if (c is '"' or '\'')
            {
                int j = i + 1;
                while (j < line.Length && line[j] != c) j += line[j] == '\\' ? 2 : 1;
                i = j + 1;
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '$')
            {
                int start = i;
                while (i < line.Length && IsPart(line[i])) i++;
                int before = start - 1;
                while (before >= 0 && line[before] is ' ' or '\t') before--;
                int after = i;
                while (after < line.Length && line[after] is ' ' or '\t') after++;
                bool member = before >= 0 && line[before] == '.';
                bool called = after < line.Length && line[after] == '(';
                if (!member && !called) names.Add(line[start..i]);
                continue;
            }
            if (char.IsDigit(c))
            {
                while (i < line.Length && IsPart(line[i])) i++;
                continue;
            }
            i++;
        }
        return names;
    }

    private static string Cut(string value) => value.Length <= Longest ? value : value[..(Longest - 1)] + "…";
}
