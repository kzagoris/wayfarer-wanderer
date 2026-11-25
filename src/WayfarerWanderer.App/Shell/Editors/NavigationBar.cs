// From Passenger 8a1df7e4 src/Passenger.App/Editors/NavigationBar.cs, MIT (notices/Passenger/LICENSE): over Wayfarer's one
// editor rather than each group's; its outline is the one the status bar's path asks the Java and Kotlin servers for
// (EditorWindow.Breadcrumbs) rather than a file's own parse; without a screen reader's words, which Wayfarer has no
// announcer for yet.
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell.Navigation;
using WayfarerWanderer.App.Shell.ToolWindows;

namespace WayfarerWanderer.App.Shell.Editors;

/// <summary>A list a navigation bar's part opens: what it is called, the rows it offers to go to, the one chosen as it
/// opens, and what it opens under.</summary>
public sealed record NavigationList(string Title, IReadOnlyList<SymbolRow> Rows, int Chosen, Control Under);

/// <summary>The bar over the editor naming where its caret is in the file's outline, outermost first (a Java or Kotlin
/// file's package, type and member), each part a button listing what stands beside it to go to, and after them, where the
/// caret is in none of what the innermost holds, a mark listing that. The bar follows the caret once it rests in another
/// part, so a key moving within one costs nothing. Shown while the setting has it and the file has an outline with
/// something in it, a narrow editor shortening its parts, the outermost going first (<see cref="PathCrumbs"/>). Left and
/// Right move along it, Down opens a part's list as Enter and Space do, Escape gives the editor the keys back.</summary>
public sealed class NavigationBar
{
    /// <summary>What a screen reader calls the bar.</summary>
    public const string Said = "Navigation bar";

    /// <summary>Before the bar follows the caret into another part of the file's outline.</summary>
    private static readonly TimeSpan FollowPause = TimeSpan.FromMilliseconds(100);

    private readonly CodeEditor editor;
    private readonly PathCrumbs crumbs = new() { VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true, Margin = new Thickness(Gaps.Small, 0) };
    private readonly List<Part> parts = [];
    private readonly Debounce follow;
    private bool on = true;

    /// <summary>The file shown, null for none.</summary>
    private string? file;

    private Outline<SymbolRow> outline = Outline<SymbolRow>.Empty;

    /// <summary>Whether <see cref="outline"/> is the shown file's.</summary>
    private bool outlined;

    /// <summary>The innermost row holding the caret's line as the path shows it, or will once the caret has rested.</summary>
    private int awaited = -2;

    /// <summary>The rows the path shows, outermost first, and the row whose own rows its last mark lists (-1 the file's
    /// top), null where it shows no such mark.</summary>
    private SymbolRow[] path = [];
    private int[] pathAt = [];
    private int? listed;

    /// <summary>Whether the keys, rather than the pointer, last pressed in the bar: a list they open leaves the keys to
    /// come back to the bar, one the pointer opened to the editor.</summary>
    private bool keyed;

