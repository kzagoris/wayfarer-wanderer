// The Explorer's row menu, its keys and its drags, after Passenger 494009d7 src/Passenger.App/MainWindow.ExplorerActions.cs,
// MainWindow.Files.cs and MainWindow.FileMoves.cs, MIT (notices/Passenger/LICENSE): the solution, project, NuGet, Docker,
// Aspire, Godot, nesting, Local History and Git parts cut, Undo kept only for a Delete, and C#'s Class/Interface made
// Java's and Kotlin's.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Docking;
using WayfarerWanderer.App.Shell.Explorer;
using WayfarerWanderer.Jvm;
using A = WayfarerWanderer.App.Shell.AppActions;
using E = Passenger.Editor.EditorActions;

namespace WayfarerWanderer.App;

public sealed partial class EditorWindow
{
    /// <summary>The group Find Action lists the Explorer's actions under.</summary>
    private const string ExplorerGroup = "Project";

    private const string NoRow = "Choose a file or folder in the Project tool window";

    private const string NothingHeld = "Nothing to paste: no files or folders were cut or copied";

    /// <summary>The kinds New Java/Kotlin Class makes, in the order its popup lists them.</summary>
    public static IReadOnlyList<string> TypeKinds { get; } =
        ["Java Class", "Java Interface", "Java Enum", "Java Record", "Kotlin Class", "Kotlin Interface", "Kotlin Object", "Kotlin File"];

    /// <summary>The actions a row's keys run while the Explorer has them, those the editor shares (the clipboard's, Delete)
    /// taken from it there.</summary>
    private static readonly HashSet<string> ExplorerKeys =
        [E.Cut, E.Copy, E.Paste, E.Delete, E.Backspace, E.Undo, E.Redo, A.Rename, A.CopyPath, A.RevealInFileManager, A.NewFile, A.NewFolder, A.NewType];

    /// <summary>The Explorer's deletes Undo takes back out of the Trash, the last on top, each what one Delete moved there.</summary>
    private readonly Stack<IReadOnlyList<Trashed>> deletesDone = [];

    /// <summary>The deletes Undo took back, which Redo moves to the Trash again; cleared by the next Delete.</summary>
    private readonly Stack<IReadOnlyList<Trashed>> deletesUndone = [];

    /// <summary>What Cut or Copy last put on the clipboard, whose text is their paths: a Paste finds them only while the
    /// clipboard still holds that text, so text copied since is never taken for them.</summary>
    private (IReadOnlyList<string> Paths, bool Cut, string Text)? held;

    /// <summary>The popup over the window asking for a name or a yes, one at a time.</summary>
    private readonly Popup asking = new()
    {
        IsLightDismissEnabled = true,
        Placement = PlacementMode.AnchorAndGravity,
        PlacementAnchor = PopupAnchor.Top,
        PlacementGravity = PopupGravity.Bottom,
        VerticalOffset = 72,
    };

    /// <summary>The Explorer's tool window, whose chosen row the actions run on.</summary>
    private ToolWindow ExplorerWindow => ToolDock[ToolWindowIds.Explorer];

    /// <summary>The popup open over the window, if any: for tests.</summary>
    public Control? Asking => asking.IsOpen ? asking.Child : null;

    /// <summary>The window holds the system's watcher's beat (<see cref="Shell.Workspace.WatchLag.Hold"/>) while it is in
    /// front, so a Mac whose watcher falls behind under a load of changes has the Explorer and watched files look for
    /// themselves; in the background the look as it comes in front finds what went unheard.</summary>
    private void HoldWatchBeat()
    {
        IDisposable? beat = null;
        // A window's coming in front the system posted before it closed may land after: it holds nothing then.
        bool closed = false;
        Activated += (_, _) => beat ??= closed ? null : Shell.Workspace.WatchLag.Hold();
        Deactivated += (_, _) =>
        {
            beat?.Dispose();
            beat = null;
        };
        Closed += (_, _) =>
        {
            closed = true;
            beat?.Dispose();
            beat = null;
        };
    }

