using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Passenger.Core;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Settings;
using EditorKeys = Passenger.Editor.EditorActions;

namespace WayfarerWanderer.App;

/// <summary>The main menu on the toolbar, as Passenger and IntelliJ IDEA put it there: built from the actions' places
/// (<see cref="MainMenuPlaces"/>, <see cref="MainMenu"/>) each time a menu opens, so each item shows the keys the keymap
/// gives its action now and is greyed, saying why, where it cannot run. Its titles stand side by side on the toolbar
/// (Settings' Show main menu as a menu bar), those it has no room for behind the button after them, or all of them behind
/// that button. A menu runs its items where the keys were as it opened: Edit's Copy copies the text box's text or the
/// editor's, or the Explorer's rows.</summary>
public sealed partial class EditorWindow
{
    private const string WayfarerGroup = "Wayfarer";

    /// <summary>The main menu's place on the toolbar: its button, then its titles.</summary>
    private readonly MainMenuPlace menuPlace = new() { Margin = new Thickness(0, 0, 2, 0) };

    /// <summary>The main menu's button, which holds the menus the bar has no room for, or all of them.</summary>
    private readonly Button menuButton = ToolButtons.Of(IconKeys.Menu, "Main menu");

    /// <summary>The menus the actions make, as they stand now.</summary>
    public IReadOnlyList<MenuEntry> MainMenuEntries => MainMenu.Build(Actions.Registry.All.SelectMany(a => a.Menus.Select(place => (a.Id, place))));

    /// <summary>One of the main menu's menus built as it would open now, for a test to read and click.</summary>
    public MenuItem MainMenuNamed(string title) =>
        (MenuItem)FlyoutEntry(MainMenuEntries.Single(m => m.Title == title), Actions.Registry.Context());

    /// <summary>The menu's own actions: Close Project and Exit, Edit's, and Find Action.</summary>
    private void AddMainMenuActions()
    {
        Actions.Registry.Add(new IdeAction(AppActions.CloseSolution, "Close Project", WayfarerGroup, _ => UnawaitedWork.Observe(CloseProjectAsync()))
        {
            WhyNot = _ => Session is null ? Refusals.NoFolder : null,
        });
        Actions.Registry.Add(new IdeAction(AppActions.Exit, OperatingSystem.IsMacOS() ? "Quit Wayfarer Wanderer" : "Exit", WayfarerGroup, _ => Exit()));
        Actions.Registry.Add(new IdeAction(AppActions.FindAction, "Find Action…", WayfarerGroup, _ => OpenFindAction()));
        Actions.Registry.Add(new IdeAction(AppActions.CheckForUpdates, "Check for Updates…", "Help", _ => IdeErrors.Observe(CheckUpdatesAsync(automatic: false))));
        Actions.Registry.Add(new IdeAction(AppActions.RestartToUpdate, "Restart to Update", "Help", _ => IdeErrors.Observe(RestartToUpdateAsync()))
        {
            WhyNot = _ => installer?.Ready is null || !SettingsKept.Value.CheckForUpdates ? "No update is ready to install" : null,
        });
        AddEdit(EditorKeys.Undo, "Undo", box => box.Undo(), redo: false);
        AddEdit(EditorKeys.Redo, "Redo", box => box.Redo(), redo: true);
        AddEdit(EditorKeys.SelectAll, "Select All", box => box.SelectAll());
        AddFolding();

        // Undo and Redo take the Explorer's last Delete back, or do it again, where its rows have the keys.
        void AddEdit(string id, string title, Action<TextBox> inBox, bool? redo = null) =>
            Actions.Registry.Add(new IdeAction(id, title, "Edit", context =>
            {
                if (redo is not { } again || !UndoInExplorer(context, again))
                {
                    EditText(context, id, inBox);
                }
            })
            {
                WhyNot = context => (redo is { } again ? WhyNotUndoInExplorer(context, again) : null) ?? WhyNotEditText(context),
                InEditor = true,
            });
    }