    public NavigationBar(CodeEditor editor)
    {
        this.editor = editor;
        Bar = ToolBars.OverEditor(crumbs);
        Bar.IsVisible = false;
        AutomationProperties.SetName(Bar, Said);
        Bar.AddHandler(InputElement.KeyDownEvent, (_, _) => keyed = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        Bar.AddHandler(InputElement.PointerPressedEvent, (_, _) => keyed = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        Bar.AddHandler(InputElement.KeyDownEvent, OnKeyDown);
        follow = new Debounce(FollowPause, () =>
        {
            ShowPath();
            return Task.CompletedTask;
        });
        editor.SelectionChanged += (_, _) => CaretMoved();
    }

    /// <summary>The bar, docked over the editor.</summary>
    public Border Bar { get; }

    /// <summary>A part asked for its list, to show under it.</summary>
    public event EventHandler<NavigationList>? ListAsked;

    /// <summary>Whether the setting shows the bar.</summary>
    public bool IsOn
    {
        get => on;
        set
        {
            on = value;
            Bar.IsVisible = on && HasOutline;
        }
    }

    /// <summary>The names of the parts the path shows, outermost first.</summary>
    public IReadOnlyList<string> Shown => [.. path.Select(row => row.Name)];

    /// <summary>Whether the bar shows and names where the caret is in the file's outline, so the status bar's path need not.</summary>
    public bool NamesOutline => Bar.IsVisible && HasOutline;

    /// <summary>Whether the bar has the shown file's outline, with something in it, shown or not.</summary>
    public bool HasOutline => outlined && outline.Rows.Count > 0;

    /// <summary>The buttons the bar shows: its parts' in their order and then its last mark's where it shows one.</summary>
    public IReadOnlyList<Button> Buttons => [.. parts.Where(p => p.Button.IsVisible).Select(p => p.Button)];

    /// <summary>Whether the bar's last mark lists what the innermost part holds (the file's top where it shows no part).</summary>
    public bool ListsMore => listed is not null;

    /// <summary>The file the editor shows now; null for none.</summary>
    public void Show(string? shown)
    {
        if (shown != file)
        {
            follow.Stop();
            outline = Outline<SymbolRow>.Empty;
            outlined = false;
            awaited = -2;
        }
        file = shown;
        Bar.IsVisible = on && HasOutline;
        ShowPath(force: true);
    }

    /// <summary>The shown file's outline as its server read it, its rows in the file's order each under its container
    /// (<see cref="SymbolRow.Depth"/>); none where no server outlines the file.</summary>
    public void ShowOutline(string forFile, IReadOnlyList<SymbolRow>? rows)
    {
        if (forFile != file)
        {
            return;
        }
        outline = rows is null ? Outline<SymbolRow>.Empty : OutlineOf(rows);
        outlined = rows is not null;
        Bar.IsVisible = on && HasOutline;
        ShowPath(force: true);
    }

    /// <summary>Rows each under the last row before it a level up, as an outline. A file-level package or namespace
    /// declaration that the language server reports as a sibling of its types (spanning only its own line, as Java and
    /// Kotlin servers do) encloses the top-level declarations after it, as C#'s file-scoped namespace does in Passenger.</summary>
    public static Outline<SymbolRow> OutlineOf(IReadOnlyList<SymbolRow> rows)
    {
        List<Node> roots = [];
        List<Node> open = [];
        foreach (SymbolRow row in rows)
        {
            Node node = new(row, []);
            while (open.Count > 0 && open[^1].Row.Depth >= row.Depth)
            {
                open.RemoveAt(open.Count - 1);
            }
            (open.Count > 0 ? open[^1].Children : roots).Add(node);
            open.Add(node);
        }
        if (roots.Count > 1 && roots[0].Children.Count == 0 && roots[0].Row.Kind is SymbolKind.Package or SymbolKind.Namespace or SymbolKind.Module)
        {
            Node scope = roots[0];
            int endLine = rows.Max(r => Math.Max(r.Position.Line, r.EndLine ?? r.Position.Line));
            scope = new Node(scope.Row with { EndLine = Math.Max(scope.Row.EndLine ?? scope.Row.Position.Line, endLine) }, [.. roots.Skip(1)]);
            roots = [scope];
        }
        return Outline<SymbolRow>.Of<Node>(roots, n => n.Children, (n, _) => (n.Row, n.Row.Position.Line, Math.Max(n.Row.Position.Line, n.Row.EndLine ?? n.Row.Position.Line)));
    }

    private sealed record Node(SymbolRow Row, List<Node> Children);

    /// <summary>The caret moved: once it rests in another part of the outline, the path follows it.</summary>
    private void CaretMoved()
    {
        if (!outlined || file is null)
        {
            return;
        }
        int innermost = outline.InnermostAt(editor.CaretPosition.Line);
        if (innermost != awaited)
        {
            awaited = innermost;
            follow.Restart();
        }
    }

    /// <summary>Where the caret's line is in an outline: the rows holding it, outermost first, and the row whose own rows
    /// the last mark lists, where the innermost holds rows (the file's top, -1, where none holds the line).</summary>
    private static (int[] Path, int? Listed) Where(Outline<SymbolRow> outline, int line)
    {
        int innermost = outline.InnermostAt(line);
        int depth = 0;
        for (int i = innermost; i >= 0; i = outline.ParentOf(i))
        {
            depth++;
        }
        int[] at = new int[depth];
        for (int i = innermost, place = depth - 1; i >= 0; i = outline.ParentOf(i), place--)
        {
            at[place] = i;
        }
        bool holds = innermost + 1 < outline.Rows.Count && outline.ParentOf(innermost + 1) == innermost;
        return (at, holds ? innermost : null);
    }

    /// <summary>The path to where the caret is, the bar's buttons left as they are where it is the one shown.</summary>
    private void ShowPath(bool force = false)
    {
        (int[] at, int? lists) = outlined && outline.Rows.Count > 0 ? Where(outline, editor.CaretPosition.Line) : ([], null);
        awaited = at.Length > 0 ? at[^1] : -1;
        if (!force && lists == listed && at.Length == path.Length && at.AsSpan().SequenceEqual(pathAt) && Same(at))
        {
            return;
        }
        pathAt = at;
        path = [.. at.Select(i => outline.Rows[i])];
        listed = lists;
        for (int i = 0; i < path.Length; i++)
        {
            PartAt(i).ShowRow(path[i], ListTitle(outline, outline.ParentOf(at[i])), first: i == 0);
        }
        if (lists is { } parent)
        {
            PartAt(path.Length).ShowMore(ListTitle(outline, parent), after: path.Length > 0);
        }
        for (int i = path.Length + (lists is null ? 0 : 1); i < parts.Count; i++)
        {
            parts[i].Hide();
        }

        bool Same(int[] rows)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (!ReferenceEquals(outline.Rows[rows[i]], path[i]))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>What the list of a row's own rows is called: a type's members, else what is in it; the file's top level by
    /// the file's name.</summary>
    private string ListTitle(Outline<SymbolRow> from, int parent)
    {
        if (parent < 0)
        {
            return file is null ? string.Empty : System.IO.Path.GetFileName(file);
        }
        SymbolRow row = from.Rows[parent];
        return row.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum ? $"Members of {row.Name}" : $"In {row.Name}";
    }

    /// <summary>A part of the bar, made as the longest path shown needs it, with the mark before it.</summary>
    private Part PartAt(int index)
    {
        while (parts.Count <= index)
        {
            int at = parts.Count;
            Part made = new();
            made.Button.Click += (_, _) => Open(at, made.Button);
            crumbs.Children.Add(made.Mark);
            crumbs.Children.Add(made.Button);
            parts.Add(made);
        }
        return parts[index];
    }

    /// <summary>A part's list: the rows beside the part's own, it chosen; or those the last mark lists.</summary>
    private void Open(int index, Control under)
    {
        if (!outlined || (index < pathAt.Length ? outline.ParentOf(pathAt[index]) : listed) is not { } parent)
        {
            return;
        }
        Offer(outline, parent, index < pathAt.Length ? pathAt[index] : -1, under, keyed);
    }

    /// <summary>Asks the window to list a row's own rows (the file's top level for -1) under a control, one of them chosen.</summary>
    private void Offer(Outline<SymbolRow> from, int parent, int chosen, Control under, bool byKeys)
    {
        if (!byKeys)
        {
            editor.Focus();
        }
        IReadOnlyList<int> children = from.ChildrenOf(parent);
        SymbolRow[] rows = [.. children.Select(i => from.Rows[i] with { Depth = 0 })];
        int first = 0;
        for (int i = 0; i < children.Count; i++)
        {
            first = children[i] == chosen ? i : first;
        }
        ListAsked?.Invoke(this, new NavigationList(ListTitle(from, parent), rows, first, under));
    }

    /// <summary>Gives the keys to the bar's last button, as by Tab; false where it shows none.</summary>
    public bool TakeKeys() => Bar.IsEffectivelyVisible && Buttons is { Count: > 0 } shown && shown[^1].Focus(NavigationMethod.Tab);

    /// <summary>Jump to Navigation Bar: the keys to its last button; with the bar hidden (the setting off), the list that
    /// button would open, under the editor's top. False where the file has no outline to list.</summary>
    public bool JumpTo()
    {
        if (TakeKeys())
        {
            return true;
        }
        if (!outlined || outline.Rows.Count == 0)
        {
            return false;
        }
        (int[] at, int? lists) = Where(outline, editor.CaretPosition.Line);
        if (lists is { } parent)
        {
            Offer(outline, parent, -1, editor, byKeys: false);
        }
        else if (at.Length > 0)
        {
            Offer(outline, outline.ParentOf(at[^1]), at[^1], editor, byKeys: false);
        }
        else
        {
            Offer(outline, -1, -1, editor, byKeys: false);
        }
        return true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        IReadOnlyList<Button> shown = Buttons;
        int at = -1;
        for (int i = 0; i < shown.Count; i++)
        {
            at = shown[i].IsKeyboardFocusWithin ? i : at;
        }
        if (at < 0 || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        int to = e.Key switch
        {
            Key.Left => Math.Max(0, at - 1),
            Key.Right => Math.Min(shown.Count - 1, at + 1),
            Key.Home => 0,
            Key.End => shown.Count - 1,
            _ => -1,
        };
        if (to >= 0)
        {
            shown[to].Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            int index = parts.FindIndex(p => p.Button == shown[at]);
            Open(index, shown[at]);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            editor.Focus(NavigationMethod.Tab);
            e.Handled = true;
        }
    }

    /// <summary>A member's name as the bar and status bar show it: without the parameters or type its outline name may give.</summary>
    internal static string NameOf(string name) => name.IndexOfAny(['(', ':', '<']) is var cut and > 0 ? name[..cut].TrimEnd() : name;

    /// <summary>A part's mark and button: a row's kind badge and name, its tip and accessible words naming the list it
    /// opens; or, as the last mark, the mark alone, named by its list.</summary>
    private sealed class Part
    {
        private readonly Border badge;
        private readonly TextBlock letter;
        private readonly TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        private string? badgeBrush;

        public Part()
        {
            badge = Tags.Kind(out letter);
            badge.Margin = new Thickness(0, 0, Gaps.Tight, 0);
            Button = new Button { Classes = { StyleClasses.Crumb }, Content = new DockPanel { Children = { badge, name } } };
        }

        public TextBlock Mark { get; } = new() { Text = "›", Classes = { StyleClasses.Status }, Margin = new Thickness(Gaps.Tight, 0), VerticalAlignment = VerticalAlignment.Center };

        public Button Button { get; }

        public void ShowRow(SymbolRow row, string list, bool first)
        {
            SymbolPopup.ShowBadge(badge, letter, ref badgeBrush, row.Kind);
            badge.IsVisible = true;
            name.Text = NameOf(row.Name);
            Mark.IsVisible = !first;
            Button.IsVisible = true;
            Button.Margin = default;
            AutomationProperties.SetName(Button, $"{NameOf(row.Name)}, {row.Kind.ToString().ToLowerInvariant()}");
            AutomationProperties.SetHelpText(Button, list);
            ToolTip.SetTip(Button, list);
        }

        public void ShowMore(string list, bool after)
        {
            badge.IsVisible = false;
            name.Text = "›";
            // The mark is the button itself, standing where a part's mark would.
            Mark.IsVisible = false;
            Button.IsVisible = true;
            Button.Margin = after ? new Thickness(Gaps.Tight, 0, 0, 0) : default;
            AutomationProperties.SetName(Button, list);
            AutomationProperties.SetHelpText(Button, list);
            ToolTip.SetTip(Button, list);
        }

        public void Hide()
        {
            Mark.IsVisible = false;
            Button.IsVisible = false;
        }
    }
}
