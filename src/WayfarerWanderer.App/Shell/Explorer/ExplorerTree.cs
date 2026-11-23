// From Passenger 8a1df7e4 src/Passenger.App/Explorer/ExplorerTree.cs, MIT (notices/Passenger/LICENSE): its File System view,
// with the solution, project, dependency, nesting and version control parts cut. Wayfarer marks the folders the
// JVM import names (modules and their source roots) in their place.
using Avalonia;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App.Shell.Explorer;

/// <summary>What a row of the Explorer stands for.</summary>
public enum NodeKind
{
    Folder,
    File,
}

/// <summary>What the JVM import says a folder is, which its row shows beside its name; none for most folders.</summary>
public enum JvmFolder
{
    None,

    /// <summary>A module's folder, a Maven module or a Gradle project.</summary>
    Module,

    /// <summary>A folder of a module's sources, tests or resources.</summary>
    SourceRoot,
}

/// <summary>The Explorer's tree: the open folder as it is on disk, its modules and source roots marked as the build's import
/// read them. Flattened into the rows currently visible, so a virtualizing list draws only what is on screen; a node's
/// children are made the first time it opens.</summary>
public sealed class ExplorerTree
{
    /// <summary>Never listed: version control's own store, which nobody edits by hand (and the system's own files,
    /// <see cref="PathNames.IsSystemFile"/>).</summary>
    private const string GitStore = ".git";

    /// <summary>The import's folders, by canonical path: what each is and the words its row shows.</summary>
    private Dictionary<string, (JvmFolder Kind, string Detail)> marked = new(PathNames.Comparer);

    /// <summary>The folders the build writes, dimmed with all they hold: each module's own target or build folder.</summary>
    private HashSet<string> output = new(PathNames.Comparer);

    public TreeRows<ExplorerNode> Rows { get; } = [];

    public ExplorerNode? Root { get; private set; }

    /// <summary>The paths cut and waiting to be pasted, by canonical path, whose rows (and all under them) are dimmed.</summary>
    private HashSet<string> cut = new(PathNames.Comparer);

    /// <summary>Dims what was cut and waits to be pasted, and what it holds.</summary>
    public void SetCut(IReadOnlyList<string> paths)
    {
        cut = new HashSet<string>(paths.Select(PathNames.Canonical), PathNames.Comparer);
        if (Root is not null)
        {
            foreach (ExplorerNode row in Loaded(Root, _ => true))
            {
                Mark(row);
            }
        }
    }

    private void Mark(ExplorerNode node) => node.IsCut = cut.Count > 0 && node.Path is { } at && IsCut(at);

