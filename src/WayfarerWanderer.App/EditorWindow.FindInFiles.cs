using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Explorer;
using WayfarerWanderer.App.Shell.Navigation;

namespace WayfarerWanderer.App;

/// <summary>Find in Files and Replace in Files, as Passenger has them: over the open folder, a module, a directory or a
/// named scope (the recent files, the open files, the file shown), under a file mask, an open file searched as the editor
/// has it. The scope, the mask and the directories and masks used lately are kept while the window is open.</summary>
public sealed partial class EditorWindow
{
    public const string RecentlyViewedScope = "Recently Viewed Files";
    public const string OpenFilesScope = "Open Files";
    public const string CurrentFileScope = "Current File";

    /// <summary>The most directories and masks kept.</summary>
    private const int FindKept = 15;

    private SearchScopeKind findKind;
    private string? findModule;
    private string? findDirectory;
    private string? findNamed;
    private string? findMask;
    private bool findRecursive = true;
    private readonly List<string> findDirectories = [];
    private readonly List<string> findMasks = [];

    private void AddFindInFiles()
    {
        AddFind(AppActions.FindInFiles, "Find in Files…", c => ShowFindInFiles(replace: false, ExplorerScope(c)));
        AddFind(AppActions.ReplaceInFiles, "Replace in Files…", c => ShowFindInFiles(replace: true, ExplorerScope(c)));

        void AddFind(string id, string title, Action<ActionContext> run) =>
            Actions.Registry.Add(new IdeAction(id, title, "Edit", run) { WhyNot = _ => Session is null ? Refusals.NoFolder : null });
    }

    /// <summary>The scope Find in Files takes on a row of the Project tool window: a folder's as a directory (a module's
    /// as the module), else the scope kept.</summary>
    private SearchScope? ExplorerScope(ActionContext context)
    {
        if (RowOf(context) is not { Kind: NodeKind.Folder, Path: { } folder })
        {
            return null;
        }
        SearchScope kept = KeptScope();
        return Modules().FirstOrDefault(m => PathNames.Same(m.Folder, folder)) is { } module
            ? kept with { Kind = SearchScopeKind.Module, Module = module.Folder }
            : kept with { Kind = SearchScopeKind.Directory, Directory = folder };
    }