    private void AddExplorerActions()
    {
        Explorer.MenuFor = ExplorerMenu;
        Explorer.Dropped += (_, dropped) => IdeErrors.Observe(DropAsync(dropped));
        Explorer.RowList.AddHandler(KeyDownEvent, OnExplorerKey);
        asking.PlacementTarget = ToolDock;
        asking.Closed += (_, _) => asking.Child = null;
        HoldWatchBeat();

        Row(A.NewFile, "New File…", row => AskName("New File", string.Empty, name => CreateFile(row.Into!, name, string.Empty)), Holds);
        Row(A.NewFolder, "New Folder…", row => AskName("New Folder", string.Empty, name => CreateFolder(row.Into!, name)), Holds);
        Row(A.NewType, "Java/Kotlin Class…", row => AskType(row.Into!), Holds);
        ClipboardAction(E.Cut, "Cut", row => IdeErrors.Observe(HoldAsync(row, cut: true)), Movable, box => box.Cut());
        ClipboardAction(E.Copy, "Copy", row => IdeErrors.Observe(HoldAsync(row, cut: false)), Movable, box => box.Copy());
        ClipboardAction(E.Paste, "Paste", row => IdeErrors.Observe(PasteAsync(row)),
            row => row.Into is null ? $"Nothing can be pasted into {row.Name}" : held is null ? NothingHeld : null, box => box.Paste());
        Row(E.Delete, "Delete…", row => IdeErrors.Observe(DeleteRowsAsync(row)), Movable);
        Row(A.Rename, "Rename…", row => AskRename(row.Path!), Movable);
        Row(A.CopyPath, "Copy Path", row => IdeErrors.Observe(CopyPathAsync(row.Path!)), row => row.Path is null ? $"{row.Name} is in no folder on disk" : null);
        Row(A.RevealInFileManager, FileManager.Showing, row => FileManager.Show(this, row.Path!), row => row.Path is null ? $"{row.Name} is in no folder on disk" : null);
        Row(A.BuildSelected, "Build Module", row =>
            {
                ToolDock.ShowBehind(ToolWindowIds.Build);
                IdeErrors.Observe(Session!.BuildAsync(ModuleOf(row)!.Id, "classes"));
            },
            row => ModuleOf(row) is null ? $"{row.Name} is no module" : Session is not { IsTrusted: true } ? "The folder is not trusted"
                : Session.IsRunning ? "A build or run is under way" : null);

        static string? Holds(ExplorerNode row) => row.Into is null ? $"Nothing new goes in {row.Name}" : null;
        static string? Movable(ExplorerNode row) => row.IsMovable ? null : $"{row.Name} is the folder opened";
    }

    /// <summary>One of the Explorer's own actions, on its row.</summary>
    private void Row(string id, string title, Action<ExplorerNode> run, Func<ExplorerNode, string?> whyNot) =>
        Actions.Registry.Add(new IdeAction(id, title, ExplorerGroup, c => run(RowOf(c)!))
        {
            WhyNot = c => RowOf(c) is { } row ? whyNot(row) : NoRow,
        });

    /// <summary>One of the clipboard's actions, Edit's and the Explorer's alike: on the Explorer's chosen rows while its menu
    /// asks or its rows have the keys, else on the text box with the keys or the editor shown (<see cref="EditText"/>).</summary>
    private void ClipboardAction(string id, string title, Action<ExplorerNode> run, Func<ExplorerNode, string?> whyNot, Action<TextBox> inBox) =>
        Actions.Registry.Add(new IdeAction(id, title, "Edit", c =>
        {
            if (InExplorer(c))
            {
                run(RowOf(c)!);
            }
            else
            {
                EditText(c, id, inBox);
            }
        })
        {
            WhyNot = c => InExplorer(c) ? RowOf(c) is { } row ? whyNot(row) : NoRow : WhyNotEditText(c),
            InEditor = true,
        });

    /// <summary>Whether an action asked for here acts on the Explorer's rows: its menu asks, or its rows have the keys.</summary>
    private bool InExplorer(ActionContext context) =>
        context.ToolWindow?.Id == ToolWindowIds.Explorer || context.Focused is not TextBox && Explorer.RowList.IsKeyboardFocusWithin;

    /// <summary>The Explorer's row an action acts on: the one chosen while its menu asks or its rows have the keys.</summary>
    private ExplorerNode? RowOf(ActionContext context) => InExplorer(context) ? Explorer.Selected : null;

    /// <summary>Where the Explorer's actions run as its menu or its keys ask: on its chosen row.</summary>
    private ActionContext ExplorerContext() => Actions.Registry.Context().In(ExplorerWindow);

    /// <summary>The module whose folder a row is, if the import named one there.</summary>
    private JvmModule? ModuleOf(ExplorerNode row) => row is { JvmFolder: JvmFolder.Module, Path: { } folder }
        ? Session?.Snapshot?.Modules.FirstOrDefault(m => PathNames.Same(PathNames.Canonical(m.Root), folder))
        : null;