    /// <summary>Whether a path was cut or is inside a folder that was, found by its own folders: every row is marked, and
    /// compared with each of thousands of files cut, each row would cost them all.</summary>
    private bool IsCut(string path)
    {
        for (string? at = path; at is not null; at = Path.GetDirectoryName(at))
        {
            if (cut.Contains(at))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A folder's files, the snapshot (when the import has read one) naming its modules and source roots.</summary>
    public void Load(string folder, WorkspaceSnapshot? snapshot)
    {
        Take(snapshot);
        Show(Folder(PathNames.Canonical(folder), 0, dimmed: false));
    }

    /// <summary>The folder shown read again from disk, every row made anew and closed but the top one, its marks kept.</summary>
    public void Reread()
    {
        if (Root?.Path is { } folder)
        {
            Show(Folder(folder, 0, dimmed: false));
        }
    }

    /// <summary>The import's modules and source roots, which the rows made after this mark: <see cref="Reread"/> shows
    /// them on the rows already made.</summary>
    public void Take(WorkspaceSnapshot? snapshot)
    {
        marked = new(PathNames.Comparer);
        output = new(PathNames.Comparer);
        if (snapshot is null)
        {
            return;
        }
        foreach (JvmModule module in snapshot.Modules)
        {
            string root = PathNames.Canonical(module.Root);
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
            marked[root] = (JvmFolder.Module, string.Equals(name, module.Name, StringComparison.Ordinal) ? "module" : module.Name);
            if (snapshot.BuildSystem == JvmBuildSystem.Idea)
            {
                if (!string.IsNullOrEmpty(snapshot.OutputRoot))
                {
                    output.Add(snapshot.OutputRoot);
                }
                if (module.Classpath.Runtime.FirstOrDefault() is { } classes)
                {
                    output.Add(classes);
                }
            }
            else
            {
                output.Add(Path.Combine(root, snapshot.BuildSystem == JvmBuildSystem.Maven ? "target" : "build"));
                if (snapshot.BuildSystem == JvmBuildSystem.Gradle)
                {
                    output.Add(Path.Combine(root, ".gradle"));
                }
            }
            foreach (JvmSourceRoot source in module.SourceRoots)
            {
                marked.TryAdd(PathNames.Canonical(source.Path), (JvmFolder.SourceRoot, Words(source.Kind)));
            }
        }
    }

    /// <summary>What a source root's row says it holds.</summary>
    internal static string Words(JvmSourceKind kind) => kind switch
    {
        JvmSourceKind.MainJava or JvmSourceKind.MainKotlin => "sources",
        JvmSourceKind.TestJava or JvmSourceKind.TestKotlin => "tests",
        JvmSourceKind.MainResources => "resources",
        JvmSourceKind.TestResources => "test resources",
        JvmSourceKind.Generated => "generated",
        _ => string.Empty,
    };

    /// <summary>Whether the build writes a folder (or it is inside one), so its rows are dimmed and the filter passes over it.</summary>
    public bool IsOutput(string path)
    {
        foreach (string folder in output)
        {
            if (PathNames.Within(path, folder))
            {
                return true;
            }
        }
        return false;
    }

    public void Toggle(ExplorerNode node)
    {
        if (node.IsExpanded)
        {
            Collapse(node);
        }
        else
        {
            Expand(node);
        }
    }

    public void Expand(ExplorerNode node)
    {
        if (!node.HasChildren || node.IsExpanded)
        {
            return;
        }
        node.IsExpanded = true;
        Rows.Open(node, [.. Visible(node)]);
    }

    public void Collapse(ExplorerNode node)
    {
        if (!node.IsExpanded)
        {
            return;
        }
        node.IsExpanded = false;
        Rows.Close(node);
    }

    /// <summary>Closes every row but the top one, the rows out of sight under a closed one too, so none opens again with
    /// its own rows open.</summary>
    public void CollapseAll()
    {
        if (Root is null)
        {
            return;
        }
        foreach (ExplorerNode row in Rows.Reverse().Where(r => r != Root && r.IsExpanded).ToList())
        {
            Collapse(row);
        }
        foreach (ExplorerNode hidden in Loaded(Root, n => n != Root && n.IsExpanded))
        {
            hidden.IsExpanded = false;
        }
    }

    /// <summary>Reads again only the folders that hold these, keeping the rows still there as they were (open, chosen), and
    /// puts the rows that came or went in their places.</summary>
    public void RefreshFolders(IEnumerable<string> folders)
    {
        if (Root is null)
        {
            return;
        }
        HashSet<string> changed = new(folders, PathNames.Comparer);
        List<ExplorerNode> reloaded = [];
        foreach (ExplorerNode node in Loaded(Root, n => n.Kind == NodeKind.Folder && n.Path is { } folder && changed.Contains(folder)))
        {
            if (node.Reload())
            {
                reloaded.Add(node);
            }
        }
        ShowAgain(reloaded);
    }

    /// <summary>The rows under each node read again made anew from it as it now is, where it is shown open: those still
    /// there kept, the rest taken out or put in, and every other row left as it was.</summary>
    private void ShowAgain(List<ExplorerNode> reloaded)
    {
        foreach (ExplorerNode node in reloaded.Where(n => n.IsExpanded))
        {
            Rows.ShowUnder(node, [.. Visible(node)]);
        }
    }

    /// <summary>Those of the node and the nodes under it whose children have been read that are picked, all found before
    /// any is read again, by a walk that makes nothing for the nodes it passes.</summary>
    private static List<ExplorerNode> Loaded(ExplorerNode node, Func<ExplorerNode, bool> picked)
    {
        List<ExplorerNode> found = [];
        Walk(node);
        return found;

        void Walk(ExplorerNode at)
        {
            if (picked(at))
            {
                found.Add(at);
            }
            if (at.LoadedChildren is { } children)
            {
                foreach (ExplorerNode child in children)
                {
                    Walk(child);
                }
            }
        }
    }

    /// <summary>Opens every node down to <paramref name="path"/> and returns its row, so a file can be shown where it lives.</summary>
    public ExplorerNode? Reveal(string path) => Find(PathNames.Canonical(path), Expand);

    /// <summary>A file's node by its canonical path (<see cref="PathNames.Canonical"/>), each node above it handed to
    /// <paramref name="through"/> before its children are read; null where the folder does not hold it.</summary>
    private ExplorerNode? Find(string full, Action<ExplorerNode> through)
    {
        if (Root is null)
        {
            return null;
        }
        ExplorerNode node = Root;
        while (!PathNames.Same(node.Path!, full))
        {
            through(node);
            if (node.Children?.FirstOrDefault(c => PathNames.Same(c.Path!, full) || c.Contains(full)) is not { } next)
            {
                return null;
            }
            node = next;
        }
        return node;
    }

    /// <summary>The rows the Explorer's filter lets through: the files it matched and every node above one; null while
    /// none is set, every row shown as its holder's opening shows it.</summary>
    private HashSet<ExplorerNode>? passing;

    /// <summary>The nodes open as the filter was first set, open again (and only they) once it is cleared.</summary>
    private HashSet<ExplorerNode>? openBefore;

    /// <summary>Whether the filter narrows the rows.</summary>
    public bool IsFiltered => passing is not null;

    /// <summary>Narrows the rows to these files and the folders above them, those opened: the Explorer's filter, given the
    /// canonical paths of the files whose names match it. Rows opened or closed while it is set show only what it lets
    /// through.</summary>
    public void Filter(IEnumerable<string> canonical)
    {
        if (Root is null)
        {
            return;
        }
        openBefore ??= [.. Loaded(Root, n => n.IsExpanded)];
        HashSet<ExplorerNode> through = new(ReferenceEqualityComparer.Instance);
        passing = new(ReferenceEqualityComparer.Instance) { Root };
        foreach (string file in canonical)
        {
            List<ExplorerNode> above = [];
            if (Find(file, above.Add) is { IsFile: true } found)
            {
                passing.Add(found);
                passing.UnionWith(above);
                through.UnionWith(above);
            }
        }
        foreach (ExplorerNode node in through.Where(n => n.HasChildren))
        {
            node.IsExpanded = true;
        }
        ShowAll();
    }

    /// <summary>Every row again, open as it was before the filter was set.</summary>
    public void Unfilter()
    {
        if (Root is null || passing is null)
        {
            passing = null;
            openBefore = null;
            return;
        }
        HashSet<ExplorerNode> open = openBefore ?? new(ReferenceEqualityComparer.Instance);
        passing = null;
        openBefore = null;
        foreach (ExplorerNode node in Loaded(Root, _ => true))
        {
            node.IsExpanded = open.Contains(node);
        }
        ShowAll();
    }

    /// <summary>The root and the rows its opening shows, as the filter lets them through.</summary>
    private void ShowAll() => Rows.Show([Root!, .. Root!.IsExpanded ? Visible(Root) : Enumerable.Empty<ExplorerNode>()]);

    private void Show(ExplorerNode root)
    {
        passing = null;
        openBefore = null;
        Rows.Clear();
        Root = root;
        Rows.Add(root);
        Expand(root);
    }

    /// <summary>Nothing shown, as before a folder opens.</summary>
    public void Clear()
    {
        passing = null;
        openBefore = null;
        Rows.Clear();
        Root = null;
        marked = new(PathNames.Comparer);
        output = new(PathNames.Comparer);
    }

    private ExplorerNode Folder(string path, int depth, bool dimmed)
    {
        (JvmFolder kind, string? detail) = marked.TryGetValue(path, out (JvmFolder Kind, string Detail) mark) ? mark : (JvmFolder.None, null);
        bool passed = dimmed || IsOutput(path);
        ExplorerNode folder = new(NodeKind.Folder, Path.GetFileName(path) is { Length: > 0 } name ? name : path, path, depth, passed,
            node => Contents(path, node.Depth + 1, node.IsDimmed), detail)
        {
            JvmFolder = kind,
        };
        Mark(folder);
        return folder;
    }

    /// <summary>A folder's entries, folders first, each by name; a save's temporary file left out (nothing reads the folder
    /// again once the save is done), and version control's store.</summary>
    private List<ExplorerNode> Contents(string folder, int depth, bool dimmed)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Where(e => e.Name != GitStore && !(e is FileInfo && (FileText.IsSaving(e.Name) || PathNames.IsSystemFile(e.Name))))
                .OrderBy(e => e is FileInfo)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return [];
        }
        return [.. entries.Select(entry =>
        {
            if (entry is DirectoryInfo)
            {
                return Folder(entry.FullName, depth, dimmed);
            }
            ExplorerNode file = new(NodeKind.File, entry.Name, entry.FullName, depth, dimmed, null);
            Mark(file);
            return file;
        })];
    }