    /// <summary>The project's modules by name, their folders each once; none before the project is read.</summary>
    private IReadOnlyList<SearchModule> Modules() =>
        Session?.Snapshot is { } snapshot
            ? [.. snapshot.Modules.Where(m => Directory.Exists(m.Root)).DistinctBy(m => PathNames.Canonical(m.Root)).Select(m => new SearchModule(m.Name, m.Root)).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)]
            : [];

    /// <summary>The scope kept, which the popup opens on: the module and directory chosen last, else the shown file's.</summary>
    private SearchScope KeptScope()
    {
        IReadOnlyList<SearchModule> modules = Modules();
        string? module = findModule is not null && modules.Any(m => m.Folder == findModule) ? findModule
            : modules.Where(m => SourcePath is { } shown && PathNames.IsUnder(shown, m.Folder)).OrderByDescending(m => m.Folder.Length).FirstOrDefault()?.Folder
            ?? modules.FirstOrDefault()?.Folder;
        string? directory = findDirectory ?? (SourcePath is { } path ? Path.GetDirectoryName(path) : Session?.Root);
        SearchScopeKind kind = findKind == SearchScopeKind.Module && module is null ? SearchScopeKind.Project : findKind;
        return new SearchScope(kind, module, directory, findRecursive, findNamed ?? OpenFilesScope, findMask);
    }

    /// <summary>Find in Files (or, with <paramref name="replace"/>, Replace in Files), seeded with the selection's word, in
    /// the scope given (from the Project tool window), else the one chosen last.</summary>
    public FindInFilesPopup? ShowFindInFiles(bool replace, SearchScope? on = null)
    {
        if (Session is null)
        {
            return null;
        }
        FindChoices choices = new(on ?? KeptScope(), Modules(), findDirectories, [RecentlyViewedScope, OpenFilesScope, CurrentFileScope], findMasks);
        FindInFilesPopup popup = new(SearchFilesAsync, TextOf, replace, SelectedWord(), choices, SettingsKept.Value.Ligatures);
        popup.ScopeChanged += (_, scope) => KeepFindScope(scope, closing: false);
        popup.DetachedFromVisualTree += (_, _) => KeepFindScope(popup.Scope, closing: true);
        popup.ReplaceRequested += (_, asked) => IdeErrors.Observe(ReplaceAllAsync(popup, asked));
        ShowSearch(popup, popup, match =>
        {
            KeepFindScope(popup.Scope, closing: true);
            if (match is null)
            {
                return;
            }
            NavigateTo(match.Path, new TextPosition(match.Line, match.Column));
            if (SourcePath is { } shown && PathNames.Same(shown, match.Path) && match.Line < Editor.Document.Snapshot.LineCount)
            {
                int at = Editor.Document.Snapshot.GetOffset(new TextPosition(match.Line, match.Column));
                Editor.Select(new TextRange(at, Math.Min(match.Length, Editor.Document.Snapshot.Length - at)));
            }
        });
        return popup;
    }

    /// <summary>A file's text as the editor has it, else as the disk does; null for one that cannot be read as text.</summary>
    private TextSnapshot? TextOf(string path)
    {
        if (FindOpenDocument(path) is { } open)
        {
            return open.Snapshot;
        }
        try
        {
            return FileText.Load(path) is { } file ? TextSnapshot.From(file.Text) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The open files' texts as they stand now, taken on the window's thread for a search off it.</summary>
    private Func<string, TextSnapshot?> OpenTexts()
    {
        Dictionary<string, TextSnapshot> open = documents.ToDictionary(d => d.Key, d => d.Value.Document.Snapshot, PathNames.Comparer);
        return path => open.GetValueOrDefault(path);
    }

    private Task<FilesFound> SearchFilesAsync(SearchQuery query, SearchScope scope, CancellationToken cancel) =>
        SearchFilesAsync(query, scope, cancel, FileSearch.MostMatches);

    /// <summary>The matches of a query in the files a scope names, each passing its mask: the folder's, a module's or a
    /// directory's walked off the window's thread as Go to File walks them, or a named scope's.</summary>
    private Task<FilesFound> SearchFilesAsync(SearchQuery query, SearchScope scope, CancellationToken cancel, int most)
    {
        string root = Session?.Root ?? string.Empty;
        FileMask? mask = FileMask.Parse(scope.Mask);
        IReadOnlyList<string> named = scope.Named switch
        {
            RecentlyViewedScope => [.. recentFiles],
            OpenFilesScope => [.. documents.Keys],
            CurrentFileScope => SourcePath is { } shown ? [shown] : [],
            _ => [],
        };
        Func<string, TextSnapshot?> open = OpenTexts();
        return BackgroundWork.Run(() =>
        {
            IEnumerable<string> files = scope.Kind switch
            {
                SearchScopeKind.Project => Walked(root),
                SearchScopeKind.Module when scope.Module is { } module => Walked(module),
                SearchScopeKind.Directory when scope.Directory is { Length: > 0 } directory && Directory.Exists(directory) =>
                    scope.Recursive ? Walked(directory) : Directory.EnumerateFiles(directory).Order(StringComparer.OrdinalIgnoreCase),
                SearchScopeKind.Scope => named,
                _ => [],
            };
            return FileSearch.FindAsync(files.Where(f => mask?.Matches(Path.GetFileName(f)) ?? true), query, open, cancel, most);
        }, cancel);

        IEnumerable<string> Walked(string folder) =>
            FileIndex.Of(folder, cancel).Files.Select(relative => Path.Combine(folder, relative));
    }

    /// <summary>Keeps what the popup shows; as it closes, its directory and mask go to the top of those used lately.</summary>
    private void KeepFindScope(SearchScope scope, bool closing)
    {
        string? directory = string.IsNullOrWhiteSpace(scope.Directory) ? null : scope.Directory.Trim();
        findKind = scope.Kind;
        findModule = scope.Module ?? findModule;
        findDirectory = directory ?? findDirectory;
        findNamed = scope.Named ?? findNamed;
        findRecursive = scope.Recursive;
        findMask = string.IsNullOrWhiteSpace(scope.Mask) ? null : scope.Mask.Trim();
        if (!closing)
        {
            return;
        }
        if (scope.Kind == SearchScopeKind.Directory && directory is not null)
        {
            Keep(findDirectories, directory, FindKept);
        }
        if (findMask is { } mask)
        {
            Keep(findMasks, mask, FindKept);
        }
    }

    /// <summary>The popup's Replace All, once confirmed: every match in its scope replaced (those past the list's limit
    /// found first), an open file's as one edit of its own that Undo takes back, a closed file's written in its own
    /// encoding; files refused are named in the status line, and the popup searches again over what is left.</summary>
    private async Task ReplaceAllAsync(FindInFilesPopup popup, (SearchQuery Query, string Replacement, FilesFound Found) asked)
    {
        FilesFound found = asked.Found;
        if (found.Matches.Count >= FileSearch.MostMatches)
        {
            found = await SearchFilesAsync(asked.Query, popup.Scope, CancellationToken.None, int.MaxValue);
        }
        IReadOnlyList<SearchMatch> replaceable = found.Replaceable;
        if (replaceable.Count == 0)
        {
            StatusText = found.StoppedIn(Path.GetFileName) is { } stopped ? $"Nothing replaced: {stopped}" : "Nothing to replace";
            return;
        }
        string what = FilesFound.Count(replaceable);
        bool yes = await ConfirmAsync("Replace in Files", $"Replace {what} with '{asked.Replacement}'? A closed file is written; an open file's change can be undone in it.", "Replace All");
        if (yes)
        {
            StatusText = await ReplaceInFilesAsync(asked.Query, asked.Replacement, replaceable);
            await popup.SearchAsync();
        }
        if (ReferenceEquals(SearchShown, popup))
        {
            popup.QueryBox.Focus();
        }
    }

    /// <summary>Replaces a query's matches in the files they were found in: what the status line says of it.</summary>
    public async Task<string> ReplaceInFilesAsync(SearchQuery query, string replacement, IReadOnlyList<SearchMatch> matches)
    {
        SearchPattern pattern = SearchPattern.For(query, TextSearch.Apart);
        List<(string Path, string Why)> refused = [];
        List<string> converted = [];
        List<(string Path, TextSnapshot? Open)> files = [];
        foreach (string path in matches.Select(m => m.Path).Distinct(PathNames.Comparer))
        {
            TextDocument? open = FindOpenDocument(path);
            if (open is { IsReadOnly: true })
            {
                refused.Add((path, FileSearch.ReadOnlyWhy));
                continue;
            }
            files.Add((path, open?.Snapshot));
        }
        List<FileSearch.Replacing> planned = await BackgroundWork.Run(() => files.Select(f => FileSearch.Plan(f.Path, f.Open, pattern, replacement)).ToList());
        List<FileSearch.Replacing> closed = [];
        HashSet<string> changed = new(PathNames.Comparer);
        foreach (FileSearch.Replacing plan in planned)
        {
            if (plan.Why is { } why)
            {
                refused.Add((plan.Path, why));
            }
            else if (plan.Searched is { } searched)
            {
                if (FindOpenDocument(plan.Path) is not { } open || !ReferenceEquals(open.Snapshot, searched))
                {
                    refused.Add((plan.Path, FileSearch.ChangedWhy));
                }
                else if (plan.Edits.Count > 0)
                {
                    SelectionSet before = ReferenceEquals(Editor.Document, open) ? Editor.Selections : SelectionSet.Single(0);
                    open.Edit(plan.Edits, before, TextEditing.MapThrough(before, plan.Edits));
                    changed.Add(plan.Path);
                }
            }
            else if (plan is { Closed: not null, After: not null })
            {
                closed.Add(plan);
            }
        }
        List<(string Path, (string? Refused, string? Converted)? Written)> writes =
            await BackgroundWork.Run(() => closed.Select(plan => (plan.Path, FileSearch.Write(plan))).ToList());
        foreach ((string path, (string? Refused, string? Converted)? written) in writes)
        {
            if (written is { Refused: { } why })
            {
                refused.Add((path, why));
            }
            else if (written is { } done)
            {
                changed.Add(path);
                if (done.Converted is { } note)
                {
                    converted.Add(note);
                }
            }
        }
        if (changed.Count > 0)
        {
            CameOrWent([.. changed]);
        }
        IReadOnlyList<SearchMatch> replaced = [.. matches.Where(m => changed.Contains(m.Path))];
        string? left = refused.Count == 0 ? null : "not replaced: " + string.Join(", ", refused.Select(r => $"{Path.GetFileName(r.Path)} ({r.Why})"));
        return string.Join("; ", [replaced.Count > 0 ? $"Replaced {FilesFound.Count(replaced)}" : "Nothing replaced", .. converted, .. left is null ? [] : new[] { left }]);
    }
}
