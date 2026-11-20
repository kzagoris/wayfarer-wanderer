// From Passenger 2044e49b src/Passenger.App/Vcs/GitWatcher.cs, MIT (notices/Passenger/LICENSE): on a FileSystemWatcher of
// its own in place of Passenger's WatchedTree.
using Passenger.Core;

namespace WayfarerWanderer.App.Shell.Vcs;

/// <summary>A Git folder watched for Git's own state moving: a linked worktree's or a submodule's, the repository's above a
/// folder opened below its top, the common folder of a worktree's refs, or the open folder's own .git. A change to HEAD, the
/// index or the refs (files, or a reftable repository's tables) is handed over on the UI thread once changes pause, whatever
/// made it (a terminal's commit, a branch switched elsewhere); what else Git writes there (its objects and logs) is passed
/// over.</summary>
public sealed class GitWatcher : IDisposable
{
    /// <summary>How long changes pause before they are handed over: a commit writes many files at once.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(150);

    private readonly FileSystemWatcher? watcher;
    private readonly Timer pause;
    private int disposed;

    public GitWatcher(string folder)
    {
        Top = folder;
        pause = new Timer(_ => UiThread.Post(() =>
        {
            if (!IsDisposed)
            {
                Moved?.Invoke(this, EventArgs.Empty);
            }
        }));
        try
        {
            watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += (_, e) =>
            {
                Heard(e.OldFullPath);
                Heard(e.FullPath);
            };
            // Changes lost: anything may have moved.
            watcher.Error += (_, _) => pause.Change(Quiet, Timeout.InfiniteTimeSpan);
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            watcher?.Dispose();
            watcher = null;
            IdeErrors.Write($"Git: does not watch {folder}: {error.Message}");
        }
    }

    /// <summary>The Git folder watched.</summary>
    public string Top { get; }

    /// <summary>Whether the system watches it: where it will not, Git is read again only as the window comes in front.</summary>
    public bool IsWatching => watcher is not null;

    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>Raised on the UI thread when HEAD, the index or the refs changed.</summary>
    public event EventHandler? Moved;

    /// <summary>Whether a name in Git's folder is that of a folder its refs are kept in: refs, or a reftable repository's
    /// tables (Git 2.45's reftable, which writes nothing else as a commit or a checkout moves a ref).</summary>
    public static bool HoldsRefs(ReadOnlySpan<char> name) => name is "refs" or "reftable";

    /// <summary>Whether a name in Git's folder says Git's state moved when what it names changes: HEAD, the index, packed
    /// refs, or a folder its refs are kept in (<see cref="HoldsRefs"/>).</summary>
    public static bool Moves(ReadOnlySpan<char> name) => name is "HEAD" or "index" or "packed-refs" || HoldsRefs(name);

    /// <summary>Whether a path in a Git folder says Git's state moved: its first name below the folder does (<see cref="Moves"/>).</summary>
    public static bool MovesAt(string path, string gitFolder)
    {
        ReadOnlySpan<char> parts = PathNames.Below(path, gitFolder);
        return !parts.IsEmpty && Moves(First(parts));
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Heard(e.FullPath);

    private void Heard(string path)
    {
        if (!IsDisposed && MovesAt(path, Top))
        {
            pause.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        watcher?.Dispose();
        pause.Dispose();
    }

    /// <summary>A path's first name.</summary>
    private static ReadOnlySpan<char> First(ReadOnlySpan<char> parts)
    {
        int at = parts.IndexOfAny(PathNames.Separators);
        return at < 0 ? parts : parts[..at];
    }
}
