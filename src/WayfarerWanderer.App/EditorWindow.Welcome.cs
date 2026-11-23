using Avalonia.Controls;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Editor;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Settings;
using WayfarerWanderer.App.Shell.State;
using WayfarerWanderer.App.Shell.Vcs;
using WayfarerWanderer.App.Shell.Welcome;

namespace WayfarerWanderer.App;

/// <summary>The window's start: the Welcome screen in the editor's place while no folder and no file is open, with New
/// Project, Open Folder and the folders opened lately; the first start's question of the theme and of dark or light over
/// the person's window the first time its state folder is used; the folder open as Wayfarer last quit opened again; and
/// Settings, whose changes the window's keys and trust follow.</summary>
public sealed partial class EditorWindow
{
    /// <summary>The settings a window keeps nowhere (a headless window's): Settings changes them for that window alone.</summary>
    private SettingsStore? ownSettings;

    /// <summary>The window's content under the first start's question.</summary>
    private Control frame = null!;

    /// <summary>The root the window last remembered as open among the recent folders.</summary>
    private string? rememberedRoot;

    /// <summary>The Welcome screen, in the editor's place while no folder and no file is open.</summary>
    public WelcomeView Welcome { get; } = new();

    /// <summary>The first start's question while it shows over the window, else null.</summary>
    public FirstStartView? FirstStart { get; private set; }

    /// <summary>The settings the window follows: the person's state folder's where the desktop app keeps them, else its own.</summary>
    public SettingsStore SettingsKept => EditorApplication.State?.Settings ?? (ownSettings ??= SettingsStore.Unkept());

    /// <summary>The recent folders, newest first, each marked when it is gone from disk; none where no state is kept.</summary>
    public IReadOnlyList<RecentRow> RecentFolders => EditorApplication.State is { } state
        ? [.. state.Recent.Value.Newest().Select(recent => RecentRow.Of(recent.Path, recent.Entry))]
        : [];

