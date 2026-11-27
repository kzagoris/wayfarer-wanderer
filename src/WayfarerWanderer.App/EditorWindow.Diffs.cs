using Avalonia.Controls;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Vcs;
using WayfarerWanderer.App.Shell.Workspace;

namespace WayfarerWanderer.App;

/// <summary>The window's diffs, change bars, blame and merges, as Passenger's MainWindow.Vcs, .History and .Conflicts have
/// them: a diff in a window of its own (Wayfarer's editor has no tabs to put one in), the change bars and blame in the
/// editor's gutter, and the Conflicts dialog with its merge window.</summary>
public sealed partial class EditorWindow
{
    private ConflictsDialog? conflictsDialog;

    /// <summary>The editor's Git blame column, left of the breakpoints.</summary>
    private BlameColumn Blame { get; } = new();

    /// <summary>The editor's change bars, at the gutter's right.</summary>
    private ChangeColumn? changeBars;

    /// <summary>The Conflicts dialog while it is open.</summary>
    public ConflictsDialog? ConflictsWindow => conflictsDialog;

    /// <summary>The diff windows open over this one.</summary>
    public IEnumerable<DiffWindow> DiffWindows => OwnedWindows.OfType<DiffWindow>();

    /// <summary>The gutter's columns: Git blame and the breakpoints at its left, the change bars at its right; a click on a
    /// change bar shows what the lines were, and one on a blamed line its commit.</summary>
    private void AddGutter()
    {
        changeBars = new ChangeColumn(Editor);
        changeBars.Pressed += (_, pressed) =>
        {
            if (ShownFile is { } document)
            {
                IdeErrors.Observe(ShowChangePopupAsync(document, pressed.Change));
            }
        };
        Blame.Pressed += (_, line) =>
        {
            if (ShownFile is { } document)
            {
                IdeErrors.Observe(ShowBlamedCommitAsync(document, line));
            }
        };
        Editor.SetGutterColumns([Blame, Breakpoints], [changeBars]);
    }

    /// <summary>The open file the editor shows, if any.</summary>
    private OpenDocument? ShownFile => SourcePath is { } path && documents.TryGetValue(path, out OpenDocument? document) ? document : null;

    /// <summary>The blame column follows the file shown.</summary>
    private void ShowBlameOf(OpenDocument? document) => Blame.Show(document);

    // Diffs.

    /// <summary>How the window's diffs and merges are made: Highlighting Differences as Settings has it, kept when a diff
    /// changes it.</summary>
    private DiffPanes Diffs => diffs ??= new(() => SettingsKept.Effective.DiffHighlighting,
        way => SettingsKept.Update(now => now with { DiffHighlighting = way }));

    /// <summary>Show Diff: the last commit's text beside the file's, the right side the open file's own text when it is open,
    /// so an edit or a change put back there is the file's. A binary file has no lines to compare: the status line says how
    /// it differs.</summary>
    public async Task<DiffWindow?> ShowDiffAsync(string path)
    {
        if (Vcs.Repository is null)
        {
            StatusText = Vcs.NoRepository;
            return null;
        }
        ModeChange? mode = Vcs.Changes.FirstOrDefault(c => PathNames.Same(c.Path, path))?.Mode;
        if (await Vcs.BinaryChangeAsync(path) is { } binary)
        {
            StatusText = mode is null ? binary.Said(Path.GetFileName(path)) : $"{binary.Said(Path.GetFileName(path))}; {mode.Said}";
            return null;
        }
        OpenDocument? open = documents.GetValueOrDefault(path);
        Task<string?> encoding = Vcs.EncodingChangeAsync(path);
        Task<bool> breaksAlike = Vcs.BreaksStoredAlikeAsync(path);
        TextDocument mine = open?.Text ?? new TextDocument(ReadText(path) ?? string.Empty);
        MergeSide? noFile = open is null && !File.Exists(path) ? MergeSide.Right : null;
        // An open file's differences are its change bars' own when they are of its text now.
        if (open is null || await Vcs.DiffAsync(open) is not { } diff)
        {
            string? head = await Vcs.HeadTextAsync(path);
            noFile = head is null ? MergeSide.Left : noFile;
            TextSnapshot text = mine.Snapshot;
            diff = await BackgroundWork.Run(() => FileDiff.Compare(head ?? string.Empty, text));
        }
        // Line breaks git converts as it stores the file are no difference (core.autocrlf, a text or eol attribute).
        diff = diff.StoringBreaksAlike(await breaksAlike);
        string[] besides = [.. new[] { await encoding, mode?.Said }.OfType<string>()];
        return ShowLocalDiff(path, open, mine, diff, "HEAD", note: besides.Length == 0 ? null : string.Join("; ", besides), noFile: noFile);
    }

