using System.Text.Json;
using Passenger.Core;
using Passenger.Text;

namespace WayfarerWanderer.App;

internal static class JvmSemanticEdits
{
    public static TextPosition Position(JsonElement position) =>
        new(position.GetProperty("line").GetInt32(), position.GetProperty("character").GetInt32());

    public static TextEdit Edit(TextSnapshot snapshot, JsonElement range, string replacement)
    {
        int Offset(string name)
        {
            var point = Position(range.GetProperty(name));
            if (point.Line < 0 || point.Line >= snapshot.LineCount || point.Column < 0 || point.Column > snapshot.GetLine(point.Line).Length)
                throw new InvalidDataException("The server returned an invalid source position.");
            return snapshot.GetOffset(point);
        }
        int start = Offset("start"), end = Offset("end");
        if (end < start) throw new InvalidDataException("The server returned a reversed edit.");
        return new(start, end - start, replacement);
    }

    public static Dictionary<string, JsonElement> Documents(JsonElement result, string root, Func<string, int?> version)
    {
        Dictionary<string, JsonElement> edits = new(PathNames.Comparer);
        void Add(string uri, JsonElement changes, int? expectedVersion)
        {
            Uri location = new(uri);
            if (!location.IsFile || !PathNames.IsUnder(PathNames.Canonical(location.LocalPath), PathNames.Canonical(root)))
                throw new InvalidDataException("The requested edit leaves this workspace.");
            if (expectedVersion is not null && expectedVersion != version(location.LocalPath))
                throw new InvalidOperationException("The server's edit targets an earlier document version.");
            if (changes.GetArrayLength() > 0) edits.Add(location.LocalPath, changes);
        }

        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("changes", out var changes))
            foreach (var file in changes.EnumerateObject()) Add(file.Name, file.Value, null);
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("documentChanges", out var documentChanges))
            foreach (var file in documentChanges.EnumerateArray())
            {
                if (!file.TryGetProperty("textDocument", out var doc)) throw new InvalidOperationException("This rename requires a file operation.");
                Add(doc.GetProperty("uri").GetString()!, file.GetProperty("edits"),
                    doc.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null);
            }
        if (edits.Count == 0) throw new InvalidOperationException("The language server returned no rename edits.");
        return edits;
    }
}

internal sealed record SemanticFileChange(string Path, FileText Before, string After, byte[] OriginalBytes);

internal static class SemanticFileTransaction
{
    public static void Write(IReadOnlyList<SemanticFileChange> changes, bool undo)
    {
        List<(string Path, byte[] Previous)> written = [];
        try
        {
            foreach (var change in changes)
            {
                byte[] bytes = File.ReadAllBytes(change.Path);
                byte[] renamed = FileText.Encoded(change.After, change.Before.Encoding, change.Before.HasBom, change.Before.CrBreaks);
                if (!bytes.AsSpan().SequenceEqual(undo ? renamed : change.OriginalBytes))
                    throw new IOException($"The file changed: {change.Path}");
                FileText.Restore(change.Path, undo ? change.OriginalBytes : renamed);
                written.Add((change.Path, bytes));
            }
        }
        catch (Exception failure)
        {
            List<Exception> failures = [failure];
            foreach (var (path, bytes) in written.AsEnumerable().Reverse())
                try { FileText.Restore(path, bytes); }
                catch (Exception rollback) { failures.Add(rollback); }
            if (failures.Count > 1) throw new AggregateException("Rename recovery failed.", failures);
            throw;
        }
    }
}
