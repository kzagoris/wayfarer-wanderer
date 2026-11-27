// From Passenger 494009d7 src/Passenger.Languages/Language.cs, MIT (notices/Passenger/LICENSE), cut to Wayfarer's languages.
using Passenger.Editor;
using Passenger.Text;

namespace WayfarerWanderer.App.Shell.Languages;

/// <summary>A file name a language claims, matched without regard to case: a name (<c>gradlew</c>), or one <c>*</c>
/// standing for any run (<c>*.gradle.kts</c>).</summary>
public sealed record FilePattern(string Glob)
{
    private readonly int star = Glob.IndexOf('*', StringComparison.Ordinal);

    public static implicit operator FilePattern(string glob) => new(glob);

    public bool Matches(ReadOnlySpan<char> path)
    {
        ReadOnlySpan<char> name = Path.GetFileName(path);
        return star < 0
            ? name.Equals(Glob, StringComparison.OrdinalIgnoreCase)
            : name.Length >= Glob.Length - 1 && name.StartsWith(Glob.AsSpan(0, star), StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(Glob.AsSpan(star + 1), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The languages Wayfarer colours before any language server answers: its id (VS Code's, as
/// <see cref="LanguageIds"/> lists them), the name the IDE says, the file names it claims, its comments, the TextMate
/// grammar that colours it and the scan that finds its folds (<see cref="FoldScan"/>, XML's and JSON's). A file is in the first language whose pattern its name matches, so names (<c>gradlew</c>)
/// come before extensions; one matching none is plain text. A new language is one entry here, its grammar embedded by
/// the project file and its notice under <c>Shell/Languages/NOTICES.md</c>.</summary>
public sealed class Language
{
    /// <summary>Every language in the order declared, each adding itself as it is made, so this comes before them.</summary>
    private static readonly List<Language> All = [];

    /// <summary>Scopes the grammars here embed under another name than their own grammar's: JSON with comments and JSON
    /// Lines, in a Markdown fence, are read by JSON's grammar.</summary>
    private static readonly (string Scope, string Carried)[] EmbeddedAs =
        [("source.json.comments", "source.json"), ("source.json.lines", "source.json")];

    /// <summary>Grammars carried only for another's includes: a grammar for each version of YAML and one for YAML embedded
    /// in another document, which YAML's own grammar reads by.</summary>
    private static readonly (string Scope, string File)[] Included =
    [
        ("source.yaml.1.0", "yaml-1.0.tmLanguage.json"), ("source.yaml.1.1", "yaml-1.1.tmLanguage.json"),
        ("source.yaml.1.2", "yaml-1.2.tmLanguage.json"), ("source.yaml.1.3", "yaml-1.3.tmLanguage.json"),
        ("source.yaml.embedded", "yaml-embedded.tmLanguage.json"),
    ];

    /// <summary>Each grammar's file by the scope it names, for the grammars that embed others.</summary>
    private static readonly Lazy<Dictionary<string, string>> GrammarFiles = new(() =>
    {
        Dictionary<string, string> files = All.Select(l => l.Grammar).OfType<GrammarFile>()
            .DistinctBy(g => g.Scope).ToDictionary(g => g.Scope, g => g.File, StringComparer.Ordinal);
        foreach ((string scope, string file) in Included)
        {
            files[scope] = file;
        }
        foreach ((string scope, string carried) in EmbeddedAs)
        {
            files[scope] = files[carried];
        }
        return files;
    });

    private Language(string id, string name, IReadOnlyList<FilePattern> patterns, string? line = null,
        (string Start, string End)? block = null, GrammarFile? grammar = null, Func<TextDocument, IFoldFinder>? folds = null)
    {
        Id = id;
        Name = name;
        Patterns = patterns;
        LineComment = line;
        BlockComment = block;
        Grammar = grammar;
        Folds = folds;
        All.Add(this);
    }

    public static readonly Language PlainText = new("plaintext", "Text", ["*.txt", "*.text", "*.log"]);

    public static readonly Language Ignore = new("ignore", "Ignore", [".*ignore", "*.gitignore"], line: "#");

    /// <summary>Git's attributes files, coloured by Passenger's own grammar: a pattern, then its attributes.</summary>
    public static readonly Language GitAttributes = new("gitattributes", "Git Attributes", [".gitattributes"], line: "#",
        grammar: new("source.gitattributes", "gitattributes.tmLanguage.json"));

    public static readonly Language Java = new("java", "Java", ["*.java"], line: "//", block: ("/*", "*/"),
        grammar: new("source.java", "java.tmLanguage.json"));

    /// <summary>Kotlin, its scripts too (a Gradle build's <c>.gradle.kts</c>).</summary>
    public static readonly Language Kotlin = new("kotlin", "Kotlin", ["*.kt", "*.kts"], line: "//", block: ("/*", "*/"),
        grammar: new("source.kotlin", "Kotlin.tmLanguage.json"));

    /// <summary>Groovy, Gradle's Groovy builds (<c>build.gradle</c>, <c>settings.gradle</c>) and Jenkins pipelines too.</summary>
    public static readonly Language Groovy = new("groovy", "Groovy", ["*.groovy", "*.gradle", "*.gvy", "Jenkinsfile"],
        line: "//", block: ("/*", "*/"), grammar: new("source.groovy", "groovy.tmLanguage.json"));

    /// <summary>XML: Maven's POMs and settings, IntelliJ's modules, JavaFX's layouts and the rest.</summary>
    public static readonly Language Xml = new("xml", "XML",
        ["*.xml", "*.pom", "*.iml", "*.xsd", "*.xsl", "*.xslt", "*.wsdl", "*.fxml", "*.svg", "*.plist"],
        block: ("<!--", "-->"), grammar: new("text.xml", "xml.tmLanguage.json", Tags.Xml), folds: FoldScan.Xml);

    public static readonly Language Json = new("json", "JSON", ["*.json", "*.jsonc"], line: "//", block: ("/*", "*/"),
        grammar: new("source.json", "JSON.tmLanguage.json"), folds: FoldScan.Json);

    public static readonly Language Yaml = new("yaml", "YAML", ["*.yml", "*.yaml"], line: "#",
        grammar: new("source.yaml", "yaml.tmLanguage.json"));

    /// <summary>Java's properties files (<c>gradle.properties</c>, Spring's <c>application.properties</c>), an
    /// .editorconfig and INI files, coloured as VS Code colours them, by the INI grammar.</summary>
    public static readonly Language Properties = new("properties", "Properties", ["*.properties", ".editorconfig", "*.ini", "*.cfg"],
        line: "#", grammar: new("source.ini", "ini.tmLanguage.json"));

    public static readonly Language Markdown = new("markdown", "Markdown", ["*.md", "*.markdown"], block: ("<!--", "-->"),
        grammar: new("text.html.markdown", "markdown.tmLanguage.json"));

    /// <summary>Shell scripts, the Gradle and Maven wrappers' among them.</summary>
    public static readonly Language Shell = new("shellscript", "Shell", ["*.sh", "*.bash", "*.zsh", "gradlew", "mvnw"], line: "#",
        grammar: new("source.shell", "shell-unix-bash.tmLanguage.json"));

    /// <summary>VS Code's id for the language, as a theme names a language it colours.</summary>
    public string Id { get; }

    /// <summary>The language as the status bar and the tools name it.</summary>
    public string Name { get; }

    public string? LineComment { get; }

    /// <summary>The block comment's two ends, or null for a language with none.</summary>
    public (string Start, string End)? BlockComment { get; }

    /// <summary>The file names it claims, though a language declared before it may claim a file first.</summary>
    public IReadOnlyList<FilePattern> Patterns { get; }

    /// <summary>Every language, in the order declared.</summary>
    public static IReadOnlyList<Language> Every => All;

    internal GrammarFile? Grammar { get; }

    /// <summary>What scans a file of the language for its folds; null for a language with none.</summary>
    internal Func<TextDocument, IFoldFinder>? Folds { get; }

    /// <summary>The language a file is in, by its name.</summary>
    public static Language ForPath(string path)
    {
        foreach (Language language in All)
        {
            foreach (FilePattern pattern in language.Patterns)
            {
                if (pattern.Matches(path))
                {
                    return language;
                }
            }
        }
        return PlainText;
    }

    public override string ToString() => Name;

    /// <summary>The file a grammar a language's grammar embeds is carried in, by its scope; null for one not carried
    /// (a Markdown fence in a language with no grammar here stays code).</summary>
    internal static string? GrammarFileOf(string scope) => GrammarFiles.Value.GetValueOrDefault(scope);
}
