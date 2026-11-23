// From Passenger 8a1df7e4 src/Passenger.App/Containers/ContainerRun.cs, MIT (notices/Passenger/LICENSE).
using Passenger.Core;

namespace WayfarerWanderer.App.Shell;

/// <summary>The state's files that only the person may read on macOS and Linux (the state folder itself, its logs and Local
/// History, which keeps copies of a folder's .env and appsettings files, the development certificate's copy and its
/// password, a run's override and single service's compose file, which name them and the configuration's environment, a
/// file copied out of a container), so from the moment each is made: a folder made 0700, a file 0600 as it is created,
/// never set so after its text is in.</summary>
internal static class OwnFiles
{
    public const UnixFileMode OwnersFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public const UnixFileMode OwnersFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Makes a folder only the person enters (one an earlier run made, put right), before anything is
    /// written in it.</summary>
    public static void Folder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        Directory.CreateDirectory(path, OwnersFolder);
        File.SetUnixFileMode(path, OwnersFolder);
    }

    /// <summary>Makes a folder only the person enters where it is not there yet, with each folder above it made with it;
    /// one there already is kept as it is (<see cref="Tree"/> puts one right once).</summary>
    public static void Made(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        Directory.CreateDirectory(path, OwnersFolder);
    }

    /// <summary>Puts a folder an earlier run made right with all it holds, where its own mode is not yet 0700: each
    /// folder below made 0700 and each file 0600, links passed over, the folder itself last, so one put right is known by
    /// its own mode and walked no more. One not there is left so.</summary>
    public static void Tree(string folder)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(folder) || File.GetUnixFileMode(folder) == OwnersFolder)
        {
            return;
        }
        EnumerationOptions everything = new() { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
        foreach (FileSystemInfo entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", everything))
        {
            UnixFileMode own = entry is DirectoryInfo ? OwnersFolder : OwnersFile;
            if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.UnixFileMode != own)
            {
                entry.UnixFileMode = own;
            }
        }
        File.SetUnixFileMode(folder, OwnersFolder);
    }

    /// <summary>A file only the person reads, opened to append to, made where it is not there yet.</summary>
    public static FileStream Append(string path, FileShare share)
    {
        FileStreamOptions options = new() { Mode = FileMode.Append, Access = FileAccess.Write, Share = share };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnersFile;
        }
        return new FileStream(path, options);
    }

    /// <summary>Writes a file only the person reads, whole (<see cref="AtomicFile"/>): one there before is replaced, never
    /// written into, since it would keep the mode it had.</summary>
    public static void Write(string path, string text) => Write(path, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary><see cref="Write(string, string)"/> of bytes.</summary>
    public static void Write(string path, ReadOnlySpan<byte> bytes) => AtomicFile.Write(path, bytes, OwnersFile);

    /// <summary>A new file only the person reads, opened to be written (one there already refused).</summary>
    public static FileStream Create(string path)
    {
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnersFile;
        }
        return new FileStream(path, options);
    }

    /// <summary>A file another program wrote in such a folder, made only the person's to read too.</summary>
    public static void Keep(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, OwnersFile);
        }
    }
}

/// <summary>A .NET service of a container run in its container, as the debugger and the profiler reach it: its container
/// reached through Passenger's tools (with its caveat, an image run under emulation), its program there (by its id there),
