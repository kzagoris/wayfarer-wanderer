// From Passenger 8a1df7e4 src/Passenger.App/MainWindow.Tabs.cs, MIT (notices/Passenger/LICENSE): the editor's tab strip
// over Wayfarer's one editor, its actions (Next/Previous Tab, Pin Tab, Close Tab / Others / All / Unpinned / Left /
// Right, Reopen Closed Tab), its context menu and its hidden tabs overflow popup.
using Avalonia;
using Avalonia.Controls;
using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Editors;
using WayfarerWanderer.App.Shell.Settings;
using WayfarerWanderer.App.Shell.ToolWindows;
using WayfarerWanderer.App.Shell.Workspace;
using A = WayfarerWanderer.App.Shell.AppActions;

namespace WayfarerWanderer.App;

/// <summary>The editor's tab strip (<see cref="Shell.Editors.EditorTabs"/>): open files as tabs over (or beside) the
/// editor, each with its icon, name, modified dot, close button, pin and context menu; plus Next/Previous Tab, Pin Tab,
/// the close actions, Reopen Closed Tab, and the hidden tabs overflow popup.</summary>
public sealed partial class EditorWindow
{
    private const string TabsGroup = "Editor Tabs";
    private const int ClosedTabsKept = 50;

    private readonly List<ClosedTab> closedTabs = [];

    private sealed record ClosedTab(string Path, int Index, TextPosition Caret, Vector Scroll, bool Pinned);

    /// <summary>The strip of open editor tabs.</summary>
    public EditorTabs EditorTabs { get; private set; } = null!;

    /// <summary>The hidden-tabs popup while it is open: for tests.</summary>
    public HiddenTabsPopup? HiddenTabsShown => SearchShown as HiddenTabsPopup;

    private void AddEditorTabs()
    {
        EditorTabs = new EditorTabs { TabsPlace = SettingsKept.Value.TabsPlace() };
        EditorTabs.TabSelected += (_, document) =>
        {
            OpenSource(document.Path);
            Editor.Focus();
        };
        EditorTabs.CloseRequested += (_, document) => CloseTab(document);
        EditorTabs.HiddenTabsAsked += (_, _) => ShowHiddenTabs();
        EditorTabs.MenuOpening += (_, opening) => FillTabMenu(opening);
        EditorTabs.MaximizeRequested += (_, _) => ToolDock.ToggleHideAll();

        AddTab(A.NextTab, "Next Tab", _ => CycleTab(1), NeedsOpenTab);
        AddTab(A.PreviousTab, "Previous Tab", _ => CycleTab(-1), NeedsOpenTab);
        Actions.Registry.Add(new IdeAction(A.PinTab, "Pin Tab", TabsGroup, _ =>
        {
            if (ShownFile is { } shown)
            {
                EditorTabs.Pin(shown, !EditorTabs.IsPinned(shown));
            }
        })
        {
            WhyNot = NeedsOpenTab,
            TitleIn = _ => ShownFile is { } shown && EditorTabs.IsPinned(shown) ? "Unpin Tab" : "Pin Tab",
        });
        Actions.Registry.Add(new IdeAction(A.CloseTab, "Close Tab", "IDE", _ =>
        {
            if (ShownFile is { } shown)
            {
                CloseTab(shown);
            }
        })
        {
            WhyNot = NeedsOpenTab,
        });
        AddTab(A.CloseOtherTabs, "Close Other Tabs", _ =>
        {
            if (ShownFile is { } shown)
            {
                CloseOtherTabs(shown);
            }
        }, _ => Closes((_, tab, _) => tab.Document != ShownFile, "No other unpinned tab is open"));
        Func<ActionContext, string?> whyNotCloseAll = _ => Closes((_, _, _) => true, "Every tab is pinned");
        AddTab(A.CloseAllTabs, "Close All Tabs", _ => CloseAllTabs(), whyNotCloseAll);
        AddTab(A.CloseUnpinned, "Close All but Pinned", _ => CloseAllTabs(), whyNotCloseAll);
        AddTab(A.CloseTabsLeft, "Close Tabs to the Left", _ =>
        {
            if (ShownFile is { } shown)
            {
                CloseTabsToSide(shown, left: true);
            }
        }, _ => Closes((at, _, index) => index < at, "No unpinned tab is to the left"));
        AddTab(A.CloseTabsRight, "Close Tabs to the Right", _ =>
        {
            if (ShownFile is { } shown)
            {
                CloseTabsToSide(shown, left: false);
            }
        }, _ => Closes((at, _, index) => index > at, "No unpinned tab is to the right"));
        AddTab(A.ReopenClosedTab, "Reopen Closed Tab", _ =>
        {
            if (ReopenClosedTab() is { } why)
            {
                StatusText = why;
            }
        }, _ => closedTabs.Count > 0 ? null : "No tab has been closed");

        void AddTab(string id, string title, Action<ActionContext> run, Func<ActionContext, string?> whyNot) =>
            Actions.Registry.Add(new IdeAction(id, title, TabsGroup, run) { WhyNot = whyNot });

        string? NeedsOpenTab(ActionContext _) => ShownFile is null ? "No file is open" : null;

        string? Closes(Func<int, EditorTab, int, bool> which, string none)
        {
            if (ShownFile is not { } shown)
            {
                return "No file is open";
            }
            int at = EditorTabs.IndexOf(shown);
            return EditorTabs.Unpinned((tab, index) => which(at, tab, index)).Count > 0 ? null : none;
        }
    }