    /// <summary>A version of a file beside the file as it is now: <paramref name="revision"/>'s text of it at
    /// <paramref name="then"/> (where it was in that version), the right side the open file's own text when it is open. A
    /// file <paramref name="added"/> since that version, which has none of it, is beside an empty side.</summary>
    public async Task<DiffWindow?> CompareWithAsync(string path, string revision, string version, string then, bool added = false)
    {
        if (Vcs.Repository is null)
        {
            StatusText = Vcs.NoRepository;
            return null;
        }
        FileVersion? was = await Vcs.VersionAsync(revision, then, path);
        if ((was is null && added ? string.Empty : was?.Text) is not { } theirs)
        {
            StatusText = was is null ? $"{Path.GetFileName(then)} is not in {version}" : $"{Path.GetFileName(then)} is binary in {version}, so it has no lines to compare";
            return null;
        }
        OpenDocument? open = documents.GetValueOrDefault(path);
        TextDocument mine = open?.Text ?? new TextDocument(ReadText(path) ?? string.Empty);
        TextSnapshot text = mine.Snapshot;
        Task<bool> breaksAlike = was is null ? Task.FromResult(false) : Vcs.BreaksStoredAlikeAsync(revision, then, was, path);
        FileDiff diff = (await BackgroundWork.Run(() => FileDiff.Compare(theirs, text))).StoringBreaksAlike(await breaksAlike);
        MergeSide? noFile = was is null ? MergeSide.Left : open is null && !File.Exists(path) ? MergeSide.Right : null;
        return ShowLocalDiff(path, open, mine, diff, version, noFile: noFile);
    }

    /// <summary>What a commit did to a file: its first parent's text beside its own, both read-only.</summary>
    public async Task<DiffWindow?> ShowCommitDiffAsync(CommitInfo commit, FileChange file)
    {
        if (Vcs.Repository is null)
        {
            return null;
        }
        string? parent = commit.Parents.Count > 0 ? commit.Parents[0] : null;
        string before = parent is null || file.Kind is ChangeKind.Added ? string.Empty : (await Vcs.VersionAsync(parent, file.From ?? file.Path, file.Path))?.Text ?? string.Empty;
        string after = file.Kind is ChangeKind.Deleted ? string.Empty : (await Vcs.VersionAsync(commit.Hash, file.Path, file.Path))?.Text ?? string.Empty;
        string leftTitle = parent is null ? "Before the first commit" : GitHashes.Short(parent);
        MergeSide? noFile = parent is null || file.Kind is ChangeKind.Added ? MergeSide.Left : file.Kind is ChangeKind.Deleted ? MergeSide.Right : null;
        TextDocument mine = new(after);
        TextSnapshot text = mine.Snapshot;
        FileDiff diff = await BackgroundWork.Run(() => FileDiff.Compare(before, text));
        return ShowDiff(new DiffContent($"{file.Name}: {commit.Short} {commit.Subject}", $"{commit.Hash}\n{file.From ?? file.Path}\n{file.Path}", file.Path, null,
            () => Diffs.ForFile(file.Name, diff, mine, null, (leftTitle, commit.Short), file.From ?? file.Path, file.Path, noFile)));
    }

