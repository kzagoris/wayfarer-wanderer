using System.Text;
using System.Text.RegularExpressions;

namespace WayfarerWanderer.Tests;

/// <summary>"Passenger" is never a word Wayfarer shows: no string literal in Wayfarer's own source, nor in the vendored Passenger
/// libraries it builds on, says it. Namespaces, comments, provenance headers and resource keys ("Passenger.Panel") are code,
/// not words shown, and are passed over; the few literals that name Passenger for a reason other than showing it are listed in
/// <see cref="Allowed"/>, each with why.</summary>
public sealed partial class NoPassengerInUiTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));

    /// <summary>The word as shown: on its own, not the start of a longer name, and not a resource key's or namespace's first part.</summary>
    [GeneratedRegex(@"\bPassenger\b(?!\.\w)")]
    private static partial Regex Shown();

    /// <summary>Literals that say "Passenger" and are never shown as Wayfarer's words, by file (from the repository's root)
    /// and the literal's text.</summary>
    private static readonly (string File, string Literal, string Why)[] Allowed =
    [
        ("vendor/passenger/src/Passenger.Core/Product.cs", "Passenger", "the default name, which Wayfarer's Product.Identify replaces first in every process"),
        ("vendor/passenger/src/Passenger.Core/OutboundHttp.cs", "Passenger", "the HTTP User-Agent's product token, which no window shows"),
        ("vendor/passenger/src/Passenger.Core/UpdateFolders.cs", "Passenger/", "the prefix of the libraries' names in a deps.json, read, not shown"),
        ("vendor/passenger/src/Passenger.Core/ShellPath.cs", "# The .NET SDK Passenger installed", "the marker of the .NET SDK's line in a shell profile, which must match what Passenger wrote"),
        ("vendor/passenger/src/Passenger.Editor/FallbackFonts.cs", "fonts:Passenger", "the scheme of the editor's fallback font source, a key"),
        ("src/WayfarerWanderer.App/Shell/Theme/ThemeSources.cs", "Passenger", "the look's ID as settings kept it before it was named Wayfarer, read to move it"),
        ("src/WayfarerWanderer.App/Shell/Theme/ThemeResources.cs", "Passenger.", "the prefix of the theme's resource keys"),
        ("src/WayfarerWanderer.App/Shell/Theme/ThemeFiles.cs", "Passenger.", "the prefix of the theme's resource keys, looked for in a theme file's token names"),
        ("src/WayfarerWanderer.App/Shell/Theme/ThemeFiles.cs", " (a token is named without \"Passenger.\")", "said only of a theme file whose author wrote the resource keys' prefix"),
    ];

    [Fact]
    public void NoLiteralWayfarerBuildsSaysPassenger()
    {
        List<string> said = [];
        HashSet<(string, string)> used = [];
        foreach (string file in ScannedFiles())
        {
            string relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
            string text = File.ReadAllText(file);
            IEnumerable<Literal> literals = file.EndsWith(".axaml", StringComparison.Ordinal) ? MarkupWords(text) : CSharpLiterals(text);
            foreach (Literal literal in literals.Where(l => Shown().IsMatch(l.Text)))
            {
                if (Allowed.Any(a => a.File == relative && a.Literal == literal.Text))
                {
                    used.Add((relative, literal.Text));
                    continue;
                }
                said.Add($"{relative}:{LineOf(text, literal.At)}: {literal.Text}");
            }
        }
        Assert.True(said.Count == 0, "Shown words that say Passenger (use Product.Name or Wayfarer's own words):\n" + string.Join("\n", said));
        // An allowance no literal needs any more is taken out, so the list stays what is true.
        string[] unused = [.. Allowed.Where(a => !used.Contains((a.File, a.Literal))).Select(a => $"{a.File}: {a.Literal}")];
        Assert.True(unused.Length == 0, "Allowances no literal needs:\n" + string.Join("\n", unused));
    }

    [Fact]
    public void TheScanFindsLiteralsAndPassesOverCode()
    {
        const string Code = """"
            // Passenger in a comment
            /* Passenger in a block */
            using Passenger.Core;
            var a = "Passenger runs";
            var b = $"{Passenger.Core.Product.Name} and {(x ? "inner Passenger" : "no")}";
            var c = @"verbatim ""Passenger""";
            var d = '"';
            var e = "Passenger.Panel";
            var f = """raw Passenger""";
            """";
        string[] found = [.. CSharpLiterals(Code).Select(l => l.Text).Where(t => Shown().IsMatch(t))];
        Assert.Equal(["Passenger runs", "inner Passenger", "verbatim \"Passenger\"", "raw Passenger"], found);

        const string Markup = """
            <!-- From Passenger 2044e49b src/Passenger.App/X.axaml, MIT -->
            <UserControl xmlns:p="clr-namespace:Passenger.Editor;assembly=Passenger.Editor" Background="{DynamicResource Passenger.Panel}">
                <TextBlock Text="About Passenger" />
            </UserControl>
            """;
        Assert.Equal(["About Passenger"], MarkupWords(Markup).Select(l => l.Text).Where(t => Shown().IsMatch(t)));
    }

    /// <summary>Wayfarer's own source and the vendored libraries its projects reference, followed through their references.</summary>
    private static IEnumerable<string> ScannedFiles()
    {
        HashSet<string> projects = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> waiting = new(Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories));
        while (waiting.TryDequeue(out string? project))
        {
            if (!projects.Add(Path.GetFullPath(project)))
            {
                continue;
            }
            foreach (Match reference in ProjectReference().Matches(File.ReadAllText(project)))
            {
                waiting.Enqueue(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, reference.Groups[1].Value.Replace('\\', '/'))));
            }
        }
        Assert.Contains(projects, p => p.EndsWith("Passenger.Core.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(projects, p => p.EndsWith("Passenger.App.csproj", StringComparison.OrdinalIgnoreCase));
        foreach (string folder in projects.Select(p => Path.GetDirectoryName(p)!))
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                string inFolder = Path.GetRelativePath(folder, file).Replace('\\', '/');
                if (inFolder.StartsWith("bin/", StringComparison.Ordinal) || inFolder.StartsWith("obj/", StringComparison.Ordinal))
                {
                    continue;
                }
                if (file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".axaml", StringComparison.Ordinal))
                {
                    yield return file;
                }
            }
        }
    }

    [GeneratedRegex("""<ProjectReference\s+Include="([^"]+)"\s*/?>""")]
    private static partial Regex ProjectReference();

    private readonly record struct Literal(string Text, int At);

    private static int LineOf(string text, int at) => text.AsSpan(0, at).Count('\n') + 1;

    /// <summary>The words of a markup file: its attribute values and its elements' text, without its comments or namespaces.</summary>
    private static IEnumerable<Literal> MarkupWords(string markup)
    {
        string bare = MarkupComment().Replace(markup, m => new string(' ', m.Length));
        foreach (Match value in MarkupValue().Matches(bare))
        {
            if (!value.Groups[1].Value.StartsWith("xmlns", StringComparison.Ordinal))
            {
                yield return new Literal(value.Groups[2].Value, value.Index);
            }
        }
        foreach (Match words in MarkupText().Matches(bare))
        {
            if (!string.IsNullOrWhiteSpace(words.Groups[1].Value))
            {
                yield return new Literal(words.Groups[1].Value.Trim(), words.Index);
            }
        }
    }

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex MarkupComment();

    [GeneratedRegex(@"([\w:.]+)\s*=\s*""([^""]*)""")]
    private static partial Regex MarkupValue();

    [GeneratedRegex(">([^<]+)<")]
    private static partial Regex MarkupText();

    /// <summary>A C# file's string literals, as their text: regular, verbatim, raw and interpolated, with an interpolation's
    /// holes read as code (their own literals found too). Comments and character literals are passed over.</summary>
    private static List<Literal> CSharpLiterals(string code)
    {
        List<Literal> found = [];
        int i = 0;
        ReadCode(code, ref i, found, closing: null);
        return found;
    }

    /// <summary>Reads code from <paramref name="i"/> to its end, or to the brace that closes an interpolation's hole.</summary>
    private static void ReadCode(string code, ref int i, List<Literal> found, char? closing)
    {
        int depth = 0;
        while (i < code.Length)
        {
            char c = code[i];
            if (c == '/' && At(code, i + 1) == '/')
            {
                while (i < code.Length && code[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && At(code, i + 1) == '*')
            {
                int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? code.Length : end + 2;
            }
            else if (c == '\'')
            {
                i++;
                while (i < code.Length && code[i] != '\'')
                {
                    i += code[i] == '\\' ? 2 : 1;
                }
                i++;
            }
            else if (c is '"' or '$' or '@' && StringStarts(code, i))
            {
                ReadString(code, ref i, found);
            }
            else if (c == '{')
            {
                depth++;
                i++;
            }
            else if (c == '}' && closing is not null && depth == 0)
            {
                return;
            }
            else
            {
                if (c == '}')
                {
                    depth--;
                }
                i++;
            }
        }
    }

    private static char At(string code, int i) => i < code.Length ? code[i] : '\0';

    private static bool StringStarts(string code, int i)
    {
        if (i > 0 && (char.IsLetterOrDigit(code[i - 1]) || code[i - 1] == '_') && code[i] != '"')
        {
            return false;
        }
        while (code[i] is '$' or '@')
        {
            i++;
            if (i >= code.Length)
            {
                return false;
            }
        }
        return code[i] == '"';
    }

    private static void ReadString(string code, ref int i, List<Literal> found)
    {
        int start = i;
        int dollars = 0;
        bool verbatim = false;
        while (code[i] is '$' or '@')
        {
            dollars += code[i] == '$' ? 1 : 0;
            verbatim |= code[i] == '@';
            i++;
        }
        int quotes = 0;
        while (At(code, i + quotes) == '"')
        {
            quotes++;
        }
        bool raw = quotes >= 3;
        if (!raw)
        {
            quotes = 1;
        }
        i += quotes;
        StringBuilder text = new();
        string close = new('"', quotes);
        // A raw string's holes open with as many braces as it has dollars; the others' with one, and "{{" is a brace.
        int braces = raw ? Math.Max(dollars, 1) : 1;
        while (i < code.Length)
        {
            char c = code[i];
            if (raw ? string.CompareOrdinal(code, i, close, 0, quotes) == 0 && At(code, i + quotes) != '"' : c == '"' && !(verbatim && At(code, i + 1) == '"'))
            {
                i += quotes;
                break;
            }
            if (!raw && !verbatim && c == '\\')
            {
                text.Append(Escaped(At(code, i + 1)));
                i += 2;
                continue;
            }
            if (verbatim && !raw && c == '"')
            {
                text.Append('"');
                i += 2;
                continue;
            }
            if (dollars > 0 && c == '{')
            {
                int run = 0;
                while (At(code, i + run) == '{')
                {
                    run++;
                }
                if (!raw && run >= 2)
                {
                    text.Append('{');
                    i += 2;
                    continue;
                }
                if (run >= braces)
                {
                    text.Append(code, i, run - braces);
                    i += run;
                    ReadCode(code, ref i, found, closing: '}');
                    while (At(code, i) == '}')
                    {
                        i++;
                        if (--braces == 0)
                        {
                            break;
                        }
                    }
                    braces = raw ? Math.Max(dollars, 1) : 1;
                    // A hole stands for a value, not words, so the literal's text keeps a space in its place.
                    text.Append(' ');
                    continue;
                }
            }
            if (dollars > 0 && !raw && c == '}' && At(code, i + 1) == '}')
            {
                text.Append('}');
                i += 2;
                continue;
            }
            text.Append(c);
            i++;
        }
        found.Add(new Literal(text.ToString(), start));
    }

    private static char Escaped(char c) => c switch
    {
        'n' => '\n',
        't' => '\t',
        'r' => '\r',
        '0' => '\0',
        _ => c,
    };
}
