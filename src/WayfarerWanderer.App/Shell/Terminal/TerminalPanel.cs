// From Passenger 494009d7 src/Passenger.App/ToolWindows/TerminalPanel.cs, MIT (notices/Passenger/LICENSE): its shells
// alone, without run configurations' programs, the agents' shell or a .NET installation's PATH.
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Passenger.Core;
using Passenger.Terminal;
using WayfarerWanderer.App.Shell.ToolWindows;

namespace WayfarerWanderer.App.Shell.Terminal;

/// <summary>A tab of the Terminal window: a shell and its name (Local).</summary>
public sealed class TerminalTab(TerminalSession session, string name) : Observable
{
    private bool isActive;

    public TerminalSession Session { get; } = session;

    public string Name { get; } = name;

    public bool IsActive
    {
        get => isActive;
        set => Set(ref isActive, value);
    }
}

/// <summary>The Terminal window: a shell per tab in the open folder, the first started when the window is first shown, +
/// for another; a tab whose shell ends closes, and the window hides with its last. Shown, it takes the keys only as asked
/// (<see cref="FocusView"/>, a tab started or chosen, stepped to by Next Tab or Previous Tab), never because a layout shows
/// it; Close Tool Window Tab closes the shell shown as its × does.</summary>
public sealed class TerminalPanel : DockPanel, ToolTabs.ITabbed
{
    private readonly ObservableCollection<TerminalTab> tabs = [];
    // Refusals.NothingSelected's words, until Refusals is caught up with v0.4.2.
    private readonly TerminalView view = new() { NothingSelected = "No text is selected" };
    private readonly Func<string> folder;
    private int opened;
    private bool firstComing;

