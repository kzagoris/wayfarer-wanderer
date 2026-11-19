using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Text;

namespace WayfarerWanderer.App;

/// <summary>For ordinary named static functions, audit every possible spelling in the project.
/// A server's rename establishes identity for its edits. For any omitted spelling, require an
/// unambiguous source declaration from its owning server. An uncertain result blocks the edit.</summary>
internal sealed class MixedMethodRename
{
    private readonly string root;
    private readonly Dictionary<string, string> texts = new(PathNames.Comparer);

    public MixedMethodRename(string root)
    {
        this.root = root;
        foreach (string path in Sources()) texts.Add(path, File.ReadAllText(path));
    }

    public bool Mixed => texts.Keys.Any(p => p.EndsWith(".kt", StringComparison.OrdinalIgnoreCase)) &&
        texts.Keys.Any(p => p.EndsWith(".java", StringComparison.OrdinalIgnoreCase));

    public void Revalidate()
    {
        var current = Sources().ToHashSet(PathNames.Comparer);
        if (!current.SetEquals(texts.Keys) || texts.Any(kv => File.ReadAllText(kv.Key) != kv.Value))
            throw new IOException("Project sources changed. Request a new rename preview.");
    }

    private IEnumerable<string> Sources()
    {
        Stack<string> dirs = new();
        dirs.Push(root);
        while (dirs.TryPop(out var dir))
        {
            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                if (Path.GetFileName(child) is ".git" or ".gradle" or ".idea") continue;
                if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("Rename coverage is uncertain in a linked source tree.");
                dirs.Push(child);
            }
            foreach (var path in Directory.EnumerateFiles(dir))
                if (Path.GetExtension(path) is ".java" or ".kt") yield return path;
        }
    }

    public async Task<string?> CoordinateAsync(Dictionary<string, List<TextEdit>> edits, string newName,
        Func<string, TextPosition, Task<JsonElement>> definition)
    {
        var declarations = new List<(string Path, int Offset, string Name)>();
        foreach (var (path, replacements) in edits)
        {
            if (!texts.TryGetValue(path, out var text)) return "The backend edited a file outside the source inventory.";
            var doc = TextSnapshot.From(text);
            foreach (var edit in replacements)
            {
                string name = doc.GetText(edit.Start, edit.Length);
                if (edit.Text != newName || !Regex.IsMatch(name, @"^[a-z][a-zA-Z0-9_]*$") || !Regex.IsMatch(newName, @"^[a-z][a-zA-Z0-9_]*$"))
                    return "The backend requested a transformation beyond a plain method rename.";
                var at = doc.GetPosition(edit.Start);
                string before = doc.GetLineText(at.Line)[..at.Column];
                string after = doc.GetLineText(at.Line)[(at.Column + name.Length)..];
                if (after.TrimStart().StartsWith('(') &&
                    (Regex.IsMatch(before, @"^fun\s+$") ||
                     Regex.IsMatch(before, @"^\s*(?:(?:public|private|protected|final|synchronized)\s+)*static\s+[\w.$<>?\[\],]+\s+$")))
                    declarations.Add((path, edit.Start, name));
            }
        }
        if (declarations.Count != 1) return "Coverage is available for one ordinary Java static method or Kotlin top-level function.";
        var target = declarations[0];
        if (Regex.IsMatch(target.Name, @"^(?:get|set|is)(?:$|[A-Z])") || Regex.IsMatch(newName, @"^(?:get|set|is)(?:$|[A-Z])"))
            return "Accessor syntax can use an alternate spelling of this method.";
        var token = new Regex(@"(?<![\w$])" + Regex.Escape(target.Name) + @"(?![\w$])");
        foreach (var (path, text) in texts)
        {
            if (text.Contains("\\u", StringComparison.Ordinal) || text.Contains("@JvmName", StringComparison.Ordinal))
                return "Escaped identifiers or JVM name mappings require a broader coverage check.";
            var doc = TextSnapshot.From(text);
            foreach (Match match in token.Matches(text))
            {
                if (edits.TryGetValue(path, out var existing) && existing.Any(e => e.Start == match.Index && e.Length == match.Length)) continue;
                var result = await definition(path, doc.GetPosition(match.Index));
                var resolved = result.ValueKind == JsonValueKind.Array ? result.EnumerateArray().ToArray() :
                    result.ValueKind == JsonValueKind.Object ? [result] : Array.Empty<JsonElement>();
                if (resolved.Length != 1) return Unresolved(path, doc.GetPosition(match.Index));
                JsonElement loc = resolved[0];
                string uri = (loc.TryGetProperty("targetUri", out var targetUri) ? targetUri : loc.GetProperty("uri")).GetString()!;
                var dest = new Uri(uri);
                if (!dest.IsFile || !texts.TryGetValue(dest.LocalPath, out var declared)) return Unresolved(path, doc.GetPosition(match.Index));
                var range = loc.TryGetProperty("targetSelectionRange", out var selected) ? selected : loc.GetProperty("range");
                var decl = TextSnapshot.From(declared);
                var bounds = JvmSemanticEdits.Edit(decl, range, "");
                // Selection ranges from JDT and Kotlin point at the identifier. A broader range
                // cannot distinguish two declarations when one is nested inside the other.
                if (bounds.Length != target.Name.Length || decl.GetText(bounds.Start, bounds.Length) != target.Name)
                    return Unresolved(path, doc.GetPosition(match.Index));
                if (PathNames.Same(dest.LocalPath, target.Path) && bounds.Start == target.Offset)
                {
                    if (existing == null) edits[path] = existing = [];
                    existing.Add(new(match.Index, match.Length, newName));
                }
            }
        }
        return null;
    }

    private string Unresolved(string path, TextPosition at) =>
        $"Reference identity is uncertain at {Path.GetRelativePath(root, path)} ({at.Line + 1}, {at.Column + 1}).";
}
