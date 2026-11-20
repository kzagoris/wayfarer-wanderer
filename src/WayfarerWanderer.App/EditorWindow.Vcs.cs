using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.Core;
using Passenger.Text;
using Passenger.Vcs;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Docking;
using WayfarerWanderer.App.Shell.ToolWindows;
using WayfarerWanderer.App.Shell.Vcs;
using WayfarerWanderer.App.Shell.Workspace;

namespace WayfarerWanderer.App;

/// <summary>The window's Git, as Passenger's MainWindow.Vcs has it: the Commit and Git tool windows over one
/// <see cref="VcsController"/> that follows the open folder, the Git menu's actions, the change bars of every open file,
/// diffs in a window of their own, git's questions put to the person, and what Git says in the status line.</summary>
public sealed partial class EditorWindow
{
    private CommitView? commitView;
    private GitView? gitView;
    private DiffPanes? diffs;
    private GitWatcher? ownGit;
    private FileSystemWatcher? workingTree;
    private Timer? treeQuiet;
    private readonly TextBlock statusText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button branchButton = new() { Classes = { StyleClasses.StatusItem }, IsVisible = false };

    /// <summary>The open folder's Git: in no working tree until a folder in one opens.</summary>
    public VcsController Vcs { get; } = new();

    /// <summary>The Commit tool window, built as it is first shown or asked for.</summary>
    public CommitView CommitView => commitView ?? (CommitView)ToolDock[ToolWindowIds.Commit].Content;

    /// <summary>The Git tool window: its Log and the views opened beside it.</summary>
    public GitView GitWindow => gitView ?? (GitView)ToolDock[ToolWindowIds.Git].Content;

    /// <summary>What the status line says last: Git's outcomes and refusals.</summary>
    public string? StatusText
    {
        get => statusText.Text;
        set => statusText.Text = value;
    }

    /// <summary>The status line along the window's foot: what was said last, and the branch, which opens the branches.</summary>
    private Control StatusLine()
    {
        branchButton.Click += (_, _) => ShowAllBranches();
        DockPanel.SetDock(branchButton, Dock.Right);
        statusText.Classes.Add(StyleClasses.Status);
        Border line = new()
        {
            Classes = { StyleClasses.StatusBar },
            Child = new DockPanel { Children = { branchButton, statusText } },
        };
        DockPanel.SetDock(line, Dock.Bottom);
        return line;
    }

    /// <summary>The Commit window at the left and the Git window at the bottom, each built as it is first shown.</summary>
    private void AddVcsToolWindows()
    {
        ToolDock.Add(new ToolWindow(ToolWindowIds.Commit, "Commit", IconKeys.Commit, MakeCommit, ToolAnchor.LeftTop)
        {
            Action = AppActions.ShowCommit,
            ActionTitle = "Commit Tool Window",
        });
        ToolDock.Add(new ToolWindow(ToolWindowIds.Git, "Git", IconKeys.Branch, MakeGit, ToolAnchor.BottomLeft)
        {
            Action = AppActions.ShowGit,
            ActionTitle = "Git Tool Window",
            Tools = () => gitView?.Tabs,
        });
    }

    /// <summary>Git's actions and what the window does with what Git says, before the menus are made.</summary>
    private void AddVcs()
    {
        treeQuiet = new Timer(_ => UiThread.Post(() => IdeErrors.Observe(Vcs.FolderChangedAsync(gitMoved: false))));
        Vcs.SaveFirst = SaveAllFirstAsync;
        Vcs.MoveToTrash = TrashForGitAsync;
        Vcs.ChooseRemote = (_, remotes) => Task.FromResult<string?>(remotes.Contains("origin") ? "origin" : remotes[0]);
        Vcs.AskGit = AskGitAsync;
        Vcs.Work = Actions.Registry.Work;
        Vcs.Message += (_, message) => StatusText = message;
        Vcs.Changed += (_, _) => OnVcsChanged();
        Vcs.ConflictsFound += (_, _) => ShowConflicts();
        AddVcsActions();
        // Back from another program, the open files take what it changed on disk, and Git is read again.
        Activated += (_, _) =>
        {
            Vcs.ReloadOpenFiles();
            IdeErrors.Observe(Vcs.RefreshAsync());
        };
        // As the window starts to close, before its session ends: nothing of Git holds the folder from then on.
        Closing += (_, _) =>
        {
            vcsClosed = true;
            Unwatch();
            IdeErrors.Observe(Vcs.CloseAsync());
        };
        Closed += (_, _) =>
        {
            treeQuiet?.Dispose();
            Vcs.Dispose();
        };
    }

