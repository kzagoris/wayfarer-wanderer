using System.Text;
using System.Text.RegularExpressions;
using Passenger.Text;

namespace WayfarerWanderer.App;

/// <summary>Structured documentation shown by <see cref="QuickDocCard"/>: an optional declaring owner (class or package),
/// a syntax-coloured signature, rich Markdown documentation blocks (paragraphs, inline code, Javadoc/KDoc tag sections,
/// bullet lists, fenced code examples, dividers), and an optional footer naming the JDK, module or source file.</summary>
public sealed record QuickDoc(
    string? Owner,
    string? Signature,
    string SignatureLanguage,
    IReadOnlyList<QuickDocBlock> Body,
    string? SourceLabel = null,
    string? SourceTarget = null)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Owner) && string.IsNullOrWhiteSpace(Signature) && Body.Count == 0;

    /// <summary>Readable plain-text projection of the documentation, used for clipboard copy and tests.</summary>
    public string PlainText()
    {
        List<string> lines = [];
        if (!string.IsNullOrWhiteSpace(Owner))
        {
            lines.Add(Owner);
        }
        if (!string.IsNullOrWhiteSpace(Signature))
        {
            lines.Add(Signature);
        }
        foreach (QuickDocBlock block in Body)
        {
            string rendered = block.PlainText();
            if (rendered.Length > 0)
            {
                lines.Add(rendered);
            }
        }
        return string.Join('\n', lines);
    }
}

public abstract record QuickDocBlock
{
    public abstract string PlainText();
}

public sealed record QuickDocParagraph(IReadOnlyList<QuickDocInline> Inlines, int HeadingLevel = 0) : QuickDocBlock
{
    public override string PlainText() => QuickDocInline.JoinPlain(Inlines);
}

public sealed record QuickDocCodeBlock(string Code, string Language) : QuickDocBlock
{
    public override string PlainText() => Code;
}

public sealed record QuickDocList(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<QuickDocInline>> Items) : QuickDocBlock
{
    public override string PlainText() => string.Join('\n', Items.Select((item, i) =>
        $"{(Ordered ? $"{Start + i}." : "•")} {QuickDocInline.JoinPlain(item)}"));
}

public sealed record QuickDocTagItem(string? Name, IReadOnlyList<QuickDocInline> Description)
{
    public string PlainText()
    {
        string body = QuickDocInline.JoinPlain(Description);
        return string.IsNullOrWhiteSpace(Name) ? body
            : body.Length == 0 ? Name
            : $"{Name} – {body}";
    }
}

public sealed record QuickDocTagSection(string Label, IReadOnlyList<QuickDocTagItem> Items) : QuickDocBlock
{
    public override string PlainText() => Items.Count == 1
        ? $"{Label} {Items[0].PlainText()}".TrimEnd()
        : $"{Label}\n{string.Join('\n', Items.Select(item => "  " + item.PlainText()))}";
}

public sealed record QuickDocDivider : QuickDocBlock
{
    public override string PlainText() => string.Empty;
}

public abstract record QuickDocInline
{
    public abstract void AppendPlain(StringBuilder target);

    public static string JoinPlain(IReadOnlyList<QuickDocInline> parts)
    {
        StringBuilder text = new();
        foreach (QuickDocInline part in parts)
        {
            part.AppendPlain(text);
        }
        return text.ToString().Trim();
    }
}

public sealed record QuickDocText(string Value) : QuickDocInline
{
    public override void AppendPlain(StringBuilder target) => target.Append(Value);
}

public sealed record QuickDocCodeSpan(string Value) : QuickDocInline
{
    public override void AppendPlain(StringBuilder target) => target.Append(Value);
}

public sealed record QuickDocBold(IReadOnlyList<QuickDocInline> Parts) : QuickDocInline
{
    public override void AppendPlain(StringBuilder target)
    {
        foreach (QuickDocInline part in Parts)
        {
            part.AppendPlain(target);
        }
    }
}

public sealed record QuickDocItalic(IReadOnlyList<QuickDocInline> Parts) : QuickDocInline
{
    public override void AppendPlain(StringBuilder target)
    {
        foreach (QuickDocInline part in Parts)
        {
            part.AppendPlain(target);
        }
    }
}

public sealed record QuickDocLink(IReadOnlyList<QuickDocInline> Parts, string Target) : QuickDocInline
{
    public override void AppendPlain(StringBuilder target)
    {
        foreach (QuickDocInline part in Parts)
        {
            part.AppendPlain(target);
        }
    }
}

public sealed record QuickDocBreak : QuickDocInline
{
    public override void AppendPlain(StringBuilder target) => target.Append('\n');
}

