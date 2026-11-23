// From Passenger 2044e49b src/Passenger.App/Explorer/ExplorerView.axaml.cs, MIT (notices/Passenger/LICENSE): the Solution
// view, single-click opening and file nesting cut. Wayfarer reads the folder's files for the filter itself, as it has
// no index, and watches the folder so the rows follow what the build and other programs write.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.Core;
using Passenger.Editor;
using WayfarerWanderer.App.Shell.Docking;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App.Shell.Explorer;

/// <summary>Files and folders dragged onto a row: moved into the folder it stands for, or copied with the platform's
/// copy key held (Option on the Mac, Ctrl elsewhere).</summary>
public sealed record ExplorerDrop(IReadOnlyList<ExplorerNode> Items, ExplorerNode Target, bool Copy);

public partial class ExplorerView : UserControl
{
    private readonly DropOverlay drop = new();
    private readonly DragGesture<IReadOnlyList<ExplorerNode>> drag;
    private bool copyHeld;

    /// <summary>Under the rows, what the last file operation did or why it could not, until the next one.</summary>
    private readonly TextBlock said = new() { Classes = { StyleClasses.Muted }, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };

    /// <summary>How long typing in the filter pauses before the rows follow it.</summary>
    private static readonly TimeSpan FilterPause = TimeSpan.FromMilliseconds(150);

    /// <summary>How long the folder stays quiet on disk before its rows are read again.</summary>
    private static readonly TimeSpan WatchPause = TimeSpan.FromMilliseconds(300);

    /// <summary>The most files the filter reads a folder's names from: past these, a filter finds only those read.</summary>
    public const int MostIndexed = 200_000;

    private readonly TextBox filter = SearchBox.Finding("Filter files");
    private readonly Border filterBar;
    private readonly Debounce filtering;

    /// <summary>Under the rows, how many more files a filter matched than the tree shows.</summary>
    private readonly TextBlock more = new() { Classes = { StyleClasses.EmptyText }, IsVisible = false };

    /// <summary>Filters asked for, numbering each: only the newest one's answer lands.</summary>
    private int asked;

    /// <summary>The filter whose answer landed last, by its number.</summary>
    private int answered;

    /// <summary>The newest answer landed, its files by full path, which the rows are narrowed to again as they are made
    /// anew; null while the filter is empty.</summary>
    private ExplorerFilter.Matches? matched;

    /// <summary>Down or Enter pressed in the filter before its answer landed, which then chooses the first file shown.</summary>
    private bool chooseFirst;

    private string? folder;
    private FileSystemWatcher? watcher;

    /// <summary>The folders changed on disk since the rows last followed them, by canonical path.</summary>
    private readonly HashSet<string> changedFolders = new(PathNames.Comparer);
    private readonly DispatcherTimer watchQuiet;

    /// <summary>The folder's files by their paths from its root, read off the window's thread; null until read, and again
    /// once files come or go.</summary>
    private IReadOnlyList<string>? indexed;