    /// <summary>A version's text beside the file's, the right side the open file's own text when it is still open,
    /// <paramref name="note"/> said after the differences, <paramref name="noFile"/> the side whose version has no such
    /// file; the Commit window's boxes beside each change of HEAD's diff.</summary>
    private DiffWindow ShowLocalDiff(string path, OpenDocument? open, TextDocument mine, FileDiff diff, string version, string? note = null,
        MergeSide? noFile = null)
    {
        // Closed while the differences were read, the file's text is one nothing would save: it is shown, not edited.
        if (open is not null && !documents.ContainsValue(open))
        {
            open = null;
        }
        string title = $"{Path.GetFileName(path)}: {version} and your version";
        (string Left, string Right) titles = (version, open is not null ? "Your version" : "Your version (read-only)");
        return ShowDiff(new DiffContent(title, $"{version}\n{path}\n{path}", path, open, () =>
        {
            DiffView view = Diffs.ForFile(Path.GetFileName(path), diff, mine, open, titles, path, path, noFile);
            view.Note = note;
            // A file not open has a read-only copy for its side: a change rolled back there is written to the file on disk.
            if (open is null)
            {
                view.PutsBackOnDisk = (compared, edit, what) => PutBackOnDiskAsync(path, version, compared, edit, what);
                FollowDisk(path, view);
            }
            if (version == "HEAD" && note is null && commitView is { } commits)
            {
                commits.ConfigureDiff(path, view);
            }
            if (version == "HEAD")
            {
                StageIn(path, view);
            }
            return view;
        }));
    }