    /// <summary>Code › Folding: the editor's folding actions, by level and recursively too, run on the editor shown, which
    /// takes the keys back (<see cref="MainMenuPlaces"/> places them).</summary>
    private void AddFolding()
    {
        HashSet<string> folding =
        [
            EditorKeys.Unfold, EditorKeys.UnfoldAll, EditorKeys.UnfoldRecursively, EditorKeys.Fold, EditorKeys.FoldToDefinitions,
            EditorKeys.FoldAll, EditorKeys.FoldRecursively, EditorKeys.UnfoldDocComments, EditorKeys.FoldDocComments, EditorKeys.ToggleFold,
            .. EditorKeys.UnfoldToLevels, .. EditorKeys.UnfoldAllToLevels,
        ];
        foreach ((string id, string title) in EditorKeys.All.Where(a => folding.Contains(a.Id)))
        {
            Actions.Registry.Add(new IdeAction(id, title, "Editor", _ =>
            {
                Editor.Focus();
                Editor.TryExecute(id);
            })
            {
                WhyNot = _ => Editor.IsVisible ? null : Refusals.NoFile,
                InEditor = true,
            });
        }
    }

    /// <summary>An editing action run where the keys were: on the text box that had them (a filter, a dialog's field), else
    /// on the editor shown, which takes the keys back.</summary>
    private void EditText(ActionContext context, string id, Action<TextBox> inBox)
    {
        if (context.Focused is TextBox box)
        {
            inBox(box);
            return;
        }
        Editor.Focus();
        Editor.TryExecute(id);
    }

    private string? WhyNotEditText(ActionContext context) =>
        context.Focused is TextBox ? null
        : context.Focused is Passenger.Terminal.TerminalView ? "The terminal has the keys"
        : Editor.IsVisible ? null
        : "No file is open";

    /// <summary>File, Exit: every Wayfarer window closes, each ending its folder's session as its own close does, asking
    /// first where Confirm before closing says (<see cref="QuitAskedAsync"/>).</summary>
    private void Exit()
    {
        if (ConfirmsQuit)
        {
            IdeErrors.Observe(QuitAskedAsync(this));
            return;
        }
        Quit();
    }

    /// <summary>File, Close Project: the folder's session ended, its files, tree, Git and banners let go of, and the Welcome
    /// screen in the editor's place; the folder stays among the recent ones, no longer open, so a start does not open it
    /// again.</summary>
    public async Task CloseProjectAsync()
    {
        if (Session is not { } session)
        {
            return;
        }
        ++workspaceRequest;
        ResetSemanticSession();
        ForgetPlaces();
        ResetOpenDocuments();
        Session = null;
        await session.DisposeAsync();
        Unwatch();
        IdeErrors.Observe(Vcs.CloseAsync());
        Explorer.Unload();
        Breakpoints.Reset();
        BuildTool.Reset();
        TestsTool.Reset();
        DebugTool.Reset();
        TrustBanner.IsVisible = false;
        ShowMissingJdks();
        ShowToolingJvmMissing(false);
        ForgetOpen();
        Title = "Wayfarer Wanderer";
        ShowWelcomeWhenEmpty();
        UpdateToolbar();
        Welcome.FocusSearch();
    }

    // The bar: its titles, how a press or a key opens their menus, and the button that holds the rest.