    /// <summary>The Commit window's view, as it is first shown or asked for.</summary>
    private CommitView MakeCommit()
    {
        CommitView view = commitView = new CommitView(Vcs);
        view.OpenRequested += (_, path) => OpenFromExplorer(path);
        view.ActionRequested += (_, action) => Actions.TryExecute(action);
        view.DiffRequested += (_, path) => IdeErrors.Observe(ShowDiffAsync(path));
        view.RollbackRequested += (_, changes) => IdeErrors.Observe(RollbackAsync(changes));
        view.RollbackRefused += (_, why) => StatusText = why;
        view.DeleteRequested += (_, path) => IdeErrors.Observe(DeleteAsync(path));
        view.IgnoreRequested += (_, path) => IdeErrors.Observe(IgnoreAsync(path));
        view.ResolveRequested += (_, _) => ShowConflicts();
        view.MergeRequested += (_, path) => IdeErrors.Observe(MergeAsync(path));
        view.Confirm = (title, text, yes) => new ConfirmDialog(title, text, yes).ShowDialog<bool>(this);
        return view;
    }

    /// <summary>The Git window's view, as it is first shown or asked for.</summary>
    private GitView MakeGit()
    {
        GitView view = gitView = new GitView(Vcs);
        view.DiffRequested += (_, chosen) => IdeErrors.Observe(ShowCommitDiffAsync(chosen.Commit, chosen.File));
        view.CompareRequested += (_, chosen) => IdeErrors.Observe(CompareWithAsync(chosen.Local, chosen.Commit.Hash, chosen.Commit.Short, chosen.File.Path));
        view.WorkingTreeDiffRequested += (_, chosen) =>
            IdeErrors.Observe(CompareWithAsync(chosen.File.Path, chosen.Branch.Ref.FullName, chosen.Branch.Name, chosen.File.From ?? chosen.File.Path));
        view.OpenRequested += (_, path) => OpenFromExplorer(path);
        view.ActionRequested += (_, action) => Actions.TryExecute(action);
        view.NewBranchRequested += (_, commit) =>
            Ask(ClosingAsking(new NamePopup($"New Branch from {commit.Short}", string.Empty, async name => (await Vcs.NewBranchAtAsync(name, commit.Hash)).Problem)));
        view.ResetRequested += (_, reset) => IdeErrors.Observe(ResetToAsync(reset));
        if (view.Log.Branches is { } branches)
        {
            branches.NewBranchRequested += (_, from) =>
                Ask(ClosingAsking(new NamePopup($"New Branch from {from.Name}", from.Kind == RefKind.Remote ? from.Name[(from.Name.IndexOf('/') + 1)..] : string.Empty,
                    async name => (await Vcs.NewBranchFromAsync(name, from)).Problem)));
            branches.RenameRequested += (_, branch) =>
                Ask(ClosingAsking(new NamePopup($"Rename {branch.Name}", branch.Name, async name => (await Vcs.RenameBranchAsync(branch, name)).Problem)));
            branches.DeleteRequested += (_, branch) => IdeErrors.Observe(DeleteBranchAsync(branch));
        }
        return view;
    }

    /// <summary>A popup that closes the window's asking popup once it is done.</summary>
    private NamePopup ClosingAsking(NamePopup popup)
    {
        popup.Done += (_, _) => CloseAsking();
        return popup;
    }

    /// <summary>The open folder's Git looked for and read as the folder opens; the watchers follow it.</summary>
    private async Task OpenVcsAsync(string root)
    {
        Unwatch();
        await Vcs.OpenAsync(root);
        // A window closing, or gone on to another folder, while Git looked: nothing is watched for it.
        if (!vcsClosed && Session is { } session && PathNames.Same(session.Root, root))
        {
            Watch(root);
        }
    }

    /// <summary>Whether the window has begun to close, after which Git watches nothing.</summary>
    private bool vcsClosed;