    private void AddWelcome()
    {
        Welcome.NewProjectRequested += (_, _) => Actions.TryExecute(EditorActions.NewProject);
        Welcome.OpenFolderRequested += (_, _) => Actions.TryExecute(EditorActions.OpenWorkspace);
        Welcome.OpenFileRequested += (_, _) => Actions.TryExecute(EditorActions.OpenSource);
        Welcome.CloneRequested += (_, _) => Actions.TryExecute(AppActions.CloneRepository);
        Actions.Add(new EditorAction(AppActions.CloneRepository, "Clone Repository…", () => true, () => ShowCloneRepository()));
        Welcome.Chosen += (_, row) => OpenRecent(row.Path);
        Welcome.RemoveRequested += (_, row) => ForgetRecent(row.Path);
        Welcome.RevealRequested += (_, row) => FileManager.Show(this, row.Path);
        Welcome.Show(RecentFolders);
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                ShowWelcomeWhenEmpty();
            }
        };
        ShowWelcomeWhenEmpty();
        Actions.Add(new EditorAction(AppActions.ShowSettings, "Settings…", () => true, () => ShowSettings()));
        SettingsKept.Changed += SettingsChanged;
        Closed += (_, _) => SettingsKept.Changed -= SettingsChanged;
        Opened += (_, _) =>
        {
            if (Welcome.IsVisible && FirstStart is null)
            {
                Welcome.FocusSearch();
            }
        };
    }

    /// <summary>The Welcome screen shows while no folder and no file is open.</summary>
    private void ShowWelcomeWhenEmpty() => Welcome.IsVisible = Session is null && !Editor.IsVisible;

    /// <summary>Opens a recent folder, as the Welcome screen and File, Recent Folders do: null once it is opening, else why
    /// not (it is gone), which the Welcome screen says.</summary>
    public string? OpenRecent(string path)
    {
        if (!Directory.Exists(path))
        {
            string gone = $"{RecentRow.Home(path)} no longer exists: it was moved or deleted, or its drive is not connected";
            Welcome.Say(gone);
            Welcome.Show(RecentFolders);
            return gone;
        }
        Welcome.Say(null);
        UnawaitedWork.Observe(OpenWorkspaceAsync(path));
        return null;
    }

    /// <summary>Takes a folder off the recent ones for good.</summary>
    public void ForgetRecent(string path) => ForgetRecent([path]);

    /// <summary>Takes folders off the recent ones for good, as the person took them off the taskbar's jump list.</summary>
    public void ForgetRecent(IReadOnlyList<string> paths)
    {
        if (EditorApplication.State is { } state)
        {
            state.Recent.Set(paths.Aggregate(state.Recent.Value, (recent, path) => recent.Without(path)));
            Welcome.Say(null);
            Welcome.Show(RecentFolders);
            FollowRecentShortcuts();
        }
    }

    /// <summary>The recent folders as the person's state folder keeps them; none for a window that keeps no state.</summary>
    public StateDocument<RecentFoldersState>? RecentKept => EditorApplication.State?.Recent;

    /// <summary>The system's lists of recent folders outside the window (the taskbar's jump list, the Dock's menu, the
    /// desktop's recently used list) follow this window's recent folders.</summary>
    public void FollowRecentShortcuts()
    {
        if (RecentKept is { } recent)
        {
            RecentShortcuts.Follow(this, recent);
        }
    }

    /// <summary>Puts the folder just opened at the top of the recent ones, open, the one open before it no longer open.</summary>
    private void Remember(string root)
    {
        if (EditorApplication.State is not { } state)
        {
            return;
        }
        string canonical = PathNames.Canonical(root);
        RecentFoldersState recent = state.Recent.Value;
        if (rememberedRoot is { } before && !PathNames.Same(before, canonical))
        {
            recent = recent.Closed(before);
        }
        rememberedRoot = canonical;
        state.Recent.Set(recent.Opened(canonical, Path.GetFileName(Path.TrimEndingDirectorySeparator(canonical)), recent.Next(DateTimeOffset.UtcNow, DateTimeOffset.MinValue)));
        Welcome.Show(RecentFolders);
        FollowRecentShortcuts();
    }

    /// <summary>The folder open is open no longer among the recent ones (Close Project), so a start does not open it again.</summary>
    private void ForgetOpen()
    {
        if (EditorApplication.State is { } state && rememberedRoot is { } root)
        {
            state.Recent.Set(state.Recent.Value.Closed(root));
        }
        rememberedRoot = null;
        Welcome.Show(RecentFolders);
        FollowRecentShortcuts();
    }

    /// <summary>Started with nothing named, opens the folder open as Wayfarer last quit, unless Reopen projects on startup is
    /// off; one gone from disk is left on the Welcome screen, marked, with why. Whether a folder is opening.</summary>
    public bool ReopenLast()
    {
        if (EditorApplication.State is not { } state || !SettingsKept.Value.ReopenProjectsOnStartup)
        {
            return false;
        }
        foreach ((string path, RecentFolder entry) in state.Recent.Value.StillOpen().Reverse())
        {
            RecentRow row = RecentRow.Of(path, entry);
            if (row.Missing)
            {
                Welcome.Say($"{row.Name} was not reopened: {row.Shown} no longer exists");
                continue;
            }
            UnawaitedWork.Observe(OpenWorkspaceAsync(path));
            return true;
        }
        return false;
    }

    /// <summary>The first start's question over the window when the state folder is new: shown once, its answers kept as
    /// Settings keeps them. Whether it shows.</summary>
    public bool ShowFirstStartOnce()
    {
        if (EditorApplication.State is not { UsedBefore: false })
        {
            return false;
        }
        ShowFirstStart();
        return true;
    }

    /// <summary>The first start's question over the window, shown now if it is not already: the window under it takes no
    /// keys or clicks until it ends.</summary>
    public FirstStartView ShowFirstStart()
    {
        if (FirstStart is { } shown)
        {
            return shown;
        }
        SettingsStore store = SettingsKept;
        FirstStartView page = new(() => store.Effective, change => store.Update(change));
        page.Ended += (_, _) => EndFirstStart();
        frame.IsEnabled = false;
        ((Panel)Content!).Children.Add(page);
        FirstStart = page;
        IdeErrors.Write("First start: the theme and dark or light asked");
        return page;
    }

    private void EndFirstStart()
    {
        if (FirstStart is not { } page)
        {
            return;
        }
        FirstStart = null;
        ((Panel)Content!).Children.Remove(page);
        frame.IsEnabled = true;
        SettingsStore store = SettingsKept;
        // Kept as answered, even when nothing changed, so the next start does not ask again.
        store.Update(settings => settings);
        IdeErrors.Write($"First start: {store.Value.Look?.Shown ?? Themes.OwnName}, {store.Value.Theme}");
        if (Editor.IsVisible)
        {
            Editor.Focus();
        }
        else if (Welcome.IsVisible)
        {
            Welcome.FocusSearch();
        }
    }

    /// <summary>Settings over the window: what it applies put in force for every window that keeps the same settings, its
    /// look previewed while it is open, its Trusted Folders page where the window keeps the person's answers.</summary>
    public SettingsDialog ShowSettings()
    {
        SettingsStore store = SettingsKept;
        TrustAnswers? trust = EditorApplication.State is { } state ? new TrustAnswers(state.Trust.Value, ApplyTrust) : null;
        SettingsDialog dialog = new(store.Value, Actions.Registry, store.Apply, store.BeginPreview(), trust);
        if (IsVisible)
        {
            UnawaitedWork.Observe(dialog.ShowDialog(this));
        }
        else
        {
            dialog.Show();
        }
        return dialog;
    }

    /// <summary>A change of the settings: the window's keys follow the keymap in force.</summary>
    private void SettingsChanged(object? sender, SettingsChange change) =>
        Dispatcher.UIThread.Post(() =>
        {
            keys = AppActions.Keymap();
            Editor.Keymap = keys;
            TerminalSettingsChanged(change);
            FollowMainMenu(SettingsKept.Value);
            FollowTitleBar(SettingsKept.Value);
        });

    /// <summary>The Trusted Folders page's change put in force and kept: a folder trusted anew is trusted, one whose trust
    /// was taken back is trusted no longer (the open folder's language servers ending, its trust asked again). Only what
    /// the page changed since <paramref name="from"/> is put over the answers as kept now, so another Wayfarer's answer
    /// meanwhile stays.</summary>
    private TrustState ApplyTrust(TrustState from, TrustState now)
    {
        StateStore state = EditorApplication.State!;
        state.Trust.Set(PutInForce(state.Trust.Value, from, now));
        if (Session is { IsTrusted: false })
        {
            ResetSemanticSession();
        }
        TrustBanner.IsVisible = Session is { IsTrusted: false };
        return state.Trust.Value;
    }

    /// <summary>What the Trusted Folders page changed from <paramref name="from"/> to <paramref name="now"/>, put in force
    /// (<see cref="WorkspaceTrust"/>) and over the answers as <paramref name="kept"/> has them now: the answers to keep.</summary>
    public static TrustState PutInForce(TrustState kept, TrustState from, TrustState now)
    {
        foreach (string taken in from.Trusted.Where(f => !now.Trusted.Contains(f, PathNames.Comparer)))
        {
            // Trust is held by the folder trusting it trusts (its Git repository's top), so that is what is taken back.
            WorkspaceTrust.TakeBack(taken);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(taken));
            // A JVM project host serving the folder runs its build code: it ends with the trust.
            IdeErrors.Observe(WayfarerWanderer.Jvm.ProjectHosts.EndServingAsync(taken, "trust in the folder was taken back"));
            kept = now.Untrusted.Contains(taken, PathNames.Comparer) ? kept.StoppedTrusting(taken) : kept.Forgotten(taken);
        }
        foreach (string forgotten in from.Untrusted.Where(f => !now.Untrusted.Contains(f, PathNames.Comparer) && !now.Trusted.Contains(f, PathNames.Comparer)))
        {
            kept = kept.Forgotten(forgotten);
        }
        foreach (string declined in now.Untrusted.Where(f => !from.Untrusted.Contains(f, PathNames.Comparer) && !from.Trusted.Contains(f, PathNames.Comparer)))
        {
            kept = kept.Declined(declined);
        }
        string[] added = [.. now.Trusted.Where(f => !from.Trusted.Contains(f, PathNames.Comparer)).Select(WorkspaceTrust.FolderToTrust)];
        if (added.Length > 0)
        {
            WorkspaceTrust.Trust(added);
            kept = added.Aggregate(kept, (answers, folder) => answers.With(folder));
        }
        return kept;
    }

    /// <summary>Clone Repository: the dialog over this window, and the clone opened once it is made. A clone made outside any
    /// Git repository is trusted as it opens, never asked of, as Passenger trusts one; inside another, only the person
    /// trusts it.</summary>
    public CloneDialog ShowCloneRepository()
    {
        CloneDialog dialog = new(NewProjectAction.DefaultFolder(Session?.Root));
        dialog.Closed += (_, _) =>
        {
            if (dialog.Cloned is { } cloned)
            {
                string canonical = PathNames.Canonical(cloned);
                if (WorkspaceTrust.TrustsAlone(canonical))
                {
                    WorkspaceTrust.Trust(canonical);
                    KeepTrust(canonical);
                }
                UnawaitedWork.Observe(OpenWorkspaceAsync(cloned));
            }
        };
        UnawaitedWork.Observe(dialog.ShowDialog(this));
        return dialog;
    }

    /// <summary>The folder a root's trust is held by and kept as: the top of its Git repository, else the root itself.</summary>
    public static string TrustKeptFor(string root) => WorkspaceTrust.FolderToTrust(PathNames.Canonical(root));

    /// <summary>The open folder trusted by the person, kept among their answers as the folder its trust is held by (the top
    /// of its Git repository, as <see cref="WorkspaceTrust.Trust"/> trusts), so taking it back in Settings takes it back.</summary>
    private void KeepTrust(string root)
    {
        if (EditorApplication.State is { } state)
        {
            state.Trust.Set(state.Trust.Value.With(TrustKeptFor(root)));
        }
    }

    /// <summary>The folders the person trusted, as kept, trusted as the desktop app starts.</summary>
    public static void TrustKept(StateStore state)
    {
        if (state.Trust.Value.Trusted is { Count: > 0 } trusted)
        {
            WorkspaceTrust.Trust(trusted);
        }
    }
}