    /// <summary>A node's descendants as rows, in order, through every node already open, those the filter lets through.</summary>
    private IEnumerable<ExplorerNode> Visible(ExplorerNode node) => TreeRows<ExplorerNode>.Shown(Passing(node.Children), child => Passing(child.Children));

    private IEnumerable<ExplorerNode> Passing(List<ExplorerNode>? rows) => passing is null ? rows ?? [] : (rows ?? []).Where(passing.Contains);
}

/// <summary>One row of the Explorer: a folder or a file.</summary>
public sealed class ExplorerNode : Observable, ITreeRow
{
    private readonly Func<ExplorerNode, List<ExplorerNode>>? load;
    private List<ExplorerNode>? children;
    private bool isExpanded;

    public ExplorerNode(NodeKind kind, string name, string? path, int depth, bool dimmed, Func<ExplorerNode, List<ExplorerNode>>? load, string? detail = null)
    {
        Kind = kind;
        Name = name;
        Path = path;
        Depth = depth;
        IsDimmed = dimmed;
        Detail = detail;
        this.load = load;
    }

    public NodeKind Kind { get; }
    public string Name { get; }
    public string? Path { get; }
    public int Depth { get; }
    public bool IsDimmed { get; }

    /// <summary>A quieter word beside the name: what the import says a folder is (a module's name, "sources", "tests").</summary>
    public string? Detail { get; }

