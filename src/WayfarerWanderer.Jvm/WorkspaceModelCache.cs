using System.Text.Json;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

/// <summary>A workspace model read back from the cache, and whether every file its evaluation read still stands.</summary>
public sealed record CachedWorkspaceModel(WorkspaceSnapshot Snapshot, bool Current);

/// <summary>
/// The last good model of a workspace, kept in the IDE's state (never in the repository) for a warm reopen: the host's
/// own result with the stamps of the files its evaluation read. Read back after trust only. When every stamp still
/// matches, the model is current and no import need run; otherwise it is shown stale while a new import runs. A file
/// written by another version of the cache, or one that does not read, is no cache at all.
/// </summary>
public static class WorkspaceModelCache
{
    public const int Version = 1;

    public const string FileName = "workspace-model.json";

    public static void Write(string file, HostWorkspaceResult result, HostJvm toolingJvm)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(toolingJvm);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Kept(Version, result, toolingJvm), HostProtocol.Json));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception unwritable) when (unwritable is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written only makes the next open colder.
        }
    }

    /// <summary>The cached model of <paramref name="root"/>, or null where none is kept, the folder is not trusted, or
    /// the cache is for another folder or version.</summary>
    public static CachedWorkspaceModel? Read(string file, string root)
    {
        if (!WorkspaceTrust.Trusts(root) || !File.Exists(file))
        {
            return null;
        }
        Kept? kept;
        try
        {
            kept = JsonSerializer.Deserialize<Kept>(File.ReadAllText(file), HostProtocol.Json);
        }
        catch (Exception unreadable) when (unreadable is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
        if (kept is not { Version: Version, Result: { } result, ToolingJvm: { } jvm } || !PathNames.Same(Path.GetFullPath(result.Root), Path.GetFullPath(root)))
        {
            return null;
        }
        WorkspaceSnapshot snapshot;
        try
        {
            snapshot = HostModel.ToSnapshot(result, jvm);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        return new CachedWorkspaceModel(snapshot, snapshot.BuildInputs.Count > 0 && snapshot.BuildInputs.All(Stands));
    }

    /// <summary>Whether a file read by the evaluation is as it was: the same last write and length, or still absent.</summary>
    public static bool Stands(JvmBuildInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        FileInfo file = new(input.Path);
        if (!file.Exists)
        {
            return input.Length < 0;
        }
        return input.Length >= 0 && file.Length == input.Length &&
               new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds() == input.Modified;
    }

    private sealed record Kept(int Version, HostWorkspaceResult? Result, HostJvm? ToolingJvm);
}