    /// <summary>Moves the editor's tab strip where Settings or the theme says.</summary>
    private void FollowEditorTabs(AppSettings settings) => EditorTabs.TabsPlace = settings.TabsPlace();

    /// <summary>Switches to the next or previous open tab on the strip.</summary>
    private void CycleTab(int step)
    {
        if (EditorTabs.Cycle(step) is { } next)
        {
            OpenSource(next.Path);
            Editor.Focus();
        }
    }

    /// <summary>Closes a tab, saving unsaved edits first as Passenger does; false if saving failed and the tab stays open.</summary>
    public bool CloseTab(OpenDocument document)
    {
        if (!EditorTabs.Has(document))
        {
            return true;
        }
        if (document.IsModified && TrySaveDocument(document) is { } unsaved)
        {
            StatusText = $"{document.Name} stays open, unsaved: {unsaved}";
            return false;
        }
        EditorTabs.Keep(Editor);
        int index = EditorTabs.IndexOf(document);
        bool pinned = EditorTabs.IsPinned(document);
        (SelectionSet selections, Vector scroll) = EditorTabs.ViewOf(document);
        TextPosition caret = document.Text.Snapshot.GetPosition(Math.Min(selections.Primary.Caret, document.Text.Snapshot.Length));
        bool wasShown = SourcePath is not null && PathNames.Same(SourcePath, document.Path);
        OpenDocument? next = EditorTabs.Remove(document);
        if (documents.Remove(document.Path))
        {
            ForgetOutline(document.Document);
            ForgetLanguage(document.Document);
            DocumentClosed(document);
        }
        closedTabs.Add(new ClosedTab(document.Path, index, caret, scroll, pinned));
        if (closedTabs.Count > ClosedTabsKept)
        {
            closedTabs.RemoveAt(0);
        }
        if (wasShown)
        {
            if (next is not null)
            {
                OpenSource(next.Path);
            }
            else
            {
                ClearEditor();
            }
        }
        return true;
    }

    /// <summary>Close Other Tabs: every unpinned tab but <paramref name="keep"/>, which stays shown.</summary>
    public void CloseOtherTabs(OpenDocument keep)
    {
        if (ShownFile != keep && EditorTabs.Has(keep))
        {
            OpenSource(keep.Path);
        }
        CloseTabs((tab, _) => tab.Document != keep);
    }

    /// <summary>Close All Tabs: every unpinned tab.</summary>
    public void CloseAllTabs() => CloseTabs((_, _) => true);

    /// <summary>Close Tabs to the Left or Right of <paramref name="of"/>, leaving pinned tabs open.</summary>
    public void CloseTabsToSide(OpenDocument of, bool left)
    {
        int at = EditorTabs.IndexOf(of);
        if (at < 0)
        {
            return;
        }
        if (ShownFile != of)
        {
            OpenSource(of.Path);
        }
        CloseTabs((_, index) => left ? index < at : index > at);
    }

    private void CloseTabs(Func<EditorTab, int, bool> which)
    {
        foreach (OpenDocument each in EditorTabs.Unpinned(which))
        {
            if (!CloseTab(each))
            {
                return;
            }
        }
    }