    /// <param name="folder">Where a new shell starts: the folder the window holds, else the home folder.</param>
    public TerminalPanel(Func<string> folder)
    {
        this.folder = folder;
        // The shells' tabs are in the window's header, after its title, then New Session.
        ItemsControl strip = new()
        {
            ItemsSource = tabs,
            VerticalAlignment = VerticalAlignment.Center,
            // In a narrow window the last tabs give way first, each name ending in an ellipsis, as the Debug window's do.
            ItemsPanel = new FuncTemplate<Panel?>(() => new ShrinkingRow { Spacing = Gaps.Hair }),
        };
        ReusedRows.Use(strip, Tab);
        Children.Add(view);
        Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Terminal_Background));
        AttachedToVisualTree += (_, _) =>
        {
            if (tabs.Count == 0 && !firstComing)
            {
                // After what the window does as it shows, so a layout's Terminal starts in the folder it opens then.
                firstComing = true;
                UiThread.Post(() =>
                {
                    firstComing = false;
                    if (tabs.Count == 0)
                    {
                        IdeErrors.Observe(NewSessionAsync(null, focus: false));
                    }
                });
            }
        };
        Button add = ToolButtons.Of(AppActions.NewTerminalSession, IconKeys.Add, "New Session", action => ActionRequested?.Invoke(this, action));
        HeaderTools = new ShrinkingRow { Spacing = Gaps.Tight, Children = { strip, add } };
    }

    /// <summary>Raised when the last shell has ended, so the window can hide.</summary>
    public event EventHandler? Emptied;

    /// <summary>One of the window's actions asked for by its button.</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>A command the shell of a person's tab ran failed, with its exit status (<see cref="TerminalSession.CommandEnded"/>).</summary>
    public event EventHandler<int>? CommandFailed;

    /// <summary>The window header's own tools: the tabs, then New Session.</summary>
    public Control HeaderTools { get; }

    /// <summary>The shell to start in place of the user's, and its arguments (Settings' Shell path).</summary>
    public (string App, string[] Arguments)? Shell { get; set; }

    public TerminalView View => view;

    public IReadOnlyList<TerminalTab> Tabs => tabs;

    public TerminalSession? Active => ActiveTab?.Session;

    /// <summary>The tab shown.</summary>
    public TerminalTab? ActiveTab => tabs.FirstOrDefault(t => t.IsActive);

    /// <summary>Starts a shell in a tab of its own and shows it with the keys: in the open folder, or in another (Open In,
    /// Terminal).</summary>
    public Task<TerminalSession> NewSessionAsync(string? at = null) => NewSessionAsync(at, focus: true);

    /// <summary>Starts a shell in a tab of its own and shows it, with the keys only where <paramref name="focus"/> says; the
    /// shell's marks (zsh's through a .zshenv of the IDE's) tell it each command's end.</summary>
    private async Task<TerminalSession> NewSessionAsync(string? at, bool focus)
    {
        TerminalSession session = new(view.Session?.Screen.Columns ?? 120, view.Session?.Screen.Rows ?? 30);
        string name = ++opened == 1 ? "Local" : $"Local ({opened})";
        TerminalTab tab = new(session, name);
        tabs.Add(tab);
        Show(tab, focus);
        session.Exited += (_, _) => Close(tab);
        session.CommandEnded += (_, status) =>
        {
            if (status != 0)
            {
                CommandFailed?.Invoke(this, status);
            }
        };
        try
        {
            if (Shell is { } given && string.IsNullOrWhiteSpace(given.App))
            {
                throw new InvalidOperationException("the Shell path in Settings names no program to start");
            }
            string zsh = await BackgroundWork.Run(() => FirstOnPath.ZshFolder(Path.Combine(State.StateStore.DefaultRoot, "cache", "terminal")));
            await session.StartAsync(at ?? folder(), Shell?.App, Shell?.Arguments, marks: zsh);
        }
        catch (Exception failed) when (failed is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            session.Screen.Feed($"{Product.Name} could not start a shell: {failed.Message}\r\n");
        }
        return session;
    }

    /// <summary>The text before the cursor on its line, which at a shell waiting for a command is its prompt.</summary>
    public static string Prompt(TerminalScreen screen) =>
        screen.IsAlternate ? string.Empty : screen.Lines[screen.CursorRow].Text(0, screen.CursorColumn);

    /// <summary>Ends every shell, with all it started, as the window closes.</summary>
    public Task CloseAllAsync()
    {
        TerminalTab[] closing = [.. tabs];
        tabs.Clear();
        view.Session = null;
        return Task.WhenAll(closing.Select(tab => tab.Session.DisposeAsync().AsTask()));
    }

    public bool StepTab(int step)
    {
        if (tabs.Count < 2 || ActiveTab is not { } shown)
        {
            return false;
        }
        Show(tabs[(((tabs.IndexOf(shown) + step) % tabs.Count) + tabs.Count) % tabs.Count]);
        return true;
    }

    private void Show(TerminalTab tab, bool focus = true)
    {
        foreach (TerminalTab each in tabs)
        {
            each.IsActive = each == tab;
        }
        view.Session = tab.Session;
        Avalonia.Automation.AutomationProperties.SetName(view, tab.Name);
        if (focus)
        {
            FocusView();
        }
    }

    /// <summary>The keys to the shell, once the view is laid out (a control not yet arranged cannot take the focus).</summary>
    public void FocusView() => Avalonia.Threading.Dispatcher.UIThread.Post(() => view.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);

    public bool CloseTab()
    {
        if (ActiveTab is not { } shown)
        {
            return false;
        }
        Close(shown);
        return true;
    }

    private void Close(TerminalTab tab)
    {
        int index = tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }
        tabs.RemoveAt(index);
        IdeErrors.Observe(tab.Session.DisposeAsync().AsTask());
        if (tabs.Count == 0)
        {
            view.Session = null;
            Emptied?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (tab.IsActive)
        {
            Show(tabs[Math.Min(index, tabs.Count - 1)]);
        }
    }

    /// <summary>A shell's tab for whichever shell the strip hands it: its name and its ×, which ends the shell and closes
    /// the tab.</summary>
    private Control Tab()
    {
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Controls.Primitives.ToggleButton button = null!;
        button = ToolTabs.Tab(name, () =>
        {
            if (button.DataContext is TerminalTab tab)
            {
                Close(tab);
            }
        }, "Close Session");
        button.Click += (_, _) =>
        {
            if (button.DataContext is TerminalTab tab)
            {
                Show(tab);
            }
        };
        return button.Showing<TerminalTab>(tab =>
        {
            name.Text = tab.Name;
            button.IsChecked = tab.IsActive;
        });
    }
}