    public ExplorerView()
    {
        InitializeComponent();
        filter.Margin = new Thickness(6, 0, 4, 0);
        filter.PlaceholderText = "Filter files";
        filterBar = ToolWindows.ToolBars.Top(filter);
        filterBar.IsVisible = false;
        Body.Children.Add(filterBar);
        more.Margin = new Thickness(16, 6);
        said.Margin = new Thickness(16, 4, 16, 6);
        StackPanel foot = new() { Children = { more, said } };
        Grid.SetRow(foot, 2);
        Body.Children.Add(foot);
        filtering = new Debounce(FilterPause, MatchAsync);
        filter.TextChanged += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(filter.Text))
            {
                filtering.Stop();
                IdeErrors.Observe(MatchAsync());
            }
            else
            {
                filtering.Restart();
            }
        };
        filter.KeyDown += OnFilterKey;
        Rows.ItemsSource = Tree.Rows;
        ReusedRows.Use(Rows, Row);
        TreeKeys.Attach<ExplorerNode>(Rows, Tree.Toggle, node =>
        {
            if (!node.IsFile || node.Path is not { } path)
            {
                return false;
            }
            FileOpened?.Invoke(this, path);
            return true;
        });
        Surface.Children.Add(drop);
        drag = new DragGesture<IReadOnlyList<ExplorerNode>>(Surface, drop, DropZoneAt, (items, at) =>
        {
            if (TargetAt(items, at) is { } target)
            {
                Dropped?.Invoke(this, new ExplorerDrop(items, target, copyHeld));
            }
        });
        // Seen before a row takes the press for its selection, which the drag leaves as it is.
        Rows.AddHandler(PointerPressedEvent, OnRowsPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Rows.AddHandler(PointerMovedEvent, OnRowsMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Rows.AddHandler(PointerReleasedEvent, OnRowsReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Rows.ContextRequested += (_, e) =>
        {
            if (Rows.SelectedItem is ExplorerNode node && OpenMenuOn(node, e.TryGetPosition(Rows, out Point _)))
            {
                e.Handled = true;
            }
        };
        watchQuiet = new DispatcherTimer { Interval = WatchPause };
        watchQuiet.Tick += (_, _) =>
        {
            watchQuiet.Stop();
            FollowDisk();
        };
        DetachedFromVisualTree += (_, _) => StopWatching();
    }

    /// <summary>A file chosen with a double-click or Enter.</summary>
    public event EventHandler<string>? FileOpened;

    /// <summary>A row's right-click menu, which the window makes of its actions run on that row; none for a row with
    /// nothing to do.</summary>
    public Func<ExplorerNode, IReadOnlyList<Control>?>? MenuFor { get; set; }

    /// <summary>The row's right-click menu while it is open.</summary>
    public ContextMenu? OpenMenu { get; private set; }

    /// <summary>Files and folders dragged onto a folder or a file's folder.</summary>
    public event EventHandler<ExplorerDrop>? Dropped;

    /// <summary>Every row chosen, in the order the list holds them.</summary>
    public IReadOnlyList<ExplorerNode> SelectedNodes => [.. Rows.SelectedItems?.OfType<ExplorerNode>() ?? []];

    /// <summary>What the line under the rows says of the last file operation; null while it says nothing.</summary>
    public string? Said => said.IsVisible ? said.Text : null;

    /// <summary>Says what a file operation did, or why not, under the rows; null or empty says nothing.</summary>
    public void Say(string? text)
    {
        said.Text = text;
        said.IsVisible = !string.IsNullOrEmpty(text);
    }

    /// <summary>Opens a row's menu, at the pointer or (from the keys) under the row; false with nothing on it.</summary>
    public bool OpenMenuOn(ExplorerNode node, bool atPointer = false)
    {
        if (MenuFor?.Invoke(node) is not { Count: > 0 } items)
        {
            return false;
        }
        ContextMenu menu = new();
        foreach (Control item in items)
        {
            menu.Items.Add(item);
        }
        OpenMenu?.Close();
        OpenMenu = menu;
        menu.Closed += (_, _) => OpenMenu = OpenMenu == menu ? null : OpenMenu;
        if (!atPointer && Rows.ContainerFromItem(node) is { } row)
        {
            ToolWindows.ListMenus.PlaceForKeys(menu, row);
        }
        menu.Open(Rows);
        return true;
    }

    public ExplorerTree Tree { get; } = new();

    /// <summary>The folder shown, by its canonical path; null with nothing open.</summary>
    public string? Folder => folder;

    /// <summary>The row chosen, if any.</summary>
    public ExplorerNode? Selected => Rows.SelectedItem as ExplorerNode;

    /// <summary>The list of rows, for the window's keys and tests.</summary>
    public TreeList RowList => Rows;

    /// <summary>Shows a folder's files, its modules and source roots marked once the import reads them.</summary>
    public void Load(string root, WorkspaceSnapshot? snapshot)
    {
        folder = PathNames.Canonical(root);
        Tree.Load(folder, snapshot);
        Rows.IsVisible = true;
        filterBar.IsVisible = true;
        indexed = null;
        Watch(folder);
        ShowHint();
        Refilter();
    }

    /// <summary>The import read the folder again: its modules and source roots marked anew, the rows open and chosen kept.</summary>
    public void Update(WorkspaceSnapshot? snapshot)
    {
        if (folder is null)
        {
            return;
        }
        Tree.Take(snapshot);
        indexed = null;
        Refresh();
    }

    /// <summary>Shows nothing, its hint in its place, as it did before a folder opened.</summary>
    public void Unload()
    {
        StopWatching();
        folder = null;
        indexed = null;
        Tree.Clear();
        Rows.IsVisible = false;
        filterBar.IsVisible = false;
        answered = ++asked;
        matched = null;
        more.IsVisible = false;
        ShowHint();
    }

    /// <summary>Reads the folder again, keeping the folders that were open open and the chosen row chosen, and the filter
    /// as it was.</summary>
    public void Refresh()
    {
        if (folder is null)
        {
            return;
        }
        Tree.Unfilter();
        HashSet<string> open = new(Tree.Rows.Where(r => r.IsExpanded).Select(r => r.Path!), PathNames.Comparer);
        string? chosen = Selected?.Path;
        ExplorerNode? again = null;
        Tree.Reread();
        // Down the rows as they open: a folder's rows come in after it, so each folder is reached once its holder is open.
        for (int at = 0; at < Tree.Rows.Count; at++)
        {
            ExplorerNode row = Tree.Rows[at];
            if (row is { HasChildren: true, IsExpanded: false } && open.Remove(row.Path!))
            {
                Tree.Expand(row);
            }
            if (again is null && chosen is not null && PathNames.Same(row.Path!, chosen))
            {
                again = row;
            }
        }
        if (again is not null)
        {
            Rows.SelectedItem = again;
        }
        Narrow();
    }

    /// <summary>Reads again only the folders that changed on disk, the rows open and chosen kept.</summary>
    public void RefreshFolders(IEnumerable<string> folders)
    {
        Tree.RefreshFolders(folders);
        Narrow();
        ShowHint();
    }

    /// <summary>Gives the tree the keys on its chosen row (the first when none is): the view itself cannot take the
    /// focus, only a row.</summary>
    public void FocusRows()
    {
        if (!Rows.IsVisible || Tree.Rows.Count == 0)
        {
            return;
        }
        if (Rows.SelectedIndex < 0)
        {
            Rows.SelectedIndex = 0;
        }
        int index = Rows.SelectedIndex;
        // A row scrolled out of sight, or in a window just shown, has no container until it is laid out in view.
        Rows.ScrollIntoView(index);
        Rows.UpdateLayout();
        Rows.ContainerFromIndex(index)?.Focus();
    }

    /// <summary>The filter's text: the rows narrowed to the files whose names hold it, with their folders.</summary>
    public string FilterText
    {
        get => filter.Text ?? string.Empty;
        set => filter.Text = value;
    }

    /// <summary>What the line under the rows says of the files the filter matched beyond those the tree shows; null while
    /// it shows them all.</summary>
    public string? MoreMatched => more.IsVisible ? more.Text : null;

    /// <summary>Gives the filter the keys, its text chosen.</summary>
    public void FocusFilter()
    {
        filter.Focus();
        filter.SelectAll();
    }

    /// <summary>Runs the filter now rather than after its pause, and waits for its answer: for tests.</summary>
    public Task FilterNowAsync()
    {
        filtering.Stop();
        return MatchAsync();
    }

    /// <summary>The filter matched again among the folder's files as they now are, while it has text.</summary>
    public void Refilter()
    {
        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            IdeErrors.Observe(MatchAsync());
        }
    }

    /// <summary>The files the filter's text names found off the window's thread, then the rows narrowed to them, unless a
    /// newer filter was asked for meanwhile; its text gone, every row again as it was open before.</summary>
    private async Task MatchAsync()
    {
        int ask = ++asked;
        string text = FilterText.Trim();
        if (text.Length == 0)
        {
            answered = ask;
            matched = null;
            chooseFirst = false;
            Unnarrow();
            return;
        }
        ExplorerFilter.Matches found = new([], 0);
        if (folder is { } root)
        {
            IReadOnlyList<string>? files = indexed;
            found = await Task.Run(() =>
            {
                files ??= Index(root, Tree.IsOutput);
                ExplorerFilter.Matches named = ExplorerFilter.Of(files, text);
                return named with { Files = [.. named.Files.Select(f => PathNames.Canonical(Path.Combine(root, f)))] };
            });
            if (folder == root)
            {
                indexed ??= files;
            }
        }
        if (ask != asked)
        {
            return;
        }
        answered = ask;
        matched = found;
        Narrow();
        if (chooseFirst)
        {
            chooseFirst = false;
            ChooseFirstFile();
        }
    }

    /// <summary>A folder's files by their paths from it, version control's store and the build's output passed over, at most
    /// <see cref="MostIndexed"/> of them.</summary>
    public static IReadOnlyList<string> Index(string root, Func<string, bool> isOutput)
    {
        List<string> files = [];
        Stack<string> folders = new([root]);
        while (folders.Count > 0 && files.Count < MostIndexed)
        {
            string at = folders.Pop();
            try
            {
                foreach (FileSystemInfo entry in new DirectoryInfo(at).EnumerateFileSystemInfos())
                {
                    if (entry is DirectoryInfo)
                    {
                        if (entry.Name != ".git" && !isOutput(entry.FullName) && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            folders.Push(entry.FullName);
                        }
                    }
                    else if (!PathNames.IsSystemFile(entry.Name) && files.Count < MostIndexed)
                    {
                        files.Add(Path.GetRelativePath(root, entry.FullName));
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Gone or shut: what it holds is left out.
            }
        }
        return files;
    }

    /// <summary>The rows narrowed to the files of the answer landed, as the tree now is; nothing while none has.</summary>
    private void Narrow()
    {
        if (matched is not { } found)
        {
            return;
        }
        Tree.Filter(found.Files);
        more.Text = $"{found.More:N0} more {Plural.Noun(found.More, "file")} {Plural.Agreeing(found.More, "matches", "match")}: a longer filter narrows them";
        more.IsVisible = found.More > 0;
        ShowHint();
    }

    /// <summary>Every row again, as it was open before the filter, the row chosen meanwhile kept in sight.</summary>
    private void Unnarrow()
    {
        more.IsVisible = false;
        if (!Tree.IsFiltered)
        {
            return;
        }
        string? chosen = Selected?.Path;
        Tree.Unfilter();
        if (chosen is not null && Selected is null)
        {
            Reveal(chosen);
        }
        else if (Selected is { } still)
        {
            Rows.ScrollIntoView(still);
        }
        ShowHint();
    }

    /// <summary>The first file shown chosen, where no row is.</summary>
    private void ChooseFirstFile()
    {
        if (Selected is null && Tree.Rows.FirstOrDefault(r => r.IsFile) is { } first)
        {
            Rows.SelectedItem = first;
        }
    }

    /// <summary>The hint over the rows: nothing open, or a filter that leaves no file.</summary>
    private void ShowHint()
    {
        bool none = Rows.IsVisible && Tree.IsFiltered && !Tree.Rows.Any(r => r.IsFile);
        Hint.Text = none ? "No file matches" : "No folder open";
        Hint.IsVisible = none || !Rows.IsVisible;
    }

    /// <summary>The filter's keys: Down or Enter takes the rows' keys, on the row chosen or the first file shown; Escape
    /// empties it and gives the rows their keys back.</summary>
    private void OnFilterKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Down or Key.Enter or Key.Escape))
        {
            return;
        }
        if (e.Key == Key.Escape)
        {
            filter.Text = string.Empty;
        }
        else if (filtering.IsWaiting || answered != asked)
        {
            // The answer is yet to land: the first file it shows is chosen once it does.
            chooseFirst = true;
            if (filtering.IsWaiting)
            {
                filtering.Stop();
                IdeErrors.Observe(MatchAsync());
            }
        }
        else
        {
            ChooseFirstFile();
        }
        FocusRows();
        e.Handled = true;
    }

    /// <summary>Opens the folders down to a file or folder and chooses its row; false where the folder does not hold it.</summary>
    public bool Reveal(string path)
    {
        if (Tree.Reveal(path) is not { } node)
        {
            return false;
        }
        Rows.SelectedItem = node;
        Rows.ScrollIntoView(node);
        return true;
    }

    private void OnChevronPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ExplorerNode { HasChildren: true } node })
        {
            Tree.Toggle(node);
            e.Handled = true;
        }
    }

    /// <summary>A row for whichever file or folder the list hands its container: its chevron, icon, name and detail.</summary>
    private Control Row()
    {
        RowGlyphs glyphs = new();
        glyphs.ChevronPressed += OnChevronPressed;
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock detail = new() { Classes = { StyleClasses.Muted }, FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        StackPanel row = new() { Orientation = Orientation.Horizontal, Height = ListRow.Height, Children = { glyphs, name, detail } };
        return row.Showing<ExplorerNode>(node =>
        {
            row.Margin = node.Indent;
            row.Opacity = node.Opacity;
            glyphs.HasChevron = node.HasChildren;
            glyphs.Turned = node.IsExpanded;
            glyphs.Icon = node.Icon;
            glyphs.IconBrush = node.IconBrush;
            name.Text = node.Name;
            detail.Text = node.Detail;
        });
    }

    /// <summary>A press on a row that can move begins what may become a drag of it, or of every row chosen with it.</summary>
    private void OnRowsPressed(object? sender, PointerPressedEventArgs e)
    {
        ExplorerNode? pressed = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ExplorerNode;
        if (pressed is { IsMovable: true } row && e.GetCurrentPoint(Rows).Properties.IsLeftButtonPressed)
        {
            // The rows chosen as it was pressed: the list then chooses the row alone.
            IReadOnlyList<ExplorerNode> chosen = SelectedNodes;
            drag.Begin(chosen.Contains(row) ? [.. chosen.Where(n => n.IsMovable)] : [row], e);
        }
    }

    private void OnRowsMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(Rows).Properties.IsLeftButtonPressed)
        {
            // Released outside the rows before it became a drag, which they never heard.
            drag.Cancel();
            return;
        }
        copyHeld = CopyHeld(e.KeyModifiers);
        // Once a drag, the pointer is the rows' until the release, wherever it goes.
        if (drag.Continue(e) && e.Pointer.Captured != Rows)
        {
            e.Pointer.Capture(Rows);
        }
    }

    private void OnRowsReleased(object? sender, PointerReleasedEventArgs e)
    {
        copyHeld = CopyHeld(e.KeyModifiers);
        if (drag.End(e))
        {
            e.Handled = true;
        }
        if (e.Pointer.Captured == Rows)
        {
            e.Pointer.Capture(null);
        }
    }

    /// <summary>The platform's key for a copy rather than a move as files are dropped: Option on the Mac, Ctrl elsewhere.</summary>
    private static bool CopyHeld(KeyModifiers modifiers) => modifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Alt : KeyModifiers.Control);

    /// <summary>The row drawn under a point of the list.</summary>
    private ExplorerNode? RowAt(Point inRows)
    {
        foreach (Control container in Rows.GetRealizedContainers())
        {
            if (Rows.TranslatePoint(inRows, container) is { } at && new Rect(container.Bounds.Size).Contains(at))
            {
                return container.DataContext as ExplorerNode;
            }
        }
        return null;
    }

    /// <summary>The folder what is dragged would go into at a point: the one under it, or a file's; none where it would
    /// not move (all of it there already) or a folder would go inside itself.</summary>
    private ExplorerNode? TargetAt(IReadOnlyList<ExplorerNode> items, Point at)
    {
        if (Surface.TranslatePoint(at, Rows) is not { } inRows || RowAt(inRows) is not { } row)
        {
            return null;
        }
        // A file's folder holds what is dropped on it.
        ExplorerNode? target = row;
        while (target is { Kind: NodeKind.File })
        {
            target = Tree.Rows.Holder(target);
        }
        return target?.Into is not { } into
            || items.Any(i => i.Kind == NodeKind.Folder && PathNames.Within(into, i.Path!))
            || !copyHeld && items.All(i => PathNames.Same(Path.GetDirectoryName(i.Path!)!, into))
            ? null
            : target;
    }

    /// <summary>Where a drop lights up: the folder's row with the rows under it that show, as one place.</summary>
    private DropZone? DropZoneAt(IReadOnlyList<ExplorerNode> items, Point at)
    {
        if (TargetAt(items, at) is not { } target)
        {
            return null;
        }
        int first = Tree.Rows.IndexOf(target);
        int last = first;
        while (last + 1 < Tree.Rows.Count && Tree.Rows[last + 1].Depth > target.Depth)
        {
            last++;
        }
        Rect? place = null;
        foreach (Control container in Rows.GetRealizedContainers())
        {
            int index = Rows.IndexFromContainer(container);
            if (index >= first && index <= last && container.TranslatePoint(default, Surface) is { } corner)
            {
                Rect bounds = new(corner, container.Bounds.Size);
                place = place is { } so ? so.Union(bounds) : bounds;
            }
        }
        return place is { } lit ? new DropZone(lit.Intersect(new Rect(Surface.Bounds.Size))) : null;
    }

    /// <summary>Watches the folder and all it holds, so files the build or another program writes show; where the system
    /// will not watch it, Refresh alone reads it again.</summary>
    private void Watch(string root)
    {
        StopWatching();
        try
        {
            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            watcher.Created += OnDiskChanged;
            watcher.Deleted += OnDiskChanged;
            watcher.Renamed += OnDiskChanged;
            watcher.Error += (_, _) => Dispatcher.UIThread.Post(() => QueueFolder(root));
            watcher.EnableRaisingEvents = true;
            Workspace.WatchLag.Behind += OnWatchBehind;
        }
        catch (Exception error) when (error is IOException or ArgumentException or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            watcher = null;
        }
    }

    private void StopWatching()
    {
        Workspace.WatchLag.Behind -= OnWatchBehind;
        watcher?.Dispose();
        watcher = null;
        watchQuiet.Stop();
        changedFolders.Clear();
    }

    /// <summary>The system's watcher is behind (<see cref="Workspace.WatchLag"/>, a busy Mac): what it would have said may
    /// be minutes late, so the whole folder is read again.</summary>
    private void OnWatchBehind(DateTime since) => Dispatcher.UIThread.Post(() =>
    {
        if (folder is { } root)
        {
            QueueFolder(root);
        }
    });

    private void OnDiskChanged(object sender, FileSystemEventArgs e)
    {
        string? held = Path.GetDirectoryName(e.FullPath);
        string? before = e is RenamedEventArgs renamed ? Path.GetDirectoryName(renamed.OldFullPath) : null;
        if (held is null || IsInGitStore(e.FullPath))
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            QueueFolder(held);
            if (before is not null)
            {
                QueueFolder(before);
            }
        });
    }

    /// <summary>Whether a path is inside version control's store, which changes with every command and is never listed.</summary>
    private bool IsInGitStore(string path) =>
        folder is { } root && PathNames.Within(path, Path.Combine(root, ".git"));

    private void QueueFolder(string path)
    {
        if (watcher is null)
        {
            return;
        }
        changedFolders.Add(PathNames.Canonical(path));
        watchQuiet.Stop();
        watchQuiet.Start();
    }

    /// <summary>The folders that changed on disk read again, and the filter matched again among the files as they now are.</summary>
    private void FollowDisk()
    {
        if (changedFolders.Count == 0)
        {
            return;
        }
        List<string> changed = [.. changedFolders];
        changedFolders.Clear();
        indexed = null;
        RefreshFolders(changed);
        Refilter();
    }
}