    /// <summary>The branch in the status line, the change bars and the Explorer follow what Git read.</summary>
    private void OnVcsChanged()
    {
        branchButton.IsVisible = Vcs.Repository is not null;
        branchButton.Content = Vcs.Branch ?? "Detached HEAD";
        ToolTip.SetTip(branchButton, Vcs.Repository is null ? Vcs.Refused ?? "Version control" : $"Git branch: {Vcs.Branch ?? "Detached HEAD"}");
    }

    // Watching the folder: what changes on disk (a build, a terminal's commit) is read again once it pauses.

    /// <summary>Watches the open folder for files changing and the Git folder in it for Git's own state moving, both read
    /// again once changes pause; a Git folder elsewhere the controller watches itself.</summary>
    private void Watch(string root)
    {
        if (Vcs.Repository is { } repository && PathNames.Same(repository.GitDir, Path.Join(root, ".git")))
        {
            ownGit = new GitWatcher(repository.GitDir);
            ownGit.Moved += (_, _) => IdeErrors.Observe(Vcs.FolderChangedAsync(gitMoved: true));
        }
        try
        {
            workingTree = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler heard = (_, e) => TreeChanged(e.FullPath, root);
            workingTree.Changed += heard;
            workingTree.Created += heard;
            workingTree.Deleted += heard;
            workingTree.Renamed += (_, e) => TreeChanged(e.FullPath, root);
            workingTree.Error += (_, _) => treeQuiet?.Change(TreeQuiet, Timeout.InfiniteTimeSpan);
            workingTree.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            workingTree?.Dispose();
            workingTree = null;
        }
    }

    /// <summary>How long the folder's changes pause before Git reads it again: a build writes many files at once.</summary>
    private static readonly TimeSpan TreeQuiet = TimeSpan.FromMilliseconds(400);