    /// <summary>What the JVM import says the folder is, which its icon shows.</summary>
    public JvmFolder JvmFolder { get; init; }

    public bool HasChildren => load is not null;

    bool ITreeRow.Expands => HasChildren;

    public bool IsFile => Kind == NodeKind.File;
    public List<ExplorerNode>? Children => load is null ? null : children ??= load(this);

    /// <summary>The children once read, without reading them.</summary>
    internal List<ExplorerNode>? LoadedChildren => children;

    /// <summary>Reads the children again, keeping each one still there (with what is open under it); whether any came or went.</summary>
    internal bool Reload()
    {
        if (load is null || children is null)
        {
            return false;
        }
        Dictionary<string, ExplorerNode> before = children.ToDictionary(Identity);
        List<ExplorerNode> fresh = [.. load(this).Select(c => before.TryGetValue(Identity(c), out ExplorerNode? kept) ? kept : c)];
        bool same = fresh.Count == children.Count && fresh.SequenceEqual(children);
        children = fresh;
        return !same;
    }

    /// <summary>What makes a row the same row when its folder is read again.</summary>
    private static string Identity(ExplorerNode node) => $"{node.Kind}/{node.Name}/{node.Path}/{node.Detail}/{node.JvmFolder}";

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (Set(ref isExpanded, value))
            {
                Raise(nameof(Icon));
            }
        }
    }

    /// <summary>Cut and waiting to be pasted, which dims it.</summary>
    public bool IsCut
    {
        get => isCut;
        set
        {
            if (Set(ref isCut, value))
            {
                Raise(nameof(Opacity));
            }
        }
    }

    private bool isCut;

    /// <summary>The top row: the folder opened, which is never moved, renamed or deleted from inside itself.</summary>
    public bool IsTop => Depth == 0;

    /// <summary>The folder on disk what is pasted, dropped or made new from this row goes into: a folder's own, or for a
    /// file, the folder holding it.</summary>
    public string? Into => Kind == NodeKind.Folder ? Path : Path is { } file ? System.IO.Path.GetDirectoryName(file) : null;

    /// <summary>A file or folder that can be cut, copied, dragged, renamed or deleted: any but the top one.</summary>
    public bool IsMovable => !IsTop && Path is not null;

    public Thickness Indent => new(Depth * Gaps.Indent, 0, 0, 0);
    public double Opacity => IsDimmed || IsCut ? ListRow.Dimmed : 1;
    public Geometry Icon => FileIcons.For(this);
    public IBrush IconBrush => FileIcons.BrushFor(this);

    /// <summary>Whether a path lies inside this node's folder.</summary>
    public bool Contains(string path) => Kind == NodeKind.Folder && Path is { } folder && PathNames.IsUnder(path, folder);
}