/// <summary>Parses LSP hover Markdown (from JDT LS, kotlin-lsp, or any server) into a structured <see cref="QuickDoc"/>
/// model suitable for IDEA-style Quick Documentation rendering.</summary>
public static partial class QuickDocParser
{
    private sealed record RawBlock(bool Code, string Info, string Text);

    public static QuickDoc Parse(string markdown, string defaultLanguage = "")
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new QuickDoc(null, null, defaultLanguage, []);
        }

        List<RawBlock> raw = SplitFences(markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        string? owner = null;
        string? signature = null;
        string signatureLanguage = defaultLanguage;
        int startIndex = 0;

        // When the hover begins with a fenced code block (standard for JDT LS and kotlin-lsp), promote it to the header.
        if (raw.Count > 0 && raw[0].Code && !string.IsNullOrWhiteSpace(raw[0].Text))
        {
            string info = raw[0].Info.Length > 0 ? raw[0].Info : defaultLanguage;
            signatureLanguage = info;
            (owner, signature) = ExtractHeader(raw[0].Text.Trim(), info);
            startIndex = 1;
        }

        List<QuickDocBlock> body = [];
        string? sourceLabel = null;
        string? sourceTarget = null;

        for (int i = startIndex; i < raw.Count; i++)
        {
            RawBlock part = raw[i];
            if (part.Code)
            {
                string code = part.Text.Trim('\n');
                if (code.Length > 0)
                {
                    body.Add(new QuickDocCodeBlock(code, part.Info.Length > 0 ? part.Info : defaultLanguage));
                }
                continue;
            }
            ParseProseBlocks(part.Text, body, ref sourceLabel, ref sourceTarget);
        }

        // Clean up leading, trailing, or consecutive dividers (e.g., kotlin-lsp puts `---` right after the signature fence).
        NormalizeDividers(body);

        return new QuickDoc(owner, signature, signatureLanguage, body, sourceLabel, sourceTarget);
    }

    /// <summary>Splits a header signature into its declaring owner (e.g. <c>java.lang.IO</c>) and concise signature
    /// (e.g. <c>public static void println(Object obj)</c>), preserving the original when no unambiguous owner is present.</summary>
    public static (string? Owner, string Signature) ExtractHeader(string code, string language)
    {
        string trimmed = code.Trim();
        if (trimmed.Length == 0)
        {
            return (null, string.Empty);
        }

        // Kotlin LSP often starts its signature fence with `package foo.bar` on its first line.
        string[] lines = trimmed.Split('\n');
        if (lines.Length >= 2 && lines[0].Trim() is var first && first.StartsWith("package ", StringComparison.Ordinal))
        {
            string pkg = first["package ".Length..].Trim().TrimEnd(';');
            string rest = string.Join('\n', lines.Skip(1)).Trim();
            if (pkg.Length > 0 && rest.Length > 0)
            {
                return (pkg, rest);
            }
        }

        // Multi-line signatures without a package header stay intact.
        if (lines.Length > 1)
        {
            return (null, trimmed);
        }

        // JDT LS local variable / parameter hover: `int max - example.Probe.run()`
        Match local = JdtLocalOwner().Match(trimmed);
        if (local.Success)
        {
            return (local.Groups[2].Value.Trim(), local.Groups[1].Value.Trim());
        }

        // Java / JDT LS method or constructor: `[modifiers/return] pkg.Type.member(params)`
        int paren = FindSignatureParen(trimmed);
        if (paren > 0)
        {
            int tokenStart = paren;
            while (tokenStart > 0 && !char.IsWhiteSpace(trimmed[tokenStart - 1]))
            {
                tokenStart--;
            }
            string qualified = trimmed[tokenStart..paren];
            int lastDot = qualified.LastIndexOf('.');
            if (lastDot > 0)
            {
                string ownerPart = qualified[..lastDot];
                string member = qualified[(lastDot + 1)..];
                if (IsQualifiedName(ownerPart) && IsIdentifier(member))
                {
                    string prefix = trimmed[..tokenStart];
                    string suffix = trimmed[paren..];
                    return (ownerPart, (prefix + member + suffix).Trim());
                }
            }
            return (null, trimmed);
        }

        // JDT LS field / type / constant: `[prefix ]pkg.Type.field` or `pkg.ClassName`
        int lastSpace = trimmed.LastIndexOf(' ');
        string tail = lastSpace >= 0 ? trimmed[(lastSpace + 1)..] : trimmed;
        int typeArgs = tail.IndexOf('<');
        string bareTail = typeArgs >= 0 ? tail[..typeArgs] : tail;
        int dot = bareTail.LastIndexOf('.');
        if (dot > 0)
        {
            string ownerPart = bareTail[..dot];
            string shortTail = tail[(dot + 1)..];
            if (IsQualifiedName(ownerPart) && shortTail.Length > 0 && (char.IsLetter(shortTail[0]) || shortTail[0] is '_' or '$'))
            {
                string prefix = lastSpace >= 0 ? trimmed[..(lastSpace + 1)] : string.Empty;
                return (ownerPart, (prefix + shortTail).Trim());
            }
        }

        return (null, trimmed);
    }

    private static int FindSignatureParen(string code)
    {
        int angle = 0;
        for (int i = 0; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '<': angle++; break;
                case '>' when angle > 0: angle--; break;
                case '(' when angle == 0: return i;
            }
        }
        return -1;
    }

    private static bool IsQualifiedName(string value)
    {
        if (value.Length == 0 || value.StartsWith('.') || value.EndsWith('.'))
        {
            return false;
        }
        foreach (string part in value.Split('.'))
        {
            if (part.Length == 0 || !IsIdentifier(part))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || (!char.IsLetter(value[0]) && value[0] is not ('_' or '$')))
        {
            return false;
        }
        for (int i = 1; i < value.Length; i++)
        {
            if (!char.IsLetterOrDigit(value[i]) && value[i] is not ('_' or '$'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Splits Markdown into alternating prose and fenced code blocks, supporting 3+ backtick fences.</summary>
    private static List<RawBlock> SplitFences(string text)
    {
        List<RawBlock> blocks = [];
        List<string> current = [];
        bool inCode = false;
        int fenceLength = 0;
        string info = string.Empty;

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (!inCode)
            {
                if (BacktickRun(trimmed) is >= 3 and int ticks)
                {
                    if (current.Count > 0)
                    {
                        blocks.Add(new RawBlock(false, string.Empty, string.Join('\n', current)));
                        current.Clear();
                    }
                    inCode = true;
                    fenceLength = ticks;
                    info = trimmed[ticks..].Trim();
                    continue;
                }
            }
            else
            {
                if (BacktickRun(trimmed) is int ticks && ticks >= fenceLength && trimmed[ticks..].Trim().Length == 0)
                {
                    blocks.Add(new RawBlock(true, info, string.Join('\n', current)));
                    current.Clear();
                    inCode = false;
                    fenceLength = 0;
                    info = string.Empty;
                    continue;
                }
            }
            current.Add(line);
        }

        if (current.Count > 0)
        {
            blocks.Add(new RawBlock(inCode, info, string.Join('\n', current)));
        }
        return blocks;
    }

    private static int BacktickRun(string text)
    {
        int n = 0;
        while (n < text.Length && text[n] == '`')
        {
            n++;
        }
        return n;
    }

    private static void ParseProseBlocks(string prose, List<QuickDocBlock> into, ref string? sourceLabel, ref string? sourceTarget)
    {
        string[] lines = prose.Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            int iterationStart = i;
            string rawLine = lines[i];
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            // JDT LS trailing source line: `Source: *[service](file:///...)*` or `Source: *Java 21.0.10 (module: java.base)*`
            Match sourceMatch = JdtSourceLine().Match(trimmed);
            if (sourceMatch.Success)
            {
                sourceLabel = sourceMatch.Groups[1].Value.Trim();
                sourceTarget = sourceMatch.Groups[2].Value.Trim();
                i++;
                continue;
            }
            Match plainSourceMatch = JdtPlainSourceLine().Match(trimmed);
            if (plainSourceMatch.Success)
            {
                sourceLabel = plainSourceMatch.Groups[1].Value.Trim();
                sourceTarget = null;
                i++;
                continue;
            }

            // Horizontal rule (`---`, `***`, `___`)
            if (HorizontalRule().IsMatch(trimmed))
            {
                into.Add(new QuickDocDivider());
                i++;
                continue;
            }

            // ATX heading (`# Heading`)
            Match heading = HeadingLine().Match(trimmed);
            if (heading.Success)
            {
                IReadOnlyList<QuickDocInline> parts = ParseInlines(heading.Groups[2].Value.Trim());
                if (parts.Count > 0)
                {
                    into.Add(new QuickDocParagraph(parts, heading.Groups[1].Value.Length));
                }
                i++;
                continue;
            }

            // Kotlin / Javadoc inline `@param`, `@return`, `@throws`, `@since` lines
            if (TryParseAtTag(lines, ref i, out QuickDocTagSection? atSection))
            {
                MergeTagSection(into, atSection!);
                continue;
            }

            // JDT LS Javadoc bullet section (`* **Parameters:**` followed by indented sub-bullets)
            if (TryParseJavadocBulletSection(lines, ref i, out QuickDocTagSection? jdtSection))
            {
                MergeTagSection(into, jdtSection!);
                continue;
            }

            // Standalone Javadoc tag header line (`Parameters:` / `**Returns:**`) followed by item lines
            if (TryParseStandaloneTagSection(lines, ref i, out QuickDocTagSection? standaloneSection))
            {
                MergeTagSection(into, standaloneSection!);
                continue;
            }

            // Inline label tag line (`**Params:** ...`, `Params: ...`, `**Returns:** ...`)
            if (TryParseInlineTagLine(trimmed, out QuickDocTagSection? inlineSection))
            {
                MergeTagSection(into, inlineSection!);
                i++;
                continue;
            }

            // Regular bullet or numbered list
            if (TryParseList(lines, ref i, out QuickDocList? list))
            {
                into.Add(list!);
                continue;
            }

            // Regular paragraph: gather lines until blank line or another block opener.
            // Always consume the first line so an unmatched line can never stall the outer loop.
            List<string> paragraphLines = [];
            while (i < lines.Length)
            {
                string current = lines[i].Trim();
                if (paragraphLines.Count > 0 && (current.Length == 0
                    || HorizontalRule().IsMatch(current)
                    || HeadingLine().IsMatch(current)
                    || JdtSourceLine().IsMatch(current)
                    || JdtPlainSourceLine().IsMatch(current)
                    || AtTagLine().IsMatch(current)
                    || BulletLine().IsMatch(lines[i])
                    || OrderedListLine().IsMatch(lines[i])
                    || IsKnownStandaloneTag(current)
                    || IsKnownInlineTag(current)))
                {
                    break;
                }
                if (current.Length == 0)
                {
                    break;
                }
                // Strip leading blockquote marker `>` cleanly.
                if (current.StartsWith('>'))
                {
                    current = current.TrimStart('>').TrimStart();
                }
                paragraphLines.Add(current);
                i++;
            }
            if (paragraphLines.Count > 0)
            {
                string joined = JoinParagraphLines(paragraphLines);
                IReadOnlyList<QuickDocInline> parts = ParseInlines(joined);
                if (parts.Count > 0)
                {
                    into.Add(new QuickDocParagraph(parts));
                }
            }
            if (i == iterationStart)
            {
                i++;
            }
        }
    }

    private static string JoinParagraphLines(List<string> lines)
    {
        StringBuilder result = new();
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            bool hardBreak = line.EndsWith('\\');
            if (hardBreak)
            {
                line = line[..^1].TrimEnd();
            }
            if (result.Length > 0)
            {
                result.Append(hardBreak ? "\n" : " ");
            }
            result.Append(line);
        }
        return result.ToString();
    }

    private static void MergeTagSection(List<QuickDocBlock> into, QuickDocTagSection section)
    {
        if (into.Count > 0 && into[^1] is QuickDocTagSection previous && string.Equals(previous.Label, section.Label, StringComparison.Ordinal))
        {
            into[^1] = new QuickDocTagSection(previous.Label, [.. previous.Items, .. section.Items]);
            return;
        }
        into.Add(section);
    }

    private static bool TryParseAtTag(string[] lines, ref int i, out QuickDocTagSection? section)
    {
        section = null;
        Match match = AtTagLine().Match(lines[i].Trim());
        if (!match.Success)
        {
            return false;
        }
        string rawTag = match.Groups[1].Value.ToLowerInvariant();
        string rest = match.Groups[2].Value.Trim();
        string label = NormalizeTagLabel(rawTag);
        bool hasName = rawTag is "param" or "throws" or "exception";

        i++;
        List<string> continuation = [rest];
        while (i < lines.Length && lines[i].Trim().Length > 0 && !AtTagLine().IsMatch(lines[i].Trim())
            && !HorizontalRule().IsMatch(lines[i].Trim()) && !BulletLine().IsMatch(lines[i]))
        {
            continuation.Add(lines[i].Trim());
            i++;
        }
        string full = string.Join(' ', continuation).Trim();
        QuickDocTagItem item = BuildTagItem(full, hasName);
        section = new QuickDocTagSection(label, [item]);
        return true;
    }

    private static bool TryParseJavadocBulletSection(string[] lines, ref int i, out QuickDocTagSection? section)
    {
        section = null;
        int saved = i;
        Match header = JdtTagBullet().Match(lines[i]);
        if (!header.Success)
        {
            return false;
        }
        int baseIndent = header.Groups[1].Value.Length;
        string rawName = header.Groups[2].Value.Trim().TrimEnd(':');
        if (!IsKnownDocTag(rawName))
        {
            return false;
        }
        string label = NormalizeTagLabel(rawName);
        bool hasNamedItems = label is "Params:" or "Throws:" or "Type Params:";
        List<QuickDocTagItem> items = [];
        string inlineRest = header.Groups[3].Value.Trim();
        if (inlineRest.Length > 0)
        {
            items.Add(BuildTagItem(inlineRest, hasNamedItems));
        }

        i++;
        while (i < lines.Length)
        {
            string line = lines[i];
            if (line.Trim().Length == 0)
            {
                // Skip blank line only if an indented sub-bullet continues this section.
                if (i + 1 < lines.Length && IndentedBullet(lines[i + 1], baseIndent))
                {
                    i++;
                    continue;
                }
                break;
            }
            Match sub = BulletLine().Match(line);
            if (!sub.Success || sub.Groups[1].Value.Length <= baseIndent)
            {
                break;
            }
            string itemText = sub.Groups[2].Value.Trim();
            i++;
            while (i < lines.Length && lines[i].Trim().Length > 0 && !BulletLine().IsMatch(lines[i])
                && !HorizontalRule().IsMatch(lines[i].Trim()) && !JdtSourceLine().IsMatch(lines[i].Trim()))
            {
                itemText += " " + lines[i].Trim();
                i++;
            }
            items.Add(BuildTagItem(itemText, hasNamedItems));
        }

        if (items.Count == 0)
        {
            i = saved;
            return false;
        }
        section = new QuickDocTagSection(label, items);
        return true;
    }

    private static bool IsKnownInlineTag(string trimmed) =>
        InlineTagLine().Match(trimmed) is { Success: true } m && IsKnownDocTag(m.Groups[1].Value.Trim());

    private static bool IsKnownStandaloneTag(string trimmed) =>
        StandaloneTagLine().Match(trimmed) is { Success: true } m && IsKnownDocTag(m.Groups[1].Value.Trim());

    private static bool TryParseStandaloneTagSection(string[] lines, ref int i, out QuickDocTagSection? section)
    {
        section = null;
        int saved = i;
        Match header = StandaloneTagLine().Match(lines[i].Trim());
        if (!header.Success)
        {
            return false;
        }
        string rawName = header.Groups[1].Value.Trim();
        if (!IsKnownDocTag(rawName))
        {
            return false;
        }
        string label = NormalizeTagLabel(rawName);
        bool hasNamedItems = label is "Params:" or "Throws:" or "Type Params:";
        List<QuickDocTagItem> items = [];

        i++;
        while (i < lines.Length)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
            {
                break;
            }
            if (HorizontalRule().IsMatch(trimmed) || HeadingLine().IsMatch(trimmed)
                || JdtSourceLine().IsMatch(trimmed) || JdtPlainSourceLine().IsMatch(trimmed)
                || IsKnownStandaloneTag(trimmed) || IsKnownInlineTag(trimmed) || AtTagLine().IsMatch(trimmed))
            {
                break;
            }
            string itemText = BulletLine().Match(lines[i]) is { Success: true } b ? b.Groups[2].Value.Trim() : trimmed;
            items.Add(BuildTagItem(itemText, hasNamedItems));
            i++;
        }

        if (items.Count == 0)
        {
            i = saved;
            return false;
        }
        section = new QuickDocTagSection(label, items);
        return true;
    }

    private static bool IndentedBullet(string line, int baseIndent) =>
        BulletLine().Match(line) is { Success: true } m && m.Groups[1].Value.Length > baseIndent;

    private static bool TryParseInlineTagLine(string trimmed, out QuickDocTagSection? section)
    {
        section = null;
        Match match = InlineTagLine().Match(trimmed);
        if (!match.Success)
        {
            return false;
        }
        string rawName = match.Groups[1].Value.Trim();
        if (!IsKnownDocTag(rawName))
        {
            return false;
        }
        string label = NormalizeTagLabel(rawName);
        bool hasName = label is "Params:" or "Throws:" or "Type Params:";
        string rest = match.Groups[2].Value.Trim();
        if (rest.Length == 0)
        {
            return false;
        }
        section = new QuickDocTagSection(label, [BuildTagItem(rest, hasName)]);
        return true;
    }

    private static QuickDocTagItem BuildTagItem(string text, bool extractName)
    {
        if (extractName)
        {
            // Match `**name** desc`, `` `name` desc ``, or `name - desc` / `name desc`.
            Match named = NamedTagItem().Match(text);
            if (named.Success)
            {
                string name = (named.Groups[1].Success ? named.Groups[1].Value
                    : named.Groups[2].Success ? named.Groups[2].Value
                    : named.Groups[3].Value).Trim();
                string desc = named.Groups[4].Value.Trim();
                if (desc.StartsWith('-') || desc.StartsWith('–') || desc.StartsWith('—'))
                {
                    desc = desc[1..].TrimStart();
                }
                return new QuickDocTagItem(name, ParseInlines(desc));
            }
        }
        return new QuickDocTagItem(null, ParseInlines(text));
    }

    private static bool TryParseList(string[] lines, ref int i, out QuickDocList? list)
    {
        list = null;
        int saved = i;
        Match bullet = BulletLine().Match(lines[i]);
        Match ordered = bullet.Success ? Match.Empty : OrderedListLine().Match(lines[i]);
        if (!bullet.Success && !ordered.Success)
        {
            return false;
        }
        bool isOrdered = ordered.Success;
        int startNumber = isOrdered && int.TryParse(ordered.Groups[2].Value, out int parsed) ? parsed : 1;
        List<IReadOnlyList<QuickDocInline>> items = [];

        while (i < lines.Length)
        {
            Match m = isOrdered ? OrderedListLine().Match(lines[i]) : BulletLine().Match(lines[i]);
            if (!m.Success)
            {
                break;
            }
            // Stop if a JDT Javadoc tag section starts after items already collected so it renders as a tag section.
            if (items.Count > 0 && !isOrdered && JdtTagBullet().Match(lines[i]) is { Success: true } tag
                && IsKnownDocTag(tag.Groups[2].Value.Trim().TrimEnd(':')))
            {
                break;
            }
            string text = (isOrdered ? m.Groups[3].Value : m.Groups[2].Value).Trim();
            i++;
            while (i < lines.Length && lines[i].Trim().Length > 0 && !BulletLine().IsMatch(lines[i]) && !OrderedListLine().IsMatch(lines[i])
                && !HorizontalRule().IsMatch(lines[i].Trim()) && !JdtSourceLine().IsMatch(lines[i].Trim()))
            {
                text += " " + lines[i].Trim();
                i++;
            }
            IReadOnlyList<QuickDocInline> parts = ParseInlines(text);
            if (parts.Count > 0)
            {
                items.Add(parts);
            }
        }
        if (items.Count == 0)
        {
            i = saved;
            return false;
        }
        list = new QuickDocList(isOrdered, startNumber, items);
        return true;
    }

    private static bool IsKnownDocTag(string name) => name.ToLowerInvariant() switch
    {
        "param" or "params" or "parameter" or "parameters"
            or "return" or "returns"
            or "throws" or "throw" or "exception" or "exceptions"
            or "since" or "see" or "see also" or "author" or "deprecated"
            or "api note" or "impl note" or "implementation note" or "implementation requirements"
            or "type parameters" or "type params" => true,
        _ => false,
    };

    private static string NormalizeTagLabel(string raw) => raw.ToLowerInvariant() switch
    {
        "param" or "params" or "parameter" or "parameters" => "Params:",
        "return" or "returns" => "Returns:",
        "throws" or "throw" or "exception" or "exceptions" => "Throws:",
        "since" => "Since:",
        "see" or "see also" => "See Also:",
        "author" => "Author:",
        "deprecated" => "Deprecated:",
        "api note" => "API Note:",
        "impl note" or "implementation note" => "Impl Note:",
        "implementation requirements" => "Impl Spec:",
        "type parameters" or "type params" => "Type Params:",
        _ => char.ToUpperInvariant(raw[0]) + raw[1..] + ":",
    };

    private static void NormalizeDividers(List<QuickDocBlock> blocks)
    {
        while (blocks.Count > 0 && blocks[0] is QuickDocDivider)
        {
            blocks.RemoveAt(0);
        }
        while (blocks.Count > 0 && blocks[^1] is QuickDocDivider)
        {
            blocks.RemoveAt(blocks.Count - 1);
        }
        for (int i = blocks.Count - 1; i > 0; i--)
        {
            if (blocks[i] is QuickDocDivider && blocks[i - 1] is QuickDocDivider)
            {
                blocks.RemoveAt(i);
            }
        }
    }

    /// <summary>Parses Markdown (and simple Javadoc HTML tags like <c>&lt;code&gt;</c>, <c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>,
    /// <c>&lt;br&gt;</c>, <c>&lt;a&gt;</c>) into rich inline nodes.</summary>
    public static IReadOnlyList<QuickDocInline> ParseInlines(string text, int depth = 0)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        // Strip Markdown images and data URIs, which carry no readable documentation text.
        text = DataUri().Replace(MarkdownImage().Replace(text, string.Empty), string.Empty);
        if (depth > 8)
        {
            return [new QuickDocText(text)];
        }

        List<QuickDocInline> result = [];
        StringBuilder buffer = new();
        int i = 0;

        void Flush()
        {
            if (buffer.Length > 0)
            {
                result.Add(new QuickDocText(buffer.ToString()));
                buffer.Clear();
            }
        }

        while (i < text.Length)
        {
            int inlineStart = i;
            char c = text[i];

            if (c == '\n')
            {
                Flush();
                result.Add(new QuickDocBreak());
                i++;
                continue;
            }

            // Backslash escape for ASCII punctuation / symbols
            if (c == '\\' && i + 1 < text.Length && char.IsAscii(text[i + 1]) && (char.IsPunctuation(text[i + 1]) || char.IsSymbol(text[i + 1])))
            {
                buffer.Append(text[i + 1]);
                i += 2;
                continue;
            }

            // HTML character references (&nbsp;, &lt;, &gt;, &amp;, &#...)
            if (c == '&' && DecodeEntity(text, i, out int entityLength) is { } decoded && entityLength > 0)
            {
                buffer.Append(decoded);
                i += entityLength;
                continue;
            }

            // Inline backtick code span
            if (c == '`')
            {
                int ticks = BacktickRun(text[i..]);
                int close = FindClosingTicks(text, i + ticks, ticks);
                if (close > i + ticks)
                {
                    Flush();
                    string code = text[(i + ticks)..close];
                    if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.AsSpan().ContainsAnyExcept(' '))
                    {
                        code = code[1..^1];
                    }
                    result.Add(new QuickDocCodeSpan(code));
                    i = close + ticks;
                    continue;
                }
            }

            // Simple Javadoc HTML tags (<br>, <code>...</code>, <b>...</b>, <i>...</i>, <a href="...">...</a>)
            if (c == '<' && TryReadHtmlTag(text, i, depth, out QuickDocInline? htmlInline, out int htmlEnd))
            {
                Flush();
                if (htmlInline is not null)
                {
                    result.Add(htmlInline);
                }
                i = htmlEnd;
                continue;
            }

            // Markdown link `[label](target)`
            if (c == '[' && TryReadLink(text, i, depth, out QuickDocLink? link, out int linkEnd))
            {
                Flush();
                result.Add(link!);
                i = linkEnd;
                continue;
            }

            // Strong (`**...**` or `__...__`)
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                string delimiter = c == '*' ? "**" : "__";
                int close = text.IndexOf(delimiter, i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    Flush();
                    result.Add(new QuickDocBold(ParseInlines(text[(i + 2)..close], depth + 1)));
                    i = close + 2;
                    continue;
                }
            }

            // Emphasis (`*...*` or `_..._`)
            if ((c == '*' || c == '_') && (c == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                int close = FindSingleEmphasisClose(text, i + 1, c);
                if (close > i + 1)
                {
                    Flush();
                    result.Add(new QuickDocItalic(ParseInlines(text[(i + 1)..close], depth + 1)));
                    i = close + 1;
                    continue;
                }
            }

            buffer.Append(c);
            i = Math.Max(i + 1, inlineStart + 1);
        }

        Flush();
        return result;
    }

    private static int FindClosingTicks(string text, int from, int ticks)
    {
        int at = text.IndexOf('`', from);
        while (at >= 0)
        {
            int run = 0;
            while (at + run < text.Length && text[at + run] == '`')
            {
                run++;
            }
            if (run == ticks)
            {
                return at;
            }
            at = text.IndexOf('`', at + run);
        }
        return -1;
    }

    private static int FindSingleEmphasisClose(string text, int from, char marker)
    {
        if (from >= text.Length || char.IsWhiteSpace(text[from]))
        {
            return -1;
        }
        for (int i = from; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }
            if (text[i] == '`')
            {
                int ticks = BacktickRun(text[i..]);
                int close = FindClosingTicks(text, i + ticks, ticks);
                if (close > 0)
                {
                    i = close + ticks - 1;
                    continue;
                }
            }
            if (text[i] == marker && !char.IsWhiteSpace(text[i - 1])
                && (marker == '*' || i + 1 == text.Length || !char.IsLetterOrDigit(text[i + 1])))
            {
                return i;
            }
        }
        return -1;
    }

    private static bool TryReadLink(string text, int start, int depth, out QuickDocLink? link, out int end)
    {
        link = null;
        end = start;
        int closeBracket = FindMatchingBracket(text, start);
        if (closeBracket < 0 || closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(')
        {
            return false;
        }
        int closeParen = FindMatchingParen(text, closeBracket + 1);
        if (closeParen < 0)
        {
            return false;
        }
        string label = text[(start + 1)..closeBracket];
        string target = text[(closeBracket + 2)..closeParen].Trim();
        // Strip optional link title `url "title"`
        int space = target.IndexOf(' ');
        if (space > 0 && target[0] != '<')
        {
            target = target[..space];
        }
        target = target.Trim('<', '>');
        link = new QuickDocLink(ParseInlines(label, depth + 1), target);
        end = closeParen + 1;
        return true;
    }

    private static int FindMatchingBracket(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }
            if (text[i] == '[') depth++;
            else if (text[i] == ']' && --depth == 0) return i;
        }
        return -1;
    }

    private static int FindMatchingParen(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static bool TryReadHtmlTag(string text, int start, int depth, out QuickDocInline? inline, out int end)
    {
        inline = null;
        end = start;
        ReadOnlySpan<char> slice = text.AsSpan(start);

        if (HtmlBreakTag().Match(text, start) is { Success: true } br && br.Index == start)
        {
            inline = new QuickDocBreak();
            end = start + br.Length;
            return true;
        }
        if (HtmlCodeTag().Match(text, start) is { Success: true } code && code.Index == start)
        {
            inline = new QuickDocCodeSpan(DecodeEntities(code.Groups[1].Value));
            end = start + code.Length;
            return true;
        }
        if (HtmlBoldTag().Match(text, start) is { Success: true } bold && bold.Index == start)
        {
            inline = new QuickDocBold(ParseInlines(bold.Groups[2].Value, depth + 1));
            end = start + bold.Length;
            return true;
        }
        if (HtmlItalicTag().Match(text, start) is { Success: true } italic && italic.Index == start)
        {
            inline = new QuickDocItalic(ParseInlines(italic.Groups[2].Value, depth + 1));
            end = start + italic.Length;
            return true;
        }
        if (HtmlAnchorTag().Match(text, start) is { Success: true } anchor && anchor.Index == start)
        {
            inline = new QuickDocLink(ParseInlines(anchor.Groups[2].Value, depth + 1), anchor.Groups[1].Value.Trim());
            end = start + anchor.Length;
            return true;
        }
        // Ignore stray `<p>` or `</p>` tags that leak from Javadoc.
        if (slice.StartsWith("<p>", StringComparison.OrdinalIgnoreCase) || slice.StartsWith("</p>", StringComparison.OrdinalIgnoreCase))
        {
            end = start + (slice[1] == '/' ? 4 : 3);
            return true;
        }
        return false;
    }

    private static string? DecodeEntity(string line, int at, out int length)
    {
        const string Nbsp = "&nbsp;";
        if (string.CompareOrdinal(line, at, Nbsp, 0, Nbsp.Length) == 0)
        {
            length = Nbsp.Length;
            return " ";
        }
        return XmlEntities.ReferenceAt(line, at, out length);
    }

    private static string DecodeEntities(string value)
    {
        if (!value.Contains('&'))
        {
            return value;
        }
        StringBuilder decoded = new(value.Length);
        for (int i = 0; i < value.Length;)
        {
            if (value[i] == '&' && DecodeEntity(value, i, out int len) is { } ch)
            {
                decoded.Append(ch);
                i += len;
            }
            else
            {
                decoded.Append(value[i++]);
            }
        }
        return decoded.ToString();
    }

    [GeneratedRegex(@"^(.+?)\s+-\s+([A-Za-z_][\w$.]*(?:\([^)]*\))?)$")]
    private static partial Regex JdtLocalOwner();

    [GeneratedRegex(@"^Source:\s*\*?\[([^\]]+)\]\(([^)\s]+)\)\*?$")]
    private static partial Regex JdtSourceLine();

    [GeneratedRegex(@"^Source:\s*(?:\*|_|__|\*\*)?(.+?)(?:\*|_|__|\*\*)?$")]
    private static partial Regex JdtPlainSourceLine();

    [GeneratedRegex(@"^(?:\*\*|__)?([A-Za-z][A-Za-z ]{1,28}):(?:\*\*|__)?$")]
    private static partial Regex StandaloneTagLine();

    [GeneratedRegex(@"^(?:-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^@([A-Za-z]+)\s+(.+)$")]
    private static partial Regex AtTagLine();

    [GeneratedRegex(@"^(\s*)[*-]\s+\*\*([^*:\n]+?):?\*\*:?\s*(.*)$")]
    private static partial Regex JdtTagBullet();

    [GeneratedRegex(@"^(?:\*\*|__)?([A-Za-z][A-Za-z ]{1,28}):(?:\*\*|__)?\s+(.+)$")]
    private static partial Regex InlineTagLine();

    [GeneratedRegex(@"^(?:\*\*([^*]+?)\*\*|`([^`]+?)`|([A-Za-z_$][\w$.<>]*))(?:\s+|$)(.*)$")]
    private static partial Regex NamedTagItem();

    [GeneratedRegex(@"^(\s*)[*-]\s+(.+)$")]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^(\s*)(\d+)\.\s+(.+)$")]
    private static partial Regex OrderedListLine();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)[ \t]*")]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"\bdata:[\w.+/-]*(?:;[\w.+=-]+)*;base64,[A-Za-z0-9+/=]+")]
    private static partial Regex DataUri();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBreakTag();

    [GeneratedRegex(@"<code>(.*?)</code>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlCodeTag();

    [GeneratedRegex(@"<(b|strong)>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlBoldTag();

    [GeneratedRegex(@"<(i|em)>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlItalicTag();

    [GeneratedRegex(@"<a\s+href=[""']([^""']+)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlAnchorTag();
}