    /// <summary>Reopen Closed Tab: brings back the last closed tab at its former position, pinned if it was; returns null
    /// once opened or why none could be reopened.</summary>
    public string? ReopenClosedTab()
    {
        while (closedTabs.Count > 0)
        {
            ClosedTab closed = closedTabs[^1];
            closedTabs.RemoveAt(closedTabs.Count - 1);
            if (!File.Exists(closed.Path))
            {
                continue;
            }
            try
            {
                bool had = documents.ContainsKey(closed.Path);
                if (!had)
                {
                    FileText file = FileText.Load(closed.Path) ?? throw new InvalidDataException($"Cannot open binary file '{closed.Path}'.");
                    OpenDocument opened = new(closed.Path, new TextDocument(file.Text), file);
                    documents[closed.Path] = opened;
                    FileOpened(opened);
                    TextSnapshot text = opened.Text.Snapshot;
                    TextPosition at = new(Math.Clamp(closed.Caret.Line, 0, text.LineCount - 1), Math.Max(0, closed.Caret.Column));
                    EditorTabs.Add(opened, closed.Index, closed.Pinned);
                    EditorTabs.Remember(opened, SelectionSet.Single(text.GetOffset(at)), closed.Scroll);
                    OpenSource(closed.Path);
                    opened.Decorations = Decorated(opened.Document);
                    DocumentOpened(opened);
                }
                else
                {
                    OpenSource(closed.Path);
                }
                Editor.Focus();
                return null;
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }
        return "No closed tab can be reopened";
    }

    /// <summary>Leaves the editor empty once its last tab closes or its folder closes.</summary>
    private void ClearEditor()
    {
        Editor.Document = new TextDocument();
        Editor.Highlighter = null;
        Editor.IsVisible = false;
        SourcePath = null;
        ShowBlameOf(null);
        Title = Session is { } session
            ? $"Wayfarer Wanderer - {Path.GetFileName(Path.TrimEndingDirectorySeparator(session.Root))}"
            : "Wayfarer Wanderer";
        ShowWelcomeWhenEmpty();
    }

    /// <summary>Closes every open document and clears the tab strip (switching workspace or closing the project).</summary>
    private void ResetOpenDocuments()
    {
        foreach (OpenDocument open in documents.Values.ToList())
        {
            ForgetLanguage(open.Document);
            DocumentClosed(open);
        }
        documents.Clear();
        ClearOutlines();
        EditorTabs.Clear();
        closedTabs.Clear();
        if (SourcePath is not null)
        {
            ClearEditor();
        }
    }

    /// <summary>The tabs the strip hides, listed under its button, the one chosen gone to.</summary>
    private void ShowHiddenTabs()
    {
        List<OpenDocument> hidden = [.. EditorTabs.HiddenTabs.Select(tab => tab.Document)];
        if (hidden.Count == 0)
        {
            return;
        }
        Button button = EditorTabs.HiddenTabsButton;
        Point? under = button.TranslatePoint(new Point(button.Bounds.Width - PopupFrame.Medium, button.Bounds.Height), this);
        HiddenTabsPopup popup = new(hidden, Session?.Root);
        ShowSearch(popup, popup, chosen =>
        {
            if (chosen is not null && EditorTabs.Has(chosen.Document))
            {
                OpenSource(chosen.Document.Path);
                Editor.Focus();
            }
        }, under);
    }

    /// <summary>A tab's context menu, acting on the clicked tab: close, close others/all/unpinned/left/right, pin, copy path,
    /// open in file manager, reopen closed tab.</summary>
    private void FillTabMenu(TabMenu opening)
    {
        OpenDocument file = opening.Document;
        int at = EditorTabs.IndexOf(file);
        bool pinned = EditorTabs.IsPinned(file);
        opening.Menu.Items.Clear();
        AddItem("Close", A.CloseTab, () => CloseTab(file), null);
        AddItem("Close Other Tabs", A.CloseOtherTabs, () => CloseOtherTabs(file),
            EditorTabs.Unpinned((tab, _) => tab.Document != file).Count > 0 ? null : "No other unpinned tab is open");
        string? noUnpinned = EditorTabs.Unpinned((_, _) => true).Count > 0 ? null : "Every tab is pinned";
        AddItem("Close All Tabs", A.CloseAllTabs, CloseAllTabs, noUnpinned);
        AddItem("Close All but Pinned", A.CloseUnpinned, CloseAllTabs, noUnpinned);
        AddItem("Close Tabs to the Left", A.CloseTabsLeft, () => CloseTabsToSide(file, left: true),
            EditorTabs.Unpinned((_, index) => index < at).Count > 0 ? null : "No unpinned tab is to the left");
        AddItem("Close Tabs to the Right", A.CloseTabsRight, () => CloseTabsToSide(file, left: false),
            EditorTabs.Unpinned((_, index) => index > at).Count > 0 ? null : "No unpinned tab is to the right");
        opening.Menu.Items.Add(new Separator());
        AddItem(pinned ? "Unpin Tab" : "Pin Tab", A.PinTab, () => EditorTabs.Pin(file, !pinned), null);
        opening.Menu.Items.Add(new Separator());
        AddItem("Copy Path", A.CopyPath, () => IdeErrors.Observe(CopyPathAsync(file.Path)), null);
        MenuItem openIn = new() { Header = "Open In" };
        MenuItem reveal = ListMenus.Item(FileManager.Showing, () => FileManager.Show(this, file.Path));
        Actions.Registry.ShowKeys(reveal, A.RevealInFileManager);
        openIn.Items.Add(reveal);
        opening.Menu.Items.Add(openIn);
        opening.Menu.Items.Add(new Separator());
        AddItem("Reopen Closed Tab", A.ReopenClosedTab, () =>
        {
            if (ReopenClosedTab() is { } why)
            {
                StatusText = why;
            }
        }, closedTabs.Count > 0 ? null : "No tab has been closed");

        void AddItem(string header, string actionId, Action run, string? whyNot)
        {
            MenuItem item = ListMenus.Item(header, run, whyNot);
            Actions.Registry.ShowKeys(item, actionId);
            opening.Menu.Items.Add(item);
        }
    }
}
