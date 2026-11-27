// From Passenger 494009d7 src/Passenger.App/Editors/EditorGroup.cs, MIT (notices/Passenger/LICENSE): the tab strip over
// Wayfarer's one editor, its tabs' look (chosen mark, modified dot, close button, pin, side placement and hidden tabs
// overflow) matching Passenger's; splits, previews and drag-to-split cut.
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell.Explorer;
using WayfarerWanderer.App.Shell.Workspace;

namespace WayfarerWanderer.App.Shell.Editors;

/// <summary>A tab on the editor's strip: an open file, whether it is the one shown, and whether it is pinned.</summary>
public sealed class EditorTab(OpenDocument document) : Observable
{
    private bool isActive;
    private bool isPinned;

    public OpenDocument Document { get; } = document;

    public bool IsActive
    {
        get => isActive;
        set => Set(ref isActive, value);
    }

    /// <summary>Pin Tab: it stands left of the unpinned tabs, and Close Others, Close All and closing by side leave it
    /// open.</summary>
    public bool IsPinned
    {
        get => isPinned;
        set => Set(ref isPinned, value);
    }
}

/// <summary>A tab's context menu opening, for the window to fill with actions on that tab.</summary>
public sealed record TabMenu(OpenDocument Document, ContextMenu Menu);

/// <summary>The strip of open editor tabs: the pinned ones first, scrolling as they crowd it with a button listing those
/// out of sight, across the top or down the left or right side as Settings and the theme say.</summary>
public sealed class EditorTabs
{
    /// <summary>The most of the editor's width side tabs take, so a narrow window keeps the rest for the code.</summary>
    private const double SideTabsShare = 1.0 / 3;

    private readonly ObservableCollection<EditorTab> tabs = [];

    /// <summary>The tabs shown, the latest first, each once and only while it is on the strip: what the strip goes back to
    /// as its shown tab closes.</summary>
    private readonly List<EditorTab> shownOrder = [];
    private readonly Dictionary<OpenDocument, (SelectionSet Selections, Vector Scroll)> views = [];
    private readonly Border tabBar;
    private readonly ItemsControl strip;
    private readonly ScrollViewer stripView;

    /// <summary>At the strip's end, shown while tabs are out of its sight: the list of them.</summary>
    private readonly Button hiddenTabs;

    private EditorTab? active;
    private EditorTabsPlace tabsPlace;