    /// <summary>Shows a diff in a window of its own over this one, closed once its file closes; Jump to Source shows the file.
    /// The keys of a diff's own actions (Stage, Unstage, Rollback Lines, Diff Settings, Jump to Source, Go to Declaration)
    /// run them in the diff.</summary>
    private DiffWindow ShowDiff(DiffContent shown)
    {
        DiffView view = shown.Make();
        DiffWindow window = new(shown, view);
        view.SourceRequested += (_, _) =>
        {
            if (view.Source is { } file && File.Exists(file))
            {
                window.Close();
                OpenFromExplorer(file);
                Editor.GoTo(new TextPosition(Math.Min(view.Right.CaretPosition.Line, Editor.Document.Snapshot.LineCount - 1), 0));
            }
        };
        window.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (keys.ActionFor(new Shortcut(Keystroke.From(e))) is { } pressed && DiffActions.Contains(pressed))
            {
                ActionContext context = new(Editor, window.FocusManager?.GetFocusedElement());
                e.Handled = pressed == AppActions.GoToDeclaration ? GoToDeclarationFromDiff(view, window) : Actions.Registry.Run(pressed, context) is null;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        window.Show(this);
        return window;
    }

    /// <summary>The actions whose keys a diff's window takes for its diff.</summary>
    private static readonly HashSet<string> DiffActions =
        [AppActions.Stage, AppActions.Unstage, AppActions.RollbackLines, AppActions.DiffSettings, AppActions.DiffJumpToSource, AppActions.GoToDeclaration];

    /// <summary>Go to Declaration asked on a diff's side that is the open file's own text: the file is shown at the diff's
    /// caret, where Go to Declaration goes from, as from the file's editor. False where no such side has the keys.</summary>
    private bool GoToDeclarationFromDiff(DiffView view, DiffWindow window)
    {
        if (!view.IsEditable || !view.Right.IsKeyboardFocusWithin || view.Source is not { } file || !File.Exists(file))
        {
            return false;
        }
        TextPosition caret = view.Right.CaretPosition;
        window.Close();
        OpenFromExplorer(file);
        int line = Math.Min(caret.Line, Editor.Document.Snapshot.LineCount - 1);
        Editor.GoTo(new TextPosition(line, Math.Min(caret.Column, Editor.Document.Snapshot.GetLineText(line).Length)));
        Actions.TryExecute(AppActions.GoToDeclaration);
        return true;
    }

    /// <summary>A diff's change put back in a file not open (<see cref="DiffView.PutsBackOnDisk"/>): written as a save writes
    /// it, keeping its encoding and line breaks, unless the file changed on disk since its diff read it or was opened since.
    /// Null once written, else why not.</summary>
    private Task<string?> PutBackOnDiskAsync(string path, string version, TextSnapshot compared, TextEdit edit, string what)
    {
        string? why;
        try
        {
            why = documents.ContainsKey(path) ? $"{Path.GetFileName(path)} was opened since its diff was shown: roll the change back there"
                : FileText.Load(path) is not { } file || file.Text != compared.ToString() ? $"{Path.GetFileName(path)} changed on disk since its diff read it: roll the change back again"
                : null;
            if (why is null && FileText.Load(path) is { } now)
            {
                FileText.SaveOver(path, now, string.Concat(now.Text.AsSpan(0, edit.Start), edit.Text, now.Text.AsSpan(edit.Start + edit.Length)));
                StatusText = $"Rolled back {what} of {Path.GetFileName(path)} to {version}";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            why = $"{Path.GetFileName(path)} could not be written: {error.Message}";
        }
        if (why is null)
        {
            IdeErrors.Observe(Vcs.RefreshAsync());
        }
        return Task.FromResult(why);
    }

    /// <summary>A diff whose file's side is a copy of a file not open takes the file's text again from disk whenever git's
    /// status is read (after a rollback written there, another program's write), read off the window's thread.</summary>
    private void FollowDisk(string path, DiffView view)
    {
        void Follow(object? sender, EventArgs e) => IdeErrors.Observe(FollowDiskAsync());
        async Task FollowDiskAsync() => view.FollowDisk(await BackgroundWork.Run(() => ReadText(path)));
        view.AttachedToVisualTree += (_, _) => Vcs.Changed += Follow;
        view.DetachedFromVisualTree += (_, _) => Vcs.Changed -= Follow;
    }

    /// <summary>A diff of a changed text file against the last commit stages its changes into git's index: a change's own
    /// button and Stage's keys turn as the index holds it, read again whenever the status is.</summary>
    private void StageIn(string path, DiffView view)
    {
        if (Vcs.Changes.FirstOrDefault(c => PathNames.Same(c.Path, path)) is not { Kind: ChangeKind.Modified or ChangeKind.Renamed, Submodule: SubmoduleChange.None })
        {
            return;
        }
        view.StageWith(diff => HeldInIndexAsync(path, diff));
        view.StageRequested += (_, asked) => IdeErrors.Observe(StageChangesAsync(view, path, asked.Changes, asked.Stage));
        void Follow(object? sender, EventArgs e) => view.RefreshStaged();
        view.AttachedToVisualTree += (_, _) => Vcs.Changed += Follow;
        view.DetachedFromVisualTree += (_, _) => Vcs.Changed -= Follow;
    }

    /// <summary>Which changes of a file's diff git's index holds whole; null where it holds no text of it.</summary>
    private async Task<bool[]?> HeldInIndexAsync(string path, FileDiff diff)
    {
        if (await Vcs.IndexTextAsync(path) is not { } index)
        {
            return null;
        }
        TextSnapshot text = diff.Text;
        bool alike = diff.BreaksStoredAlike;
        return await BackgroundWork.Run(() => diff.HeldIn(FileDiff.Compare(index, text).StoringBreaksAlike(alike)));
    }

    /// <summary>Stages or unstages some of a file's changes (or just the lines chosen of them) from its diff, said in the
    /// status line.</summary>
    private async Task StageChangesAsync(DiffView view, string path, IReadOnlyList<DiffHunk> changes, bool stage)
    {
        if (Vcs.Changes.FirstOrDefault(c => PathNames.Same(c.Path, path)) is not { } change)
        {
            StatusText = $"{Path.GetFileName(path)} has no change to {(stage ? "stage" : "unstage")}";
            return;
        }
        FileDiff diff = view.Compared;
        VcsOutcome outcome = stage
            ? await Vcs.StageChangesAsync(path, new PartialCommit(diff, changes, change.From))
            : await Vcs.UnstageChangesAsync(change, diff, changes);
        StatusText = outcome.Message;
        view.RefreshStaged();
    }

    /// <summary>Stages or unstages files whole, from the Commit window, said in the status line.</summary>
    private async Task StageFilesAsync(IReadOnlyList<FileChange> changes, bool stage)
    {
        if (Vcs.WhyNotStage(changes) is { } refused)
        {
            StatusText = refused;
            return;
        }
        StatusText = (await Vcs.StageAsync(changes, stage)).Message;
    }

    /// <summary>A file's text as the window would read it, or null where it is binary or cannot be read.</summary>
    private static string? ReadText(string path)
    {
        try
        {
            return FileText.Load(path)?.Text;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Change bars.

    /// <summary>Next Change and Previous Change: the caret to the start of the next or previous change bar, round past
    /// the file's end.</summary>
    private void GoToChange(int step)
    {
        if (ShownFile is not { } document || document.ChangeMarks.Count == 0)
        {
            StatusText = "No changes in this file";
            return;
        }
        int line = Editor.CaretPosition.Line;
        IReadOnlyList<ChangeMark> marks = [.. document.ChangeMarks.OrderBy(m => m.Line)];
        ChangeMark target = step > 0
            ? marks.FirstOrDefault(m => m.Line > line, marks[0])
            : marks.LastOrDefault(m => m.Line + Math.Max(1, m.Count) <= line, marks[^1]);
        Editor.GoTo(new TextPosition(Math.Min(target.Line, document.Text.Snapshot.LineCount - 1), 0));
    }

    /// <summary>Rollback Lines in a diff: the change at the caret of the side with the keys put back, said once done.</summary>
    private async Task RollBackInDiffAsync(DiffView diff) => StatusText = (await diff.RollBackAtCaretAsync()).Said;

    /// <summary>Rollback Lines: the change under the caret goes back to what the last commit has there.</summary>
    private async Task RollbackLinesAsync()
    {
        if (ShownFile is not { } document)
        {
            StatusText = DiffView.NoChangeHere;
            return;
        }
        FileDiff? diff;
        using (BackgroundWork.Scope())
        {
            // A comparison of a text edited while it ran says nothing of where the change is now: the newer is compared.
            do
            {
                diff = await Vcs.DiffAsync(document);
            }
            while (diff is not null && !ReferenceEquals(diff.Text, document.Text.Snapshot) && ShownFile == document);
        }
        if (ShownFile != document)
        {
            return;
        }
        if (diff?.HunkAt(Editor.CaretPosition.Line) is not { } hunk)
        {
            StatusText = DiffView.NoChangeHere;
            return;
        }
        Editor.ApplyEdits([diff.Restore(hunk)]);
        StatusText = $"Rolled back the change at line {hunk.NewStart + 1} of {document.Name}";
    }

    /// <summary>A click on a change bar: a popup of what the lines were, with the change's actions.</summary>
    private async Task ShowChangePopupAsync(OpenDocument document, ChangeMark change)
    {
        FileDiff? diff;
        using (BackgroundWork.Scope())
        {
            diff = await Vcs.DiffAsync(document);
        }
        if (diff is null || ShownFile != document)
        {
            return;
        }
        DiffHunk? hunk = diff.Hunks.Cast<DiffHunk?>().FirstOrDefault(h => h!.Value.NewStart == change.Line);
        string? before = hunk is { OldCount: > 0 } found ? diff.Before(found) : null;
        void Then(Action run)
        {
            CloseAsking();
            run();
        }
        ChangePopup popup = new(
            before,
            SettingsKept.Effective.ChangePopupWords,
            previous: () => Then(() => GoToChange(-1)),
            next: () => Then(() => GoToChange(1)),
            rollback: () => Then(() =>
            {
                Editor.GoTo(new TextPosition(Math.Min(change.Line, document.Text.Snapshot.LineCount - 1), 0));
                IdeErrors.Observe(RollbackLinesAsync());
            }),
            diff: () => Then(() => IdeErrors.Observe(ShowDiffAsync(document.Path))),
            highlightWordsChanged: on => SettingsKept.Update(now => now with { ChangePopupWords = on }));
        Ask(popup);
        if (hunk is { } compared)
        {
            InlineChanges words = await BackgroundWork.Run(() => diff.InnerChanges(compared, DiffHighlighting.Words));
            if (Asking == popup)
            {
                popup.ShowWords(words.Left);
            }
        }
    }

    // Blame.

    /// <summary>Annotate with Git Blame, or Close Annotations when the file shows them.</summary>
    private async Task ToggleAnnotationsAsync()
    {
        if (ShownFile is not { } document)
        {
            return;
        }
        if (Vcs.IsAnnotated(document))
        {
            Vcs.CloseAnnotations(document);
            return;
        }
        if (!await Vcs.AnnotateAsync(document))
        {
            StatusText = $"{Path.GetFileName(document.Path)} is not tracked by Git";
        }
    }

    /// <summary>A click on a line's annotation: its commit, chosen in the Git window's log.</summary>
    private async Task ShowBlamedCommitAsync(OpenDocument document, int line)
    {
        if (Vcs.BlameOf(document) is not { } lines || line >= lines.Count || !lines[line].IsCommitted)
        {
            return;
        }
        ToolDock.Show(Shell.ToolWindowIds.Git);
        GitWindow.ShowLog();
        await GitWindow.Log.SelectHashAsync(lines[line].Hash);
        StatusText = $"{lines[line].Short} {lines[line].Summary} ({lines[line].Author}, {lines[line].Time.ToLocalTime():f})";
    }

    // Conflicts.

    /// <summary>The Conflicts dialog over the window, or the one open already; none when nothing is in conflict. Once it
    /// closes with every conflict of a rebase resolved, the rebase is offered to go on.</summary>
    public ConflictsDialog? ShowConflicts()
    {
        if (Vcs.Repository is null || Vcs.Conflicts.Count == 0)
        {
            StatusText = Vcs.Repository is null ? Vcs.NoRepository : "No conflicts found";
            return null;
        }
        if (conflictsDialog is { } open)
        {
            open.Activate();
            return open;
        }
        ConflictsDialog dialog = null!;
        dialog = new ConflictsDialog(Vcs, conflict => MergeAsync(conflict.Path, dialog));
        conflictsDialog = dialog;
        dialog.Closed += (_, _) =>
        {
            conflictsDialog = null;
            IdeErrors.Observe(OfferToContinueRebaseAsync());
        };
        IdeErrors.Observe(dialog.ShowDialog(this));
        return dialog;
    }

    /// <summary>Once the Conflicts dialog closes with a rebase's last conflict resolved, whether to continue the rebase.</summary>
    private async Task OfferToContinueRebaseAsync()
    {
        await Vcs.Idle;
        if (Vcs.Merge.Operation is MergeOperation.Rebase && Vcs.Conflicts.Count == 0
            && await new ConfirmDialog("Resolve Conflicts", "All conflicts have been resolved. Do you want to continue rebase?", "Continue Rebase").ShowDialog<bool>(this))
        {
            await Vcs.ContinueRebaseAsync();
        }
    }

    /// <summary>The merge window on a conflicted file, over the Conflicts dialog (or this window), done once it closes:
    /// whether the file was resolved there. A file a side lacks, or a binary one, has nothing to merge line by line: the
    /// status line says how the Conflicts dialog resolves it.</summary>
    public async Task<bool> MergeAsync(string path, Window? over = null)
    {
        if (Vcs.Repository is not { } repository || Vcs.Conflicts.FirstOrDefault(c => c.Path == path) is not { } conflict)
        {
            return false;
        }
        if (!conflict.CanMerge)
        {
            Unmergeable(conflict);
            return false;
        }
        // What the editor shows is on disk before the merge reads and writes it.
        if (await SaveAllFirstAsync(null, CancellationToken.None) is { } unsaved)
        {
            StatusText = unsaved;
            return false;
        }
        if (await Vcs.MergeTextsAsync(path) is not { } texts)
        {
            if (Vcs.Repository is not null)
            {
                Unmergeable(conflict with { IsBinary = true });
            }
            return false;
        }
        MergeView view = DiffPanes.ForMerge(texts, path);
        MergeWindow window = null!;
        window = new MergeWindow($"Merge Revisions for {repository.Relative(path)}", view, async text => (await Vcs.ResolveAsync(path, text)).Succeeded,
            (title, text, yes, no) => new ConfirmDialog(title, text, yes, no).ShowDialog<bool>(window));
        Window owner = over is { IsVisible: true } ? over : this;
        await window.ShowDialog<bool?>(owner);
        return window.Resolved;
    }

    /// <summary>The Conflicts dialog with a file one side lacks (or a binary one) chosen, and the status line saying how it
    /// is resolved.</summary>
    private void Unmergeable(GitConflict conflict)
    {
        ConflictSides sides = Vcs.Sides;
        bool yours = sides.YoursHas(conflict);
        bool theirs = sides.TheirsHas(conflict);
        string why = conflict.IsBinary && yours && theirs
            ? $"{conflict.Name} is binary, so it has no lines to merge: Accept Yours or Accept Theirs keeps that side's file whole"
            : yours || theirs
            ? $"{conflict.Name} is only in {(yours ? "yours" : "theirs")}, so there is nothing to merge: Accept {(yours ? "Yours" : "Theirs")} keeps it, Accept {(yours ? "Theirs" : "Yours")} deletes it"
            : $"{conflict.Name} is in neither side, so there is nothing to merge: Accept Yours or Accept Theirs deletes it";
        if (ShowConflicts() is { } dialog)
        {
            dialog.Chosen = [.. dialog.Rows.Where(r => r.Conflict.Path == conflict.Path)];
        }
        StatusText = why;
    }
}
