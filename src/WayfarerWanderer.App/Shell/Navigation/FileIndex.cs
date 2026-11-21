using Passenger.Core;
using Passenger.Text;

namespace WayfarerWanderer.App.Shell.Navigation;

/// <summary>Every file under the open folder, by its path from the folder, for Go to File and Search Everywhere, ranked as
/// Passenger's index ranks them (<see cref="FuzzyMatch"/>: the highest score, then the shortest path, then by name). Read
/// whole off the window's thread as a popup opens, so it is never stale: version control, build output, the IDE's and the
/// build tools' own folders, and dependencies are left out, and a linked folder is never walked.</summary>
public sealed class FileIndex
{
    /// <summary>The most files read, so a folder that is really a disk does not hold the popup up.</summary>
    private const int Limit = 200_000;

    /// <summary>Folders no one goes to a file in: version control, the IDE's and the build tools' own, build output and
    /// dependencies.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".idea", ".gradle", ".kotlin", ".vscode", "node_modules", "build", "target", "out", "bin",
    };

    private readonly string[] files;
    private readonly PathLetters[] letters;

    private FileIndex(string root, string[] files)
    {
        Root = root;
        this.files = files;
        letters = [.. files.Select(PathLetters.Of)];
    }

    /// <summary>The folder the paths are from.</summary>
    public string Root { get; }

    /// <summary>Every file, by its path from <see cref="Root"/>, in the walk's order.</summary>
    public IReadOnlyList<string> Files => files;

    /// <summary>The folder's files read now, on the calling thread.</summary>
    public static FileIndex Of(string root, CancellationToken cancel = default)
    {
        List<string> found = [];
        Stack<string> folders = new([root]);
        while (folders.Count > 0 && found.Count < Limit)
        {
            cancel.ThrowIfCancellationRequested();
            string folder = folders.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            List<string> under = [];
            foreach (FileSystemInfo entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
                if (entry is DirectoryInfo)
                {
                    if (!Skipped.Contains(entry.Name))
                    {
                        under.Add(entry.FullName);
                    }
                }
                else if (found.Count < Limit)
                {
                    found.Add(Path.GetRelativePath(root, entry.FullName));
                }
            }
            // Walked in name order: the stack takes the last pushed first.
            for (int i = under.Count - 1; i >= 0; i--)
            {
                folders.Push(under[i]);
            }
        }
        return new FileIndex(root, [.. found]);
    }

    /// <summary>The best matches for a query, best first, as paths from <see cref="Root"/>.</summary>
    public IReadOnlyList<string> Search(string query, int limit = 60)
    {
        string trimmed = query.Trim();
        if (limit <= 0 || trimmed.Length == 0)
        {
            return [];
        }
        ulong wanted = FuzzyMatch.Wanted(trimmed);
        List<(int Score, string Path)> scored = [];
        for (int i = 0; i < files.Length; i++)
        {
            if (FuzzyMatch.Score(trimmed, wanted, files[i], letters[i]) is { } score)
            {
                scored.Add((score, files[i]));
            }
        }
        return [.. scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Path.Length)
            .ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(s => s.Path)];
    }

    /// <summary>A path as a row shows it: from the folder where it is under it, else whole.</summary>
    public static string Shown(string path, string? root) =>
        root is not null && PathNames.IsUnder(path, root) ? Path.GetRelativePath(root, path) : path;
}