    /// <summary>The keys the Explorer's rows take while they have them, by the keymap in force: the clipboard's and Delete
    /// (Backspace too) on the chosen rows, Rename, Copy Path and New.</summary>
    private void OnExplorerKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || Explorer.Selected is null)
        {
            return;
        }
        Shortcut pressed = new(Keystroke.From(e));
        string? action = AppActions.Keymap().ActionFor(pressed) is { } id && ExplorerKeys.Contains(id) ? id
            : Actions.Registry.Keymap.ActionFor(pressed) is { } own && ExplorerKeys.Contains(own) ? own
            : null;
        if (action is null)
        {
            return;
        }
        Actions.Registry.Run(action == E.Backspace ? E.Delete : action, ExplorerContext());
        e.Handled = true;
    }

    /// <summary>A row's menu, as Passenger's Explorer makes it for a folder on disk: a module's Build, Add, Edit (the
    /// clipboard's actions, Delete and Rename), the path, and Open In.</summary>
    private IReadOnlyList<Control>? ExplorerMenu(ExplorerNode node)
    {
        ActionContext context = ExplorerContext();
        List<Control> items = [];
        if (ModuleOf(node) is not null)
        {
            items.AddRange([Item(A.BuildSelected), new Separator()]);
        }
        if (node.Into is not null)
        {
            items.AddRange([Sub("Add", Item(A.NewFile), Item(A.NewFolder), Item(A.NewType)), new Separator()]);
        }
        List<Control> edit = [];
        if (node.IsMovable)
        {
            edit.AddRange([Item(E.Cut), Item(E.Copy)]);
        }
        edit.Add(Item(E.Paste));
        if (node.IsMovable)
        {
            edit.AddRange([new Separator(), Item(E.Delete), Item(A.Rename)]);
        }
        items.Add(Sub("Edit", [.. edit]));
        items.AddRange([Item(A.CopyPath), Sub("Open In", Item(A.RevealInFileManager))]);
        return items;

        MenuItem Item(string id)
        {
            IdeAction action = Actions.Registry[id]!;
            MenuItem item = new()
            {
                Header = action.TitleWhere(context),
                Tag = id,
                IsEnabled = Actions.Registry.WhyNot(id, context) is null,
                InputGesture = Gesture(id),
            };
            item.Click += (_, _) => Actions.Registry.Run(id, context);
            return item;
        }

        static MenuItem Sub(string header, params Control[] entries) => new() { Header = header, ItemsSource = entries };
    }

    /// <summary>An action's one-keystroke shortcut in the keymap in force, else in the window's own.</summary>
    private KeyGesture? Gesture(string id)
    {
        foreach (Keymap keymap in (Keymap[])[AppActions.Keymap(), Actions.Registry.Keymap])
        {
            if (keymap.ShortcutsFor(AppActions.KeysIn(id)).FirstOrDefault(s => s.Second is null) is { } shortcut)
            {
                return new KeyGesture(shortcut.First.Key, shortcut.First.Modifiers);
            }
        }
        return null;
    }

    // Asking.

    /// <summary>Asks for a name in a popup, which stays open saying why while <paramref name="attempt"/> refuses it, the
    /// first <paramref name="selected"/> characters of the initial name selected (all of it when null).</summary>
    private NamePopup AskName(string title, string initial, Func<string, string?> attempt, int? selected = null,
        IReadOnlyList<(string Name, Control Icon)>? kinds = null)
    {
        NamePopup popup = new(title, initial, name => Task.FromResult(attempt(name)), kinds, selected: selected);
        popup.Done += (_, _) => CloseAsking();
        Ask(popup);
        return popup;
    }

    /// <summary>Asks for a name as <see cref="AskName"/> does, answering with the one taken, or null once the popup closes
    /// without one (Escape, a click outside it).</summary>
    private Task<string?> AskNameAsync(string title, string initial, Func<string, string?> refuse, int? selected = null)
    {
        TaskCompletionSource<string?> answer = new();
        string? taken = null;
        AskName(title, initial, name =>
        {
            string? why = refuse(name);
            taken = why is null ? name : null;
            return why;
        }, selected);
        asking.Closed += Closed;
        return answer.Task;

        void Closed(object? sender, EventArgs e)
        {
            asking.Closed -= Closed;
            answer.TrySetResult(taken);
        }
    }

    /// <summary>Asks a yes or no in a popup: true once its button is pressed (or Enter), false once it is given up.</summary>
    private Task<bool> ConfirmAsync(string title, string question, string verb)
    {
        TaskCompletionSource<bool> answer = new();
        Button yes = new() { Content = verb, Classes = { "accent" }, IsDefault = true };
        Button no = new() { Content = "Cancel", IsCancel = true };
        yes.Click += (_, _) => { answer.TrySetResult(true); CloseAsking(); };
        no.Click += (_, _) => { answer.TrySetResult(false); CloseAsking(); };
        Border frame = new()
        {
            Classes = { StyleClasses.Popup },
            Padding = new Thickness(12, 10),
            Width = 360,
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Classes = { ShellClasses.PopupTitle }, Text = title },
                    new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { no, yes } },
                },
            },
        };
        frame.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                answer.TrySetResult(e.Key == Key.Enter);
                CloseAsking();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        asking.Closed += Closed;
        Ask(frame);
        yes.Focus();
        return answer.Task;

        void Closed(object? sender, EventArgs e)
        {
            asking.Closed -= Closed;
            answer.TrySetResult(false);
        }
    }

    private void Ask(Control popup)
    {
        CloseAsking();
        asking.Child = popup;
        asking.IsOpen = true;
    }

    private void CloseAsking()
    {
        if (asking.IsOpen)
        {
            asking.IsOpen = false;
            Explorer.FocusRows();
        }
    }

    /// <summary>New Java/Kotlin Class: a name, and the kind of type chosen under it with Up and Down.</summary>
    private void AskType(string into)
    {
        NamePopup? popup = null;
        popup = AskName("New Java/Kotlin Class", string.Empty, name => CreateType(into, name, popup?.Kind ?? TypeKinds[0]),
            kinds: [.. TypeKinds.Select(kind => (kind, TypeBadge(kind)))]);
    }

    /// <summary>A kind's icon: Java's or Kotlin's file icon.</summary>
    private static Control TypeBadge(string kind)
    {
        string file = kind.StartsWith("Java", StringComparison.Ordinal) ? "A.java" : "A.kt";
        PathIcon icon = new() { Width = 14, Height = 14, Data = FileIcons.ForFile(file) };
        icon.Bind(PathIcon.ForegroundProperty, icon.GetResourceObservable(FileIcons.TintForFile(file)));
        return icon;
    }

    // Making and renaming.

    /// <summary>Makes a file in a folder (folders in its name made too) and opens it; null when it worked, else why not.</summary>
    public string? CreateFile(string into, string name, string text)
    {
        if (Invalid(name) is { } problem)
        {
            return problem;
        }
        string path = Path.GetFullPath(Path.Combine(into, name));
        if (Stands(path))
        {
            return $"{name} is already there";
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FileText.SaveNew(path, text);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return NotMade(name, error);
        }
        CameOrWent([path]);
        OpenFromExplorer(path);
        Explorer.Reveal(path);
        return null;
    }

    public string? CreateFolder(string into, string name)
    {
        if (Invalid(name) is { } problem)
        {
            return problem;
        }
        string path = Path.GetFullPath(Path.Combine(into, name));
        if (Stands(path))
        {
            return $"{name} is already there";
        }
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return NotMade(name, error);
        }
        CameOrWent([path]);
        Explorer.Reveal(path);
        return null;
    }

    /// <summary>New Java/Kotlin Class: a file of its name holding a type of the kind chosen (<see cref="TypeKinds"/>), in
    /// the package its folder gives under its source root. Folders before the name ("shapes/Circle") are made and the type
    /// goes in their package.</summary>
    public string? CreateType(string into, string name, string kind)
    {
        string typed = name.Trim().Replace('\\', '/');
        string type = Path.GetFileName(typed);
        string folders = Path.GetDirectoryName(typed) ?? string.Empty;
        if (!IsIdentifier(type))
        {
            return $"{type} is not a type name";
        }
        bool kotlin = kind.StartsWith("Kotlin", StringComparison.Ordinal);
        string folder = Path.GetFullPath(Path.Combine(into, folders));
        string package = PackageOf(folder);
        string declared = kind switch
        {
            "Java Class" => $"public class {type} {{\n}}\n",
            "Java Interface" => $"public interface {type} {{\n}}\n",
            "Java Enum" => $"public enum {type} {{\n}}\n",
            "Java Record" => $"public record {type}() {{\n}}\n",
            "Kotlin Class" => $"class {type} {{\n}}\n",
            "Kotlin Interface" => $"interface {type} {{\n}}\n",
            "Kotlin Object" => $"object {type} {{\n}}\n",
            _ => string.Empty,
        };
        string head = package.Length == 0 ? string.Empty : kotlin ? $"package {package}\n\n" : $"package {package};\n\n";
        return CreateFile(into, Path.Combine(folders, type + (kotlin ? ".kt" : ".java")), head + declared);
    }

    /// <summary>The package a folder's types are in: its path under the source root that holds it, dotted; none outside one.
    /// Before the import has named the source roots (a folder not yet trusted), Maven's and Gradle's own are taken.</summary>
    private string PackageOf(string folder)
    {
        string canonical = Path.TrimEndingDirectorySeparator(folder);
        string? root = Session?.Snapshot?.Modules.SelectMany(m => m.SourceRoots)
            .Select(r => Path.GetFullPath(r.Path))
            .Where(r => PathNames.Within(canonical, r))
            .OrderByDescending(r => r.Length)
            .FirstOrDefault()
            ?? ConventionalRoot(canonical);
        if (root is null)
        {
            return string.Empty;
        }
        string below = Path.GetRelativePath(root, canonical);
        return below == "." ? string.Empty : string.Join('.', below.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    /// <summary>The nearest folder above (or at) one that is a conventional source root: src/main/java, src/test/kotlin
    /// and the like.</summary>
    private static string? ConventionalRoot(string folder)
    {
        for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
        {
            if (Path.GetFileName(at) is "java" or "kotlin"
                && Path.GetFileName(Path.GetDirectoryName(at)) is "main" or "test"
                && Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(at))) == "src")
            {
                return at;
            }
        }
        return null;
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '$') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$');

    /// <summary>Rename on a row: the name chosen up to its extension, the file or folder renamed on disk, its open files
    /// following it.</summary>
    private void AskRename(string path)
    {
        string name = Path.GetFileName(path);
        bool file = File.Exists(path);
        int? before = file && Path.GetFileNameWithoutExtension(name) is { Length: > 0 } stem && stem.Length < name.Length ? stem.Length : null;
        AskName(file ? "Rename File" : "Rename Folder", name, typed => Rename(path, typed), before);
    }

    /// <summary>Renames a file or folder in its folder; null when it worked (or the name is the same), else why not.</summary>
    public string? Rename(string path, string name)
    {
        if (Invalid(name) is { } problem)
        {
            return problem;
        }
        if (name.Contains('/') || name.Contains('\\'))
        {
            return $"{name} is not a file name";
        }
        string folder = Path.GetDirectoryName(path)!;
        string target = Path.Combine(folder, name);
        if (string.Equals(Path.GetFileName(path), name, StringComparison.Ordinal))
        {
            return null;
        }
        // A change of case alone is the same file where the folder ignores case.
        if (Stands(target) && !PathNames.Same(PathNames.Canonical(target), PathNames.Canonical(path)))
        {
            return $"{name} is already there";
        }
        if (MoveOnDisk(path, target) is { } unmoved)
        {
            return $"{Path.GetFileName(path)} could not be renamed: {unmoved}";
        }
        CameOrWent([path, target]);
        Explorer.Reveal(target);
        Explorer.Say($"Renamed {Path.GetFileName(path)} to {name}");
        return null;
    }

    private static string NotMade(string name, Exception error) =>
        $"{name} could not be made: {(error is UnauthorizedAccessException ? "access denied" : error.Message)}";

    /// <summary>Why a name given a new or renamed file or folder is none: empty, holding a character no name may, or with a
    /// part that climbs out of its folder (".."), where two dots inside a name (release..notes.txt) are its own.</summary>
    private static string? Invalid(string name) =>
        name.Trim().Length == 0 ? "A name is needed"
        : name.IndexOfAny(Path.GetInvalidFileNameChars().Where(c => c is not '/' and not '\\').ToArray()) >= 0 || name.Split(['/', '\\']).Contains("..") ? $"{name} is not a file name"
        : null;

    private static bool Stands(string path) => File.Exists(path) || Directory.Exists(path);

    // Deleting.

    /// <summary>The rows an action on a row takes: it, or every row chosen with it, but the folder opened.</summary>
    private IReadOnlyList<ExplorerNode> RowsWith(ExplorerNode row)
    {
        IReadOnlyList<ExplorerNode> chosen = Explorer.SelectedNodes;
        return [.. (chosen.Contains(row) ? chosen : [row]).Where(n => n.IsMovable)];
    }

    /// <summary>Delete on a row: it, or every row chosen with it, to the Trash once asked, those inside another taken with it.</summary>
    private async Task DeleteRowsAsync(ExplorerNode row)
    {
        IReadOnlyList<string> paths = Outermost([.. RowsWith(row).Select(n => n.Path!)]);
        if (paths.Count == 0 || !await ConfirmAsync("Delete", DeleteQuestion(paths), "Delete"))
        {
            return;
        }
        await DeleteAsync(paths);
    }

    /// <summary>Moves files and folders to the Trash, closing what of them is open; true once all are gone. Where it is
    /// <paramref name="undoable"/> (a Delete of its own, not a paste's replacing), it is one step of Undo, which brings back
    /// out of the Trash what it moved there, and Redo moves it there again.</summary>
    public Task<bool> DeleteAsync(IReadOnlyList<string> paths, bool undoable = true)
    {
        List<string> gone = [];
        List<string> refused = [];
        List<Trashed> trashed = [];
        foreach (string path in paths)
        {
            try
            {
                if (Trash.Move(path) is { } went)
                {
                    trashed.Add(went);
                }
                gone.Add(path);
                Forget(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                refused.Add($"{Path.GetFileName(path)} could not be deleted: {error.Message}");
            }
        }
        if (gone.Count > 0)
        {
            CameOrWent(gone);
        }
        if (undoable && trashed.Count > 0)
        {
            deletesDone.Push(trashed);
            deletesUndone.Clear();
        }
        Explorer.Say(string.Join("; ", [.. gone.Count > 0 ? [$"Deleted {Plural.Listed([.. gone.Select(Path.GetFileName).OfType<string>()])}"] : Array.Empty<string>(), .. refused]));
        return Task.FromResult(refused.Count == 0);
    }

    /// <summary>Undo or Redo where the Explorer's rows have the keys or its menu asks: its last Delete brought back out of
    /// the Trash, or the last one undone moved there again; false where it is not the Explorer's to do, the editor's or a
    /// text box's then.</summary>
    private bool UndoInExplorer(ActionContext context, bool redo)
    {
        if (!InExplorer(context) || (redo ? deletesUndone : deletesDone).Count == 0)
        {
            return false;
        }
        IReadOnlyList<Trashed> step = (redo ? deletesUndone : deletesDone).Pop();
        List<string> done = [];
        List<string> refused = [];
        List<Trashed> again = [];
        foreach (Trashed item in redo ? step : step.Reverse())
        {
            string name = Path.GetFileName(item.From);
            try
            {
                if (redo)
                {
                    if (Trash.Move(item.From) is { } went)
                    {
                        again.Add(went);
                    }
                    Forget(item.From);
                }
                else
                {
                    Trash.PutBack(item);
                    again.Add(item);
                }
                done.Add(item.From);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                refused.Add($"{name} could not be {(redo ? "deleted" : "brought back")}: {error.Message}");
            }
        }
        if (again.Count > 0)
        {
            (redo ? deletesDone : deletesUndone).Push(again);
        }
        if (done.Count > 0)
        {
            CameOrWent(done);
            if (!redo)
            {
                Explorer.Reveal(done[^1]);
            }
        }
        string names = Plural.Listed([.. done.Select(Path.GetFileName).OfType<string>()]);
        Explorer.Say(string.Join("; ", [.. done.Count > 0 ? [redo ? $"Deleted {names}" : $"Brought {names} back from the {TrashName}"] : Array.Empty<string>(), .. refused]));
        return true;
    }

    /// <summary>Why Undo or Redo does nothing in the Explorer, or null where it has a delete to take back or do again; null
    /// too where it is not the Explorer's, for the editor or text box to say.</summary>
    private string? WhyNotUndoInExplorer(ActionContext context, bool redo) =>
        !InExplorer(context) ? null : (redo ? deletesUndone : deletesDone).Count == 0 ? $"No delete to {(redo ? "redo" : "undo")} in the Explorer" : null;

    private static string DeleteQuestion(IReadOnlyList<string> paths) => paths.Count == 1
        ? $"Delete {(Directory.Exists(paths[0]) ? "the folder " : string.Empty)}{Path.GetFileName(paths[0])}{(Directory.Exists(paths[0]) ? " with all it holds" : string.Empty)}? It goes to the {TrashName}."
        : $"Delete these {paths.Count} files and folders? They go to the {TrashName}.";

    private static string TrashName => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

    // Cut, Copy, Paste and drops.

    /// <summary>Cut or Copy on a row: it, or every file and folder chosen with it, but those inside another of them.</summary>
    private async Task HoldAsync(ExplorerNode node, bool cut)
    {
        IReadOnlyList<string> paths = Outermost([.. RowsWith(node).Select(n => n.Path!)]);
        if (paths.Count == 0)
        {
            return;
        }
        string text = string.Join(Environment.NewLine, paths);
        held = (paths, cut, text);
        Explorer.Tree.SetCut(cut ? paths : []);
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
        Explorer.Say($"{(cut ? "Cut" : "Copied")} {Plural.Listed([.. paths.Select(Path.GetFileName).OfType<string>()])}");
    }

    /// <summary>Paste on a row: what was cut or copied goes into the folder the row stands for (<see cref="ExplorerNode.Into"/>).</summary>
    private async Task PasteAsync(ExplorerNode node)
    {
        if (node.Into is not { } into)
        {
            return;
        }
        string? text = Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        if (held is not { } what || text != what.Text)
        {
            ForgetHeld();
            Explorer.Say(NothingHeld);
            return;
        }
        if (what.Cut)
        {
            // A cut is pasted once.
            ForgetHeld();
        }
        await PutAsync(what.Paths, into, copy: !what.Cut);
    }

    private void ForgetHeld()
    {
        held = null;
        Explorer.Tree.SetCut([]);
    }

    private async Task DropAsync(ExplorerDrop drop)
    {
        if (drop.Target.Into is { } into)
        {
            await PutAsync(Outermost([.. drop.Items.Select(i => i.Path!)]), into, drop.Copy);
        }
    }

    /// <summary>The paths, each once, but those inside a folder among them.</summary>
    private static IReadOnlyList<string> Outermost(IReadOnlyList<string> paths)
    {
        HashSet<string> chosen = new(paths, PathNames.Comparer);
        return [.. paths.Where(path => !Above(path).Any(chosen.Contains)).Distinct(PathNames.Comparer)];

        static IEnumerable<string> Above(string path)
        {
            for (string? folder = Path.GetDirectoryName(path); folder is not null; folder = Path.GetDirectoryName(folder))
            {
                yield return folder;
            }
        }
    }

    /// <summary>Moves or copies files and folders into a folder: what is there already stays put, a folder never goes
    /// inside itself nor replaces one holding it, two of one name never go at all, a copy into its own folder is named by the person, and a name taken
    /// there is replaced (to the Trash) only once they agree. The line under the Explorer's rows says what went where and
    /// what could not; the last one put is chosen. Answers with where each went.</summary>
    public async Task<IReadOnlyList<string>> PutAsync(IReadOnlyList<string> paths, string into, bool copy)
    {
        into = PathNames.Canonical(into);
        StringComparer names = PathNames.NamesIn(into);
        if (paths.GroupBy(Path.GetFileName, names).FirstOrDefault(g => g.Count() > 1) is { } clash)
        {
            Explorer.Say($"Nothing was {(copy ? "copied" : "moved")}: {Plural.Listed([.. clash.Select(c => $"{clash.Key} from {Path.GetFileName(Path.GetDirectoryName(c))}")])} would each be {clash.Key} in {Path.GetFileName(into)}");
            return [];
        }
        List<string> put = [];
        List<string> refused = [];
        List<string> changed = [];
        foreach (string source in paths)
        {
            string name = Path.GetFileName(source);
            // One whose name there is a folder holding it would go to the Trash with what it replaced: it stays, said, unasked.
            if (PathNames.IsUnder(source, Path.Combine(into, name)))
            {
                refused.Add($"{name} would replace the folder holding it");
                continue;
            }
            if (!Stands(source))
            {
                refused.Add($"{name} is no longer there");
                continue;
            }
            if (Directory.Exists(source) && PathNames.Within(into, source))
            {
                refused.Add($"{name} cannot go inside itself");
                continue;
            }
            string target = Path.Combine(into, name);
            if (PathNames.Same(PathNames.Canonical(target), PathNames.Canonical(source)))
            {
                if (!copy || await AskNameAsync($"Duplicate {name}", name, other => Invalid(other) ?? (Stands(Path.Combine(into, other)) ? $"{other} is already there" : null),
                    File.Exists(source) && Path.GetFileNameWithoutExtension(name).Length is > 0 and var stem && stem < name.Length ? stem : null) is not { } other)
                {
                    continue;
                }
                target = Path.Combine(into, other);
            }
            else if (Stands(target))
            {
                if (!await ConfirmAsync("Files Already Exist", $"{name} with the same name already exists in '{Path.GetFileName(into)}'. Replace?", "Replace")
                    || !await DeleteAsync([target], undoable: false))
                {
                    continue;
                }
            }
            string? problem = copy ? CopyOnDisk(source, target) : MoveOnDisk(source, target);
            if (problem is not null)
            {
                refused.Add($"{name} could not be {(copy ? "copied" : "moved")}: {problem}");
                continue;
            }
            put.Add(target);
            changed.AddRange(copy ? [target] : [source, target]);
        }
        if (changed.Count > 0)
        {
            CameOrWent(changed);
        }
        if (put.Count > 0)
        {
            Explorer.Reveal(put[^1]);
            // The keys go with the choice to the row revealed, for the next Paste.
            Explorer.FocusRows();
            refused.Insert(0, $"{(copy ? "Copied" : "Moved")} {Plural.Listed([.. put.Select(Path.GetFileName).OfType<string>()])} to {Path.GetFileName(into)}");
        }
        Explorer.Say(string.Join("; ", refused));
        return put;
    }

    /// <summary>Moves a file or folder, its open files following it; null once moved, else why not.</summary>
    private string? MoveOnDisk(string source, string target)
    {
        try
        {
            if (Directory.Exists(source))
            {
                Directory.Move(source, target);
            }
            else
            {
                File.Move(source, target);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }
        FollowMoved(Path.GetFullPath(source), Path.GetFullPath(target));
        return null;
    }

    /// <summary>Copies a file or a folder with all it holds, the open files among them saved first, so the copy is what the
    /// editor shows; null once copied, else why not.</summary>
    private string? CopyOnDisk(string source, string target)
    {
        string full = Path.GetFullPath(source);
        foreach (string open in documents.Keys.Where(p => PathNames.Within(p, full) && documents[p].Document.IsModified).ToList())
        {
            if (SourcePath == open)
            {
                SaveSource();
            }
        }
        try
        {
            if (Directory.Exists(source))
            {
                CopyFolder(source, target);
            }
            else
            {
                File.Copy(source, target);
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }
    }

    /// <summary>A folder's copy, a link inside it copied as the link it is rather than followed (it may loop).</summary>
    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (FileSystemInfo entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            string target = Path.Combine(to, entry.Name);
            if (entry.LinkTarget is { } link)
            {
                if (entry is DirectoryInfo)
                {
                    Directory.CreateSymbolicLink(target, link);
                }
                else
                {
                    File.CreateSymbolicLink(target, link);
                }
            }
            else if (entry is DirectoryInfo)
            {
                CopyFolder(entry.FullName, target);
            }
            else
            {
                File.Copy(entry.FullName, target);
            }
        }
    }

    private async Task CopyPathAsync(string path)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(path);
            Explorer.Say($"Copied {path}");
        }
    }

    // Following the files.

    /// <summary>The open files a move took elsewhere known by their new paths, the one shown too.</summary>
    private void FollowMoved(string from, string to)
    {
        foreach (string open in documents.Keys.Where(p => PathNames.Within(p, from)).ToList())
        {
            string now = PathNames.Moved(open, from, to);
            documents[now] = documents[open];
            documents[now].Path = now;
            documents.Remove(open);
            Vcs.Moved(open, now);
            FollowLanguage(now, documents[now].Document);
            if (SourcePath == open)
            {
                SourcePath = now;
                Title = $"Wayfarer Wanderer - {now}";
            }
        }
    }

    /// <summary>The open files a delete took away let go of: the one shown gives way to the next tab, or leaves the editor empty.</summary>
    private void Forget(string path)
    {
        string full = Path.GetFullPath(path);
        bool activeGone = false;
        Shell.Workspace.OpenDocument? next = null;
        foreach (string open in documents.Keys.Where(p => PathNames.Within(p, full)).ToList())
        {
            bool wasShown = SourcePath == open;
            if (documents.Remove(open, out var closed))
            {
                Shell.Workspace.OpenDocument? candidate = EditorTabs.Remove(closed);
                if (wasShown)
                {
                    activeGone = true;
                    next = candidate;
                }
                ForgetOutline(closed.Document);
                ForgetLanguage(closed.Document);
                DocumentClosed(closed);
            }
            else if (wasShown)
            {
                activeGone = true;
            }
        }
        if (activeGone)
        {
            if (next is not null && documents.ContainsKey(next.Path))
            {
                OpenSource(next.Path);
            }
            else
            {
                ClearEditor();
            }
        }
    }

    /// <summary>The Explorer's rows follow files that came or went at once, rather than when the folder's watcher hears.</summary>
    private void CameOrWent(IReadOnlyCollection<string> paths) =>
        Explorer.RefreshFolders(paths.Select(p => Path.GetDirectoryName(Path.GetFullPath(p))).OfType<string>().Select(PathNames.Canonical).Distinct(PathNames.Comparer));
}