    /// <summary>The main menu's place made: its button, and its titles as Settings asks for them.</summary>
    private void AddMainMenu()
    {
        menuButton.Click += OnMenuClicked;
        Avalonia.Automation.AutomationProperties.SetName(menuButton, "Main menu");
        menuPlace.Children.Add(menuButton);
        foreach (MenuEntry menu in MainMenuEntries)
        {
            Button title = new() { Content = menu.Title, Tag = menu.Title, Classes = { StyleClasses.Widget, StyleClasses.MenuBarItem } };
            title.Click += (_, _) => OnBarTitleClicked(title);
            menuPlace.Children.Add(title);
        }
        AddHandler(PointerPressedEvent, NoteBarPress, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnBarKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnBarKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        FollowMainMenu(SettingsKept.Value);
    }

    /// <summary>The titles on the bar, or the button alone, as Settings says.</summary>
    private void FollowMainMenu(AppSettings settings) => menuPlace.ShowsBar = settings.MainMenuBar;

    /// <summary>What had the keys before the bar took them (Alt, or a press on a title), given back as the bar lets go.</summary>
    private IInputElement? barReturn;

    /// <summary>The context the bar's keys or press were taken in, which a menu they open runs its items in.</summary>
    private ActionContext? barKeysContext;

    /// <summary>Whether Alt went down alone and nothing else has come since: its release takes the bar.</summary>
    private bool altAlone;

    /// <summary>Whether Alt is held: Windows repeats a held Alt's press, which keeps what a press or another key since its
    /// first made of it, so Alt released after an Alt-drag never takes the bar.</summary>
    private bool altHeld;

    private MenuFlyout? barMenu;
    private Button? barMenuFrom;
    private ActionContext? barMenuContext;
    private Button? barPressedOpen;

    /// <summary>The bar's titles and button a key moves among, as shown, in order.</summary>
    private List<Button> BarStops() =>
        [.. menuPlace.Children.OfType<Button>().Where(b => b.Tag is string && b.IsHitTestVisible), .. menuButton.IsHitTestVisible ? [menuButton] : Array.Empty<Button>()];

    private bool IsBarStop(object? focused) => focused is Button button && BarStops().Contains(button);

    /// <summary>Alt tapped alone takes the bar's first title (its menus then opened by the keys), or with the menu behind
    /// its button alone opens that; tapped again it gives the keys back.</summary>
    private void OnBarKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftAlt or Key.RightAlt)
        {
            altHeld = false;
        }
        if (e.Key is not (Key.LeftAlt or Key.RightAlt) || !altAlone || FirstStart is not null)
        {
            return;
        }
        altAlone = false;
        e.Handled = true;
        if (barMenu is { IsOpen: true } || IsBarStop(FocusManager?.GetFocusedElement()))
        {
            LeaveBar();
            return;
        }
        barReturn = FocusManager?.GetFocusedElement();
        barKeysContext = Actions.Registry.Context();
        if (menuPlace.MenusShown == 0)
        {
            OpenByKeys(menuButton);
            return;
        }
        BarStops().FirstOrDefault()?.Focus(NavigationMethod.Tab);
    }

    /// <summary>The bar's keys: Left and Right move among its titles, or with a menu open open the next one's; Down, Enter
    /// or Space open the title's menu; Escape gives the keys back to what had them.</summary>
    private void OnBarKeyDown(object? sender, KeyEventArgs e)
    {
        bool alt = e.Key is Key.LeftAlt or Key.RightAlt;
        altAlone = alt && (e.KeyModifiers & ~KeyModifiers.Alt) == KeyModifiers.None && (!altHeld || altAlone);
        altHeld = alt || altHeld && (e.KeyModifiers & KeyModifiers.Alt) != 0;
        if (e.Handled && barMenu is not { IsOpen: true })
        {
            return;
        }
        object? focused = FocusManager?.GetFocusedElement();
        if (barMenu is { IsOpen: true } && barMenuFrom is { } from)
        {
            // Heard by the window and the item both where the menu stands in the window's own tree: once.
            if (e.Handled || focused is not MenuItem item)
            {
                return;
            }
            bool atTop = item.Parent is not MenuItem;
            if (e.Key == Key.Left && atTop || e.Key == Key.Right && item.ItemCount == 0)
            {
                e.Handled = true;
                Button next = Beside(from, e.Key == Key.Left ? -1 : 1);
                Dispatcher.UIThread.Post(() => OpenByKeys(next));
            }
            else if (e.Key == Key.Escape && atTop)
            {
                e.Handled = true;
                LeaveBar();
            }
            return;
        }
        if (!IsBarStop(focused))
        {
            return;
        }
        Button title = (Button)focused!;
        switch (e.Key)
        {
            case Key.Left or Key.Right:
                Beside(title, e.Key == Key.Left ? -1 : 1).Focus(NavigationMethod.Directional);
                e.Handled = true;
                break;
            case Key.Down or Key.Enter or Key.Space:
                OpenByKeys(title);
                e.Handled = true;
                break;
            case Key.Escape:
                LeaveBar();
                e.Handled = true;
                break;
        }
    }

    /// <summary>The title (or button) a step to one side of another, round from one end to the other.</summary>
    private Button Beside(Button title, int step)
    {
        List<Button> stops = BarStops();
        int at = stops.IndexOf(title);
        return at < 0 ? title : stops[(at + step + stops.Count) % stops.Count];
    }

    /// <summary>A title's menu (the button's, those it holds) opened from the keys, its first item taking them.</summary>
    private void OpenByKeys(Button title)
    {
        if (title == menuButton)
        {
            barMenu?.Hide();
            OpenMenuButton();
        }
        else
        {
            OpenBarMenu(title);
        }
        if (barMenu is { } flyout)
        {
            Dispatcher.UIThread.Post(() =>
                flyout.Items.OfType<MenuItem>().FirstOrDefault(i => i.IsEffectivelyEnabled)?.Focus(NavigationMethod.Directional));
        }
    }

    /// <summary>The bar lets go: its menu closed and the keys back with what had them before (the editor when nothing did).</summary>
    private void LeaveBar()
    {
        IInputElement? back = barReturn;
        (barReturn, barKeysContext) = (null, null);
        barMenu?.Hide();
        if (back is Visual visual && TopLevel.GetTopLevel(visual) is not null && back.Focus())
        {
            return;
        }
        if (Editor.IsVisible)
        {
            Editor.Focus();
        }
    }

    /// <summary>A title clicked opens its menu, or closes it when that menu was open as the press came: the press closed
    /// it, passing through to the title, which would open it again.</summary>
    private void OnBarTitleClicked(Button title)
    {
        if (barPressedOpen == title)
        {
            barPressedOpen = null;
            return;
        }
        OpenBarMenu(title);
    }

    /// <summary>Which open menu's title a press came on, before the menu's light dismiss closes it, and what had the keys
    /// (and where its menu's items would run) before a press on a title or the button took them.</summary>
    private void NoteBarPress(object? sender, PointerPressedEventArgs e)
    {
        altAlone = false;
        Button? title = BarTitleAt(this, e);
        barPressedOpen = barMenu is { IsOpen: true } && title is not null && title == barMenuFrom ? title : null;
        bool onBar = title is not null || (e.Source as Visual)?.FindLogicalAncestorOfType<Button>(includeSelf: true) == menuButton;
        if (onBar && FocusManager?.GetFocusedElement() is var focused && !IsBarStop(focused) && focused is not MenuItem)
        {
            barReturn = focused;
            barKeysContext = Actions.Registry.Context();
        }
    }

    /// <summary>The bar's title shown under the pointer, an event of the window's or of a menu's own popup turned into the
    /// window's place through the screen.</summary>
    private Button? BarTitleAt(Visual root, PointerEventArgs e)
    {
        Point at = this.PointToClient(root.PointToScreen(e.GetPosition(root)));
        return menuPlace.Children.OfType<Button>().FirstOrDefault(t => t.Tag is string && t.IsHitTestVisible
            && t.TranslatePoint(default, this) is { } corner && new Rect(corner, t.Bounds.Size).Contains(at));
    }

    /// <summary>While one of the bar's menus is open, the pointer moving over another title opens that one. The open
    /// menu holds the pointer, so its moves are followed from the menu's own popup as well as the window.</summary>
    private void FollowBarPointer(object? sender, PointerEventArgs e)
    {
        if (barMenu is not { IsOpen: true } || sender is not Visual root)
        {
            return;
        }
        Button? over = BarTitleAt(root, e);
        if (over is not null && over != barMenuFrom)
        {
            // Not inside the event of the popup the switch closes.
            Dispatcher.UIThread.Post(() =>
            {
                if (barMenu is { IsOpen: true } && barMenuFrom != over)
                {
                    OpenBarMenu(over);
                }
            });
        }
    }

    /// <summary>One of the bar's menus opened under its title, built from the actions as it opens, in the context the first
    /// of them opened in (the focus is the menu's once one is open).</summary>
    private void OpenBarMenu(Button title)
    {
        ActionContext context = barMenu is { IsOpen: true } && barMenuContext is { } kept ? kept : barKeysContext ?? Actions.Registry.Context();
        barMenu?.Hide();
        MenuEntry entry = MainMenuEntries.First(m => m.Title == (string)title.Tag!);
        MenuFlyout flyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft, OverlayDismissEventPassThrough = true };
        if (FlyoutEntry(entry, context) is MenuItem built)
        {
            foreach (object? item in built.Items.ToList())
            {
                built.Items.Remove(item);
                flyout.Items.Add(item);
            }
        }
        (barMenu, barMenuFrom, barMenuContext) = (flyout, title, context);
        title.Classes.Add(StyleClasses.Open);
        Action release = KeepKeys(title, () => flyout.IsOpen ? flyout.Items.OfType<Control>().FirstOrDefault()?.FindLogicalAncestorOfType<MenuBase>() : null);
        AddHandler(PointerMovedEvent, FollowBarPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        flyout.ShowAt(title);
        // A popup of its own where the platform gives one; headless, the menu stands in the window's own tree.
        TopLevel? popup = flyout.Items.OfType<Control>().FirstOrDefault() is { } first ? TopLevel.GetTopLevel(first) : null;
        if (popup is not null && popup != this)
        {
            popup.AddHandler(PointerMovedEvent, FollowBarPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        // Its items' keys, which reach them in a popup of its own (the window's tunnel never sees them there).
        foreach (MenuItem item in flyout.Items.OfType<MenuItem>())
        {
            item.AddHandler(KeyDownEvent, OnBarKeyDown, RoutingStrategies.Tunnel);
        }
        flyout.Closed += (_, _) =>
        {
            release();
            title.Classes.Remove(StyleClasses.Open);
            popup?.RemoveHandler(PointerMovedEvent, FollowBarPointer);
            if (barMenu == flyout)
            {
                RemoveHandler(PointerMovedEvent, FollowBarPointer);
                (barMenu, barMenuFrom, barMenuContext) = (null, null, null);
                // Closed with an item chosen or by a click elsewhere: the keys go back, unless what ran took them.
                Dispatcher.UIThread.Post(() =>
                {
                    if (barMenu is null && FocusManager?.GetFocusedElement() is var focused && (focused is null or MenuItem || IsBarStop(focused)))
                    {
                        LeaveBar();
                    }
                });
            }
        };
    }

    private void OnMenuClicked(object? sender, RoutedEventArgs e) => OpenMenuButton();

    /// <summary>The button's menu: the menus the bar has no room for while it shows, else all of them, each a submenu.</summary>
    private void OpenMenuButton()
    {
        ActionContext context = barKeysContext ?? Actions.Registry.Context();
        MenuFlyout flyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (MenuEntry menu in MainMenuEntries.Skip(menuPlace.ShowsBar ? menuPlace.MenusShown : 0))
        {
            flyout.Items.Add(FlyoutEntry(menu, context));
        }
        barMenu = flyout;
        Action release = KeepKeys(menuButton, () => flyout.IsOpen ? flyout.Items.OfType<Control>().FirstOrDefault()?.FindLogicalAncestorOfType<MenuBase>() : null);
        flyout.Closed += (_, _) =>
        {
            release();
            if (barMenu == flyout)
            {
                barMenu = null;
                barKeysContext = null;
            }
        };
        flyout.ShowAt(menuButton);
    }

    /// <summary>Keeps the keys in an open menu when the window takes them back as it is activated again (a click in another
    /// application while the menu was open); undone as the menu closes.</summary>
    private static Action KeepKeys(Control target, Func<MenuBase?> open)
    {
        if (TopLevel.GetTopLevel(target) is not WindowBase window)
        {
            return () => { };
        }
        // Posted: the window puts its own focus back after its Activated.
        void Back(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
        {
            if (open() is not { IsKeyboardFocusWithin: false } menu)
            {
                return;
            }
            Control? chosen = menu.SelectedIndex >= 0 ? menu.ContainerFromIndex(menu.SelectedIndex) : null;
            if (chosen?.Focus() != true && !menu.Focus())
            {
                menu.GetLogicalChildren().OfType<MenuItem>().FirstOrDefault(i => i.IsEffectivelyEnabled)?.Focus();
            }
        });
        window.Activated += Back;
        return () => window.Activated -= Back;
    }

    // The menus' items.

    /// <summary>A menu item a listed submenu gives: its title and what choosing it does.</summary>
    private sealed record ListedItem(string Title, Action Run);

    /// <summary>What a listed submenu holds as it opens: every tool window the dock has, or the recent projects but the one
    /// open.</summary>
    private IReadOnlyList<ListedItem> Listed(string submenu) => submenu switch
    {
        MainMenu.ToolWindows => [.. ToolDock.Windows.OrderBy(w => w.Title, StringComparer.Ordinal)
            .Select(w => new ListedItem(w.Title, () => ShowToolWindow(w)))],
        MainMenu.RecentProjects => [.. RecentProjectRows().Select(row => new ListedItem($"{row.Name} – {row.Shown}", () => OpenRecent(row.Path)))],
        _ => throw new InvalidOperationException($"No window lists a submenu called {submenu}"),
    };

    /// <summary>View, Tool Windows: the window shown with the keys, as its stripe button's second click would not hide it.</summary>
    private void ShowToolWindow(Shell.Docking.ToolWindow window)
    {
        if (window.Activate is { } activate)
        {
            activate();
            return;
        }
        ToolDock.Activate(window.Id);
    }

    /// <summary>The recent projects but the one open, newest first.</summary>
    private IEnumerable<Shell.Welcome.RecentRow> RecentProjectRows() =>
        RecentFolders.Where(r => Session is null || !PathNames.Same(r.Path, Session.Root));

    /// <summary>An entry of a menu in the window: its action's title and one-key shortcut, or its chord where it has none,
    /// greyed with why where it cannot run in the context the menu opened in, and run there when chosen.</summary>
    private Control FlyoutEntry(MenuEntry entry, ActionContext context)
    {
        if (entry.IsSeparator)
        {
            return new Separator();
        }
        if (entry.IsListed)
        {
            MenuItem listed = new() { Header = entry.Title };
            foreach (ListedItem item in Listed(entry.Title!))
            {
                MenuItem each = new() { Header = item.Title };
                each.Click += (_, _) => item.Run();
                listed.Items.Add(each);
            }
            if (listed.Items.Count == 0)
            {
                listed.Items.Add(new MenuItem { Header = "Nothing yet", IsEnabled = false });
            }
            return listed;
        }
        if (entry.Items is { } items)
        {
            MenuItem menu = new() { Header = entry.Title };
            foreach (MenuEntry item in items)
            {
                menu.Items.Add(FlyoutEntry(item, context));
            }
            return menu;
        }
        IdeAction action = Actions.Registry[entry.Action!]!;
        string? why = Actions.Registry.WhyNot(action.Id, context);
        MenuItem actionItem = new() { Header = action.TitleWhere(context), Tag = action.Id, IsEnabled = why is null };
        if (why is not null)
        {
            // Greyed, it says why on hover, as running it would.
            ToolTip.SetTip(actionItem, why);
            ToolTip.SetShowOnDisabled(actionItem, true);
        }
        Actions.Registry.ShowKeys(actionItem, action.Id);
        if (action.IsChecked is { } isChecked)
        {
            actionItem.ToggleType = MenuItemToggleType.CheckBox;
            actionItem.IsChecked = isChecked(context);
        }
        actionItem.Click += (_, _) => RunFromMenu(action.Id, context);
        return actionItem;
    }

    /// <summary>A menu item or toolbar button chosen: its action run in the context its menu opened in, or where the keys
    /// are now. False when it cannot run there, which the status line says.</summary>
    public bool RunFromMenu(string action, ActionContext? context = null)
    {
        if (Actions.Registry.Run(action, context ?? Actions.Registry.Context()) is { } why)
        {
            StatusText = why;
            return false;
        }
        return true;
    }
}