    public EditorTabs()
    {
        strip = new ItemsControl
        {
            ItemsSource = tabs,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal }),
        };
        ReusedRows.Use(strip, Tab);
        stripView = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = strip,
        };
        // A narrower strip may leave the active tab out of sight.
        stripView.SizeChanged += (_, _) => RevealActive();
        hiddenTabs = ToolButtons.Of(IconKeys.ChevronDown, "Hidden Tabs", () => HiddenTabsAsked?.Invoke(this, EventArgs.Empty));
        hiddenTabs.Classes.Add(StyleClasses.TabStripTool);
        hiddenTabs.Name = "HiddenTabs";
        hiddenTabs.IsVisible = false;
        hiddenTabs[DockPanel.DockProperty] = Dock.Right;
        // Tabs come, go, widen or scroll: the list's button shows while some are out of sight.
        stripView.ScrollChanged += (_, _) => hiddenTabs.IsVisible = HiddenTabs.Count > 0;
        tabBar = new Border
        {
            Classes = { StyleClasses.TabStrip },
            [DockPanel.DockProperty] = Dock.Top,
            IsVisible = false,
            Child = new Panel
            {
                Children =
                {
                    new DockPanel { Children = { hiddenTabs, stripView } },
                    new ThemeBackdrop(BackgroundPlaces.Frame),
                },
            },
        };
    }

    /// <summary>The strip docked over (or beside) the editor.</summary>
    public Border TabStrip => tabBar;

    /// <summary>The button at the strip's end that lists the tabs out of its sight, shown while there are some.</summary>
    public Button HiddenTabsButton => hiddenTabs;

    /// <summary>The open tabs, in their order on the strip.</summary>
    public IReadOnlyList<EditorTab> Tabs => tabs;

    /// <summary>The active tab's file, or null while none is shown.</summary>
    public OpenDocument? Active => active?.Document;

    /// <summary>A tab clicked to be shown.</summary>
    public event EventHandler<OpenDocument>? TabSelected;

    /// <summary>A tab asked to close (its × or middle click).</summary>
    public event EventHandler<OpenDocument>? CloseRequested;

    /// <summary>The hidden-tabs overflow button clicked.</summary>
    public event EventHandler? HiddenTabsAsked;

    /// <summary>A tab's context menu opening.</summary>
    public event EventHandler<TabMenu>? MenuOpening;

    /// <summary>A tab double-clicked: toggle hiding tool windows.</summary>
    public event EventHandler? MaximizeRequested;

    public bool Has(OpenDocument document) => tabs.Any(t => t.Document == document);

    public int IndexOf(OpenDocument document)
    {
        for (int i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].Document == document)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>The tabs out of the strip's sight, wholly or in part, in its order.</summary>
    public IReadOnlyList<EditorTab> HiddenTabs
    {
        get
        {
            bool down = tabsPlace != EditorTabsPlace.Top;
            double from = down ? stripView.Offset.Y : stripView.Offset.X;
            double to = from + (down ? stripView.Viewport.Height : stripView.Viewport.Width);
            return [.. tabs.Where(tab => strip.ContainerFromItem(tab) is not Control shown
                || (down ? shown.Bounds.Y : shown.Bounds.X) < from - 0.5 || (down ? shown.Bounds.Bottom : shown.Bounds.Right) > to + 0.5)];
        }
    }

    /// <summary>Where the tabs stand: a row across the top that scrolls, or a column down the left or right side.</summary>
    public EditorTabsPlace TabsPlace
    {
        get => tabsPlace;
        set
        {
            if (value == tabsPlace)
            {
                return;
            }
            tabsPlace = value;
            bool down = value != EditorTabsPlace.Top;
            tabBar[DockPanel.DockProperty] = value switch
            {
                EditorTabsPlace.Left => Dock.Left,
                EditorTabsPlace.Right => Dock.Right,
                _ => Dock.Top,
            };
            tabBar.Classes.Set(StyleClasses.SideTabs, down);
            strip.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = down ? Orientation.Vertical : Orientation.Horizontal });
            stripView.HorizontalScrollBarVisibility = down ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
            stripView.VerticalScrollBarVisibility = down ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            hiddenTabs[DockPanel.DockProperty] = down ? Dock.Bottom : Dock.Right;
            tabBar.Styles.Clear();
            if (down)
            {
                tabBar.Styles.Add(SideShadows(value));
            }
            // Every tab made again for the new direction.
            ReusedRows.Use(strip, Tab);
            RevealActive();
        }
    }

    /// <summary>Caps side tabs to a third of the editor's width, matching Passenger's EditorGroup.</summary>
    internal void UpdateSideWidth(double availableWidth)
    {
        double most = tabsPlace == EditorTabsPlace.Top ? double.PositiveInfinity : availableWidth * SideTabsShare;
        if (!tabBar.MaxWidth.Equals(most))
        {
            tabBar.MaxWidth = most;
        }
    }

    /// <summary>Adds a file's tab after the active one (or at an index), without showing it: among the pinned tabs if it
    /// is pinned, else after them.</summary>
    public void Add(OpenDocument document, int? index = null, bool pinned = false)
    {
        if (Has(document))
        {
            return;
        }
        int at = index ?? (active is null ? tabs.Count : tabs.IndexOf(active) + 1);
        int fence = tabs.Count(t => t.IsPinned);
        tabs.Insert(pinned ? Math.Clamp(at, 0, fence) : Math.Clamp(at, fence, tabs.Count), new EditorTab(document) { IsPinned = pinned });
        document.Text.Changed += OnTabTextChanged;
    }

    /// <summary>Makes a file's tab active, adding it if it is not yet on the strip, and shows the strip.</summary>
    public void Show(OpenDocument document)
    {
        Add(document);
        if (active is not null)
        {
            active.IsActive = false;
        }
        active = tabs.First(t => t.Document == document);
        active.IsActive = true;
        shownOrder.Remove(active);
        shownOrder.Insert(0, active);
        tabBar.IsVisible = true;
        RevealActive();
    }

    /// <summary>Takes a file's tab away, returning the file to show next (the latest shown before, else its neighbour,
    /// else null when it was the last); hides the strip once empty.</summary>
    public OpenDocument? Remove(OpenDocument document)
    {
        if (tabs.FirstOrDefault(t => t.Document == document) is not { } tab)
        {
            return active?.Document;
        }
        int index = tabs.IndexOf(tab);
        views.Remove(document);
        tabs.Remove(tab);
        shownOrder.Remove(tab);
        document.Text.Changed -= OnTabTextChanged;
        if (tab != active)
        {
            return active?.Document;
        }
        active = null;
        if (tabs.Count > 0)
        {
            return (shownOrder.Count > 0 ? shownOrder[0] : tabs[Math.Min(index, tabs.Count - 1)]).Document;
        }
        tabBar.IsVisible = false;
        hiddenTabs.IsVisible = false;
        return null;
    }

    /// <summary>Clears every tab and hides the strip.</summary>
    public void Clear()
    {
        foreach (EditorTab tab in tabs)
        {
            tab.Document.Text.Changed -= OnTabTextChanged;
        }
        tabs.Clear();
        shownOrder.Clear();
        views.Clear();
        active = null;
        tabBar.IsVisible = false;
        hiddenTabs.IsVisible = false;
    }

    public bool IsPinned(OpenDocument document) => tabs.FirstOrDefault(t => t.Document == document)?.IsPinned == true;

    /// <summary>Pins a tab, which goes to the end of the pinned ones, or unpins it, which goes to just after them.</summary>
    public void Pin(OpenDocument document, bool pinned)
    {
        if (tabs.FirstOrDefault(t => t.Document == document) is not { } tab || tab.IsPinned == pinned)
        {
            return;
        }
        tab.IsPinned = pinned;
        tabs.Move(tabs.IndexOf(tab), tabs.Count(t => t.IsPinned && t != tab));
    }

    /// <summary>The tab a step from the active one, wrapping around the strip; null with fewer than two tabs.</summary>
    public OpenDocument? Cycle(int step)
    {
        if (active is null || tabs.Count < 2)
        {
            return null;
        }
        return tabs[(tabs.IndexOf(active) + step + tabs.Count) % tabs.Count].Document;
    }

    /// <summary>The unpinned tabs matching <paramref name="which"/>, in strip order.</summary>
    public IReadOnlyList<OpenDocument> Unpinned(Func<EditorTab, int, bool> which) =>
        [.. tabs.Select((tab, index) => (tab, index)).Where(t => !t.tab.IsPinned && which(t.tab, t.index)).Select(t => t.tab.Document)];

    /// <summary>Keeps the active tab's carets and scroll before switching away.</summary>
    public void Keep(CodeEditor editor)
    {
        if (active is { } shown && ReferenceEquals(editor.Document, shown.Document.Text))
        {
            views[shown.Document] = (editor.Selections, editor.ScrollOffset);
        }
    }

    /// <summary>Puts a tab's carets and scroll where they were kept, for when it is shown.</summary>
    public void Remember(OpenDocument document, SelectionSet selections, Vector scroll = default) => views[document] = (selections, scroll);

    /// <summary>The kept carets and scroll for a tab, or the start of the file for one not yet shown.</summary>
    public (SelectionSet Selections, Vector Scroll) ViewOf(OpenDocument document) =>
        views.TryGetValue(document, out (SelectionSet Selections, Vector Scroll) view) ? view : (SelectionSet.Single(0), default);

    /// <summary>A hidden tab's kept carets follow edits to its text meanwhile (e.g. a workspace rename).</summary>
    private void OnTabTextChanged(object? sender, DocumentChangedEventArgs e)
    {
        EditorTab? tab = null;
        for (int i = 0; i < tabs.Count && tab is null; i++)
        {
            tab = tabs[i].Document.Text == sender ? tabs[i] : null;
        }
        if (tab is not null && tab != active && views.TryGetValue(tab.Document, out (SelectionSet Selections, Vector Scroll) view))
        {
            views[tab.Document] = (TextEditing.MapThrough(view.Selections, e.Edits).Clamp(e.After.Length), view.Scroll);
        }
    }

    /// <summary>The active tab scrolled into the strip's sight once it is laid out.</summary>
    private void RevealActive() => Dispatcher.UIThread.Post(() =>
    {
        if (active is not null && strip.ContainerFromItem(active) is { } tab)
        {
            tab.BringIntoView();
        }
    }, DispatcherPriority.Loaded);

    /// <summary>The theme's open tab down a side: an inset shadow along its top turned to the side toward the editor.</summary>
    private Styles SideShadows(EditorTabsPlace place)
    {
        Style activeStyle = new(x => x.OfType<Border>().Class(StyleClasses.Tab).Class(StyleClasses.Active));
        activeStyle.Setters.Add(new Setter(Border.BoxShadowProperty,
            tabBar.GetResourceObservable(TokenKeys.TabShadow, shadow => shadow is BoxShadows shadows ? Turned(shadows) : shadow).ToBinding()));
        return [activeStyle];

        BoxShadows Turned(BoxShadows shadows)
        {
            BoxShadow[] each = [.. Enumerable.Range(0, shadows.Count).Select(i => shadows[i] is { IsInset: true } inset
                ? inset with { OffsetX = place == EditorTabsPlace.Left ? -inset.OffsetY : inset.OffsetY, OffsetY = place == EditorTabsPlace.Left ? inset.OffsetX : -inset.OffsetX }
                : shadows[i])];
            return each.Length == 0 ? shadows : new BoxShadows(each[0], each[1..]);
        }
    }

    /// <summary>A tab for whichever file the strip hands it: its icon, its name, a pin while pinned, and its × or, while
    /// changed, a dot; its menu, close, pin and presses act on the file it shows.</summary>
    private Control Tab()
    {
        RowIcon icon = new();
        TextBlock name = new() { Classes = { StyleClasses.TabName }, VerticalAlignment = VerticalAlignment.Center };
        Ellipse modified = new() { Classes = { StyleClasses.Modified }, Width = 7, Height = 7 };
        Button close = ToolButtons.Small(IconKeys.Close, "Close");
        close.Classes.Add(StyleClasses.TabClose);
        Button pin = ToolButtons.Small(IconKeys.Pin, "Unpin Tab");
        Panel ends = new() { Width = 16, Height = 16, Children = { modified, close } };
        bool down = tabsPlace != EditorTabsPlace.Top;
        Border border = new() { Classes = { StyleClasses.Tab } };
        // Down a side, the name takes the column's width left over, its close at the column's edge.
        ChosenMark.Hold(border, down
            ? new DockPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    Docked(icon, Dock.Left, 0), Docked(ends, Dock.Right, 6), Docked(pin, Dock.Right, 6), Docked(name, Dock.Left, 6),
                },
            }
            : new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Gaps.Small,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { icon, name, pin, ends },
            });
        OpenDocument? Shown() => (border.DataContext as EditorTab)?.Document;
        close.Click += (_, e) =>
        {
            if (Shown() is { } document)
            {
                CloseRequested?.Invoke(this, document);
            }
            e.Handled = true;
        };
        pin.Click += (_, e) =>
        {
            if (Shown() is { } document)
            {
                Pin(document, false);
            }
            e.Handled = true;
        };
        void Paint(EditorTab tab)
        {
            OpenDocument document = tab.Document;
            name.Text = document.Name;
            icon.Data = FileIcons.ForFile(document.Path);
            icon.ForegroundKey = FileIcons.TintForFile(document.Path);
            ToolTip.SetTip(border, document.Path);
            border.Classes.Set(StyleClasses.Modified, document.IsModified);
            border.Classes.Set(StyleClasses.Active, tab.IsActive);
            pin.IsVisible = tab.IsPinned;
        }
        void PaintModified(object? sender, EventArgs e)
        {
            if (border.DataContext is EditorTab tab)
            {
                border.Classes.Set(StyleClasses.Modified, tab.Document.IsModified);
            }
        }
        void PaintPath(object? sender, EventArgs e)
        {
            if (border.DataContext is EditorTab tab)
            {
                Paint(tab);
            }
        }
        bool onStrip = false;
        OpenDocument? followed = null;
        void Follow()
        {
            OpenDocument? following = onStrip ? Shown() : null;
            if (following == followed)
            {
                return;
            }
            if (followed is not null)
            {
                followed.Text.Changed -= PaintModified;
                followed.Saved -= PaintModified;
                followed.PathChanged -= PaintPath;
            }
            followed = following;
            if (following is not null)
            {
                following.Text.Changed += PaintModified;
                following.Saved += PaintModified;
                following.PathChanged += PaintPath;
            }
        }
        border.DataContextChanged += (_, _) => Follow();
        border.AttachedToVisualTree += (_, _) =>
        {
            onStrip = true;
            Follow();
        };
        border.DetachedFromVisualTree += (_, _) =>
        {
            onStrip = false;
            Follow();
        };
        ContextMenu menu = new();
        menu.Opening += (_, _) =>
        {
            if (Shown() is { } document)
            {
                MenuOpening?.Invoke(this, new TabMenu(document, menu));
            }
        };
        border.ContextMenu = menu;
        border.PointerPressed += (_, e) =>
        {
            if (Shown() is not { } document)
            {
                return;
            }
            PointerPointProperties pressed = e.GetCurrentPoint(border).Properties;
            if (pressed.IsMiddleButtonPressed)
            {
                CloseRequested?.Invoke(this, document);
                e.Handled = true;
            }
            else if (pressed.IsLeftButtonPressed && e.ClickCount % 2 == 0)
            {
                MaximizeRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            else if (pressed.IsLeftButtonPressed)
            {
                TabSelected?.Invoke(this, document);
                e.Handled = true;
            }
        };
        if (!down)
        {
            return border.Showing<EditorTab>(Paint);
        }
        Panel slot = new() { Children = { border } };
        slot.Bind(Layoutable.HeightProperty, slot.GetResourceObservable(TokenKeys.TabStripHeight));
        return slot.Showing<EditorTab>(Paint);

        static Control Docked(Control part, Dock side, double apart)
        {
            part[DockPanel.DockProperty] = side;
            part.Margin = new Thickness(apart, 0, 0, 0);
            return part;
        }
    }
}

/// <summary>The editor's container holding its tab strip, navigation bar and editor, capping side tabs to a third of its
/// width as Passenger's EditorGroup does.</summary>
public sealed class EditorDock(EditorTabs tabs) : DockPanel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        tabs.UpdateSideWidth(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }
}