    /// <summary>A change in the open folder: one in its .git folder is the Git watcher's, the rest read Git again once they
    /// pause.</summary>
    private void TreeChanged(string path, string root)
    {
        if (!PathNames.Within(path, Path.Join(root, ".git")))
        {
            treeQuiet?.Change(TreeQuiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void Unwatch()
    {
        ownGit?.Dispose();
        ownGit = null;
        workingTree?.Dispose();
        workingTree = null;
        treeQuiet?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // The open files: each followed by Git for its change bars, and told of a move.

    /// <summary>A file opened in the window: Git follows its text for its change bars.</summary>
    private void DocumentOpened(OpenDocument document) => Vcs.Attach(document);

    /// <summary>A file the window let go of: Git follows it no more.</summary>
    private void DocumentClosed(OpenDocument document) => Vcs.Detach(document);

    /// <summary>Every open file with changes written before Git acts on the disk (a commit, a stash): the reason it could not
    /// be, else null.</summary>
    private Task<string?> SaveAllFirstAsync(Working? work, CancellationToken cancel)
    {
        foreach ((string path, OpenDocument open) in documents.Where(d => d.Value.IsModified).ToList())
        {
            try
            {
                var saved = FileText.SaveOver(path, open.File, open.Text.Snapshot.ToString());
                open.Text.MarkSaved();
                open.OnSaved(saved.Written);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult<string?>($"{Path.GetFileName(path)} could not be saved: {error.Message}");
            }
        }
        return Task.FromResult<string?>(null);
    }

    // The Git menu's actions.

    private void AddVcsActions()
    {
        Add(AppActions.Commit, "Commit…", () =>
        {
            ToolDock.Show(ToolWindowIds.Commit);
            IdeErrors.Observe(Vcs.RefreshAsync());
            CommitView.FocusMessage();
        });
        Add(AppActions.Push, "Push…", () => IdeErrors.Observe(Vcs.PushAsync()), remote: true);
        Add(AppActions.UpdateProject, "Update Project…", () => IdeErrors.Observe(Vcs.UpdateAsync()), remote: true);
        Add(AppActions.Fetch, "Fetch", () => IdeErrors.Observe(Vcs.FetchAsync()), remote: true);
        Add(AppActions.Branches, "Branches…", ShowAllBranches);
        Add(AppActions.Stash, "Stash Changes…", () =>
            Ask(ClosingAsking(new NamePopup("Stash Changes", string.Empty, async message => (await Vcs.StashAsync(message)).Problem))), () => Vcs.WhyNotStash);
        Add(AppActions.Unstash, "Unstash Changes…", () => IdeErrors.Observe(ShowUnstashAsync()));
        Add(AppActions.ShowDiff, "Show Diff", () => IdeErrors.Observe(ShowDiffAsync(SourcePath!)), onFile: true);
        Add(AppActions.Annotate, "Annotate with Git Blame", () => IdeErrors.Observe(ToggleAnnotationsAsync()), onFile: true);
        Add(AppActions.RollbackLines, "Rollback Lines", () => IdeErrors.Observe(RollbackLinesAsync()), onFile: true);
        Add(AppActions.NextChange, "Next Change", () => GoToChange(1), onFile: true);
        Add(AppActions.PreviousChange, "Previous Change", () => GoToChange(-1), onFile: true);
        Add(AppActions.ResolveConflicts, "Resolve Conflicts…", () => ShowConflicts(), () => Vcs.Conflicts.Count == 0 ? "No files are in conflict" : null);
        Actions.Registry.Add(new IdeAction(AppActions.CreateRepository, VcsController.CreateRepository, "Git", _ => IdeErrors.Observe(Vcs.InitAsync()))
        {
            WhyNot = _ => Vcs.WhyNotCreate,
        });
        Actions.Registry.Add(new IdeAction(AppActions.CommitSigning, SigningFailure.SetUp, "Git", _ => IdeErrors.Observe(ShowCommitSigningAsync()))
        {
            WhyNot = _ => Vcs.Repository is null ? Vcs.NoRepository : null,
        });
        Actions.Registry.Add(new IdeAction(AppActions.CommitRefresh, "Refresh Changes", "Git", _ => IdeErrors.Observe(Vcs.RefreshAsync()))
        {
            WhyNot = _ => Vcs.Repository is null ? Vcs.NoRepository : null,
        });
        Actions.Registry.Add(new IdeAction(AppActions.GitLogRefresh, "Refresh Git Log", "Git", _ =>
        {
            Vcs.Signatures.Forget();
            IdeErrors.Observe(GitWindow.RefreshAsync());
        })
        {
            WhyNot = _ => Vcs.Repository is null ? Vcs.NoRepository : null,
        });
        // A diff's own, whatever it compares: how its differences are highlighted, and its file shown.
        Actions.Registry.Add(new IdeAction(AppActions.DiffSettings, "Diff Settings", "Git", context => DiffWithKeys(context)!.ShowSettings())
        {
            WhyNot = context => DiffWithKeys(context) is null ? NoDiff : null,
        });
        Actions.Registry.Add(new IdeAction(AppActions.DiffJumpToSource, "Jump to Source", "Git", context => DiffWithKeys(context)!.AskForSource())
        {
            WhyNot = context => DiffWithKeys(context) is { } diff ? WhyNotJump(diff) : NoDiff,
        });

        void Add(string id, string title, Action run, Func<string?>? whyNot = null, bool onFile = false, bool remote = false) =>
            Actions.Add(new EditorAction(id, title, () => WhyNotGit(onFile, remote, whyNot) is null, () =>
            {
                if (WhyNotGit(onFile, remote, whyNot) is { } refused)
                {
                    StatusText = refused;
                    return;
                }
                run();
            }));
    }

    /// <summary>Why a Git action cannot run now: no folder in a repository, no file open where it acts on one, a remote
    /// unreachable from a folder not trusted, or its own reason.</summary>
    private string? WhyNotGit(bool onFile, bool remote, Func<string?>? whyNot) =>
        Session is null ? Refusals.NoFolder
        : Vcs.Repository is null ? Vcs.NoRepository
        : onFile && SourcePath is null ? "No file is open"
        : remote ? Vcs.WhyNotRemote
        : whyNot?.Invoke();

    /// <summary>The Git menu: its actions in Passenger's groups, each greyed with why where it cannot run.</summary>
    private MenuItem GitMenu()
    {
        MenuItem menu = new()
        {
            Header = "Git",
            Items =
            {
                MenuAction(AppActions.Commit), MenuAction(AppActions.Push), MenuAction(AppActions.UpdateProject), MenuAction(AppActions.Fetch),
                MenuAction(AppActions.Branches), new Separator(),
                MenuAction(AppActions.Stash), MenuAction(AppActions.Unstash), new Separator(),
                MenuAction(AppActions.ShowDiff), MenuAction(AppActions.Annotate), MenuAction(AppActions.RollbackLines),
                MenuAction(AppActions.ResolveConflicts), new Separator(),
                RegistryItem(AppActions.CreateRepository), RegistryItem(AppActions.CommitSigning), MenuAction(AppActions.CloneRepository),
            },
        };
        menu.SubmenuOpened += (_, _) => RefreshActions(menu);
        return menu;
    }

    /// <summary>A menu item for an action only the registry holds, greyed as it says.</summary>
    private MenuItem RegistryItem(string id)
    {
        IdeAction action = Actions.Registry[id]!;
        MenuItem item = new() { Header = action.Title, Tag = (Func<bool>)(() => action.CanRun(Actions.Registry.Context())) };
        item.Click += (_, _) => Actions.TryExecute(id);
        return item;
    }

    private const string NoDiff = "No diff has the keys";

    private static DiffView? DiffWithKeys(ActionContext context) => (context.Focused as Visual)?.FindAncestorOfType<DiffView>(includeSelf: true);

    private static string? WhyNotJump(DiffView diff) => diff.Source is { } file && File.Exists(file) ? null : $"{Path.GetFileName(diff.Source)} is not on disk";

    // Branches, stashes and resets.

    /// <summary>Branches…: the Git window's branches panel, its search box given the keys.</summary>
    private void ShowAllBranches()
    {
        ToolDock.Show(ToolWindowIds.Git);
        GitWindow.ShowLog();
        GitWindow.Log.Branches?.FocusSearch();
    }

    /// <summary>Unstash Changes: the stashes to pop, apply or drop (once the person agrees).</summary>
    public async Task<UnstashPopup?> ShowUnstashAsync()
    {
        UnstashPopup popup = new(await Vcs.StashesAsync(), async (stash, pop) => (await Vcs.UnstashAsync(stash, pop)).Succeeded,
            async stash => (await Vcs.DropStashAsync(stash)).Succeeded, (title, text, yes) => new ConfirmDialog(title, text, yes).ShowDialog<bool>(this));
        popup.Done += (_, _) =>
        {
            if (Asking == popup)
            {
                CloseAsking();
            }
        };
        Ask(popup);
        return popup;
    }

    /// <summary>A branch deleted once the person agrees, forced only once they agree again where it is not merged.</summary>
    private async Task DeleteBranchAsync(BranchInfo branch)
    {
        bool merged = branch.Kind != RefKind.Local || await Vcs.IsMergedAsync(branch);
        string what = branch.Kind == RefKind.Tag ? "tag" : branch.Kind == RefKind.Remote ? "remote branch" : "branch";
        string text = merged ? $"Delete the {what} {branch.Name}?" : $"{branch.Name} is not fully merged. Delete it, and the commits only it has?";
        if (await new ConfirmDialog($"Delete {branch.Name}", text, "Delete").ShowDialog<bool>(this))
        {
            await Vcs.DeleteRefAsync(branch, force: !merged);
        }
    }

    /// <summary>The Git window's Reset Current Branch to Here: a hard reset, which drops changes, only once confirmed.</summary>
    private async Task ResetToAsync((CommitInfo Commit, ResetMode Mode) reset)
    {
        if (reset.Mode == ResetMode.Hard && !await new ConfirmDialog("Reset Current Branch to Here",
            $"Move {Vcs.Branch ?? "HEAD"} to {reset.Commit.Short} and drop every change since, committed or not?", "Reset").ShowDialog<bool>(this))
        {
            return;
        }
        await Vcs.ResetAsync(reset.Commit.Hash, reset.Mode);
    }

    /// <summary>Rollback: the files listed, so what goes back is seen before it does, and done once the person agrees.</summary>
    private async Task RollbackAsync(IReadOnlyList<FileChange> changes)
    {
        if ((changes.Count == 0 ? "No files are chosen to roll back" : Vcs.WhyNotRollback(changes)) is { } refused)
        {
            StatusText = refused;
            return;
        }
        string what = changes.Count == 1 ? changes[0].Name : $"these {changes.Count} files";
        string kept = changes.Any(c => c.Kind is ChangeKind.Added) ? " Files added to Git stay on disk, unversioned." : string.Empty;
        bool unversioned = changes.Any(c => c.Kind is ChangeKind.Unversioned);
        bool tracked = changes.Any(c => c.Kind is not (ChangeKind.Unversioned or ChangeKind.Added));
        string lost = (unversioned ? " Unversioned files are moved to the Trash." : string.Empty)
            + (!tracked ? string.Empty : unversioned ? " The other changes cannot be undone." : " This cannot be undone.");
        if (await new ConfirmDialog("Rollback Changes", $"Roll back the changes to {what}?{kept}{lost}", "Rollback",
            detail: CommitView.FileList(changes, Vcs.Repository)).ShowDialog<bool>(this))
        {
            await Vcs.RollbackAsync(changes);
        }
    }

    /// <summary>Files Git's operation takes away that git has nothing of (a rollback's unversioned files), moved to the
    /// Trash: each that stayed, saying why.</summary>
    private static async Task<IReadOnlyList<string>> TrashForGitAsync(string command, IReadOnlyList<string> paths)
    {
        List<string> left = [];
        foreach (string path in paths)
        {
            try
            {
                await BackgroundWork.Run(() => Trash.Move(path));
            }
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                left.Add($"{Path.GetFileName(path)} could not be moved to the Trash: {refused.Message}");
            }
        }
        return left;
    }

    /// <summary>A changed file the Commit window asks to delete: to the Trash, once the person agrees.</summary>
    private async Task DeleteAsync(string path)
    {
        if (await new ConfirmDialog("Delete", $"Move {Path.GetFileName(path)} to the Trash?", "Delete").ShowDialog<bool>(this))
        {
            if (await TrashForGitAsync("Delete", [path]) is [var why, ..])
            {
                StatusText = why;
            }
            Forget(path);
            await Vcs.RefreshAsync();
        }
    }

    /// <summary>Ignore: an unversioned file's path, from the repository's top, added to its .gitignore.</summary>
    private async Task IgnoreAsync(string path)
    {
        if (Vcs.Repository is not { } repository)
        {
            return;
        }
        string ignore = Path.Join(repository.Root, ".gitignore");
        string line = "/" + Path.GetRelativePath(repository.Root, path).Replace('\\', '/');
        try
        {
            string before = File.Exists(ignore) ? await File.ReadAllTextAsync(ignore) : string.Empty;
            string separator = before.Length == 0 || before.EndsWith('\n') ? string.Empty : Environment.NewLine;
            await File.AppendAllTextAsync(ignore, separator + line + Environment.NewLine);
            StatusText = $"{line} added to .gitignore";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText = $".gitignore could not be written: {error.Message}";
        }
        await Vcs.RefreshAsync();
    }

    /// <summary>Commit Signing…: how commits are signed, set up in the dialog as git's config has it now, then written to
    /// git's config as chosen.</summary>
    private async Task ShowCommitSigningAsync()
    {
        SigningSetup setup = await Vcs.SigningAsync();
        CommitSigningDialog dialog = new(setup, Vcs.Repository, VcsController.GlobalAllowedSigners);
        await dialog.ShowDialog(this);
        if (dialog.Choice is { } choice)
        {
            await Vcs.SetUpSigningAsync(choice);
        }
    }

    // What git asks.

    /// <summary>The question git asked last while it reached a remote, while the window asks it; null once answered.</summary>
    public GitQuestionDialog? GitQuestion { get; private set; }

    /// <summary>What git asks while it reaches a remote (a login, a key's passphrase), put to the person at once, Stop
    /// taking the question away.</summary>
    private async Task<GitAnswer> AskGitAsync(GitQuestion question, CancellationToken stop)
    {
        UiThread.Check();
        GitQuestionDialog dialog = new(question);
        GitQuestion = dialog;
        IdeErrors.Write($"Git: asks the person for {question.Asks}");
        try
        {
            using CancellationTokenRegistration closing = stop.Register(() => UiThread.Post(() =>
            {
                if (dialog.IsVisible)
                {
                    dialog.Close();
                }
            }));
            await dialog.ShowDialog(this);
            if (dialog.Answer is { Text: not null } answer)
            {
                IdeErrors.Write($"Git: {question.Asks} given");
                return answer;
            }
            string why = stop.IsCancellationRequested ? $"{question.Asks} was not given: the operation was stopped" : $"{question.Asks} was cancelled";
            IdeErrors.Write($"Git: {why}");
            return GitAnswer.No(why);
        }
        finally
        {
            if (GitQuestion == dialog)
            {
                GitQuestion = null;
            }
        }
    }
}
