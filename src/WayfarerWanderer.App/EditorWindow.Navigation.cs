using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Navigation;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>Getting about the code, as Passenger's Navigator does it: Find Action, Search Everywhere (types, then files,
/// then members), Go to File, Go to Symbol (the language servers' workspace symbols), Go to Line:Column, File Structure
/// (the file's document symbols), Recent Files, and Back and Forward over the places a navigation or a switch of file
/// left. Each popup stands over the window, one at a time, the keys in its query; Escape or a click outside it gives it
/// up and gives the keys back to the editor.</summary>
public sealed partial class EditorWindow
{
    private const string NavigateGroup = "Navigate";

    /// <summary>How many places Back keeps, and how many files Recent Files lists.</summary>
    private const int PlacesKept = 50;

    /// <summary>The layer over the whole window the search popups stand in.</summary>
    private readonly Panel searchLayer = new() { IsVisible = false };

    /// <summary>What a search popup does once it closes, however it closes; null while none is open.</summary>
    private Action? searchClosed;

    /// <summary>Taps of a modifier alone, of which two make a double tap ("shift , shift": Search Everywhere).</summary>
    private readonly KeymapDispatcher taps = new(AppActions.Keymap());

    /// <summary>The files shown, the latest first, and the files edited, the latest first.</summary>
    private readonly List<string> recentFiles = [];
    private readonly List<string> editedFiles = [];

    /// <summary>Where the caret was left in each file shown before, which a file opened again from a list goes back to.</summary>
    private readonly Dictionary<string, int> leftCarets = new(PathNames.Comparer);

    /// <summary>The actions run lately, the latest first, which Find Action lists before the rest.</summary>
    private readonly List<string> recentActions = [];

    /// <summary>The places Back and Forward go to.</summary>
    private readonly List<(string Path, int Offset)> placesBack = [];
    private readonly List<(string Path, int Offset)> placesForward = [];

    /// <summary>Whether Back or Forward is showing a file, which keeps the place it leaves itself.</summary>
    private bool steppingPlaces;

    /// <summary>The search popup open over the window, if any.</summary>
    public Control? SearchShown => searchLayer.IsVisible ? searchLayer.Children.FirstOrDefault() : null;

    /// <summary>The files Recent Files lists, the latest first: for tests.</summary>
    public IReadOnlyList<string> RecentFiles => recentFiles;

    private void AddNavigation()
    {
        AddNavigate(AppActions.Back, "Back", _ => StepPlaces(-1), _ => placesBack.Count == 0 ? "No place to go back to" : null);
        AddNavigate(AppActions.Forward, "Forward", _ => StepPlaces(1), _ => placesForward.Count == 0 ? "No place to go forward to" : null);
        AddNavigate(AppActions.GoToEverything, "Search Everywhere", _ => ShowSearchEverywhere(), NeedsFolder, ["Go to Everything"]);
        AddNavigate(AppActions.GoToFile, "Go to File…", _ => IdeErrors.Observe(ShowGoToFileAsync()), NeedsFolder);
        AddNavigate(AppActions.GoToSymbol, "Go to Symbol…", _ => ShowGoToSymbol(), NeedsFolder, ["Go to Class"]);
        AddNavigate(AppActions.GoToLine, "Go to Line:Column…", _ => ShowGoToLine(), _ => SourcePath is null ? "No file is open" : null);
        AddNavigate(AppActions.FileStructure, "File Structure", _ => ShowFileStructure(), WhyNoStructure);
        AddNavigate(AppActions.RecentFiles, "Recent Files", _ => ShowRecentFiles(), _ => null);
        AddNavigate(AppActions.RecentChangedFiles, "Recently Changed Files", _ => ShowRecentFiles(editedOnly: true), _ => null);
        Actions.Registry.Running += (_, run) =>
        {
            if (run.Action.Id != AppActions.FindAction)
            {
                Keep(recentActions, run.Action.Id, 10);
            }
        };

        searchLayer.Children.Clear();
        AddHandler(PointerPressedEvent, DismissSearchOutside, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, HearTaps, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, e) => taps.Release(e), RoutingStrategies.Tunnel, handledEventsToo: true);

        void AddNavigate(string id, string title, Action<ActionContext> run, Func<ActionContext, string?> whyNot, IReadOnlyList<string>? words = null) =>
            Actions.Registry.Add(new IdeAction(id, title, NavigateGroup, run) { WhyNot = whyNot, Words = words ?? [] });

        string? NeedsFolder(ActionContext _) => Session is null ? Refusals.NoFolder : null;
    }

    // The popups' host.

    /// <summary>A popup over the window in place of any open, the keys in it, and what it answers handed on once it has
    /// closed (null when given up: Escape or a click outside).</summary>
    private void ShowSearch<T>(Control popup, IAnswers<T> answers, Action<T> done)
    {
        CloseSearch(focusEditor: false);
        popup.HorizontalAlignment = HorizontalAlignment.Center;
        popup.VerticalAlignment = VerticalAlignment.Top;
        popup.Margin = new Thickness(16, 64, 16, 16);
        popup.MaxWidth = Math.Max(240, Bounds.Width - 32);
        searchLayer.Children.Clear();
        searchLayer.Children.Add(popup);
        searchLayer.IsVisible = true;
        T? answer = default;
        bool answered = false;
        searchClosed = () =>
        {
            if (answered)
            {
                done(answer!);
            }
        };
        answers.Done += (_, given) =>
        {
            if (!ReferenceEquals(SearchShown, popup))
            {
                return;
            }
            (answer, answered) = (given, true);
            CloseSearch(focusEditor: true);
        };
    }

    /// <summary>The popup open closed, its answer handed on, the keys back with the editor where it shows.</summary>
    private void CloseSearch(bool focusEditor)
    {
        if (!searchLayer.IsVisible)
        {
            return;
        }
        // Kept until the next is shown, hidden, so what held it reads as gone.
        searchLayer.IsVisible = false;
        Action? closed = searchClosed;
        searchClosed = null;
        if (focusEditor && Editor.IsVisible)
        {
            Editor.Focus();
        }
        closed?.Invoke();
    }

    /// <summary>A press outside the open popup gives it up, as a light dismiss does.</summary>
    private void DismissSearchOutside(object? sender, PointerPressedEventArgs e)
    {
        if (SearchShown is { } shown && (e.Source as Visual)?.FindAncestorOfType<Control>(includeSelf: true) is { } at
            && !ReferenceEquals(at, shown) && !shown.IsVisualAncestorOf(at))
        {
            CloseSearch(focusEditor: true);
        }
    }

    /// <summary>A modifier pressed alone twice in quick succession runs what the keymap gives its double tap.</summary>
    private void HearTaps(object? sender, KeyEventArgs e)
    {
        if (FirstStart is not null)
        {
            return;
        }
        taps.Keymap = keys;
        if (e.Key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
        {
            taps.Heard(e);
            return;
        }
        if (taps.Press(e, out _, out _) is { } action && Actions.Registry[action] is not null)
        {
            e.Handled = true;
            RunFromMenu(action);
        }
    }

    // Find Action.

    /// <summary>Find Action: every action by name, the recently run first, greyed where it cannot run where the keys were.</summary>
    public ActionPopup ShowFindAction(string? query = null)
    {
        ActionContext context = Actions.Registry.Context();
        ActionPopup popup = new(Actions.Registry, context, recentActions, query);
        ShowSearch(popup, popup, chosen =>
        {
            if (chosen is not null)
            {
                RunFromMenu(chosen.Id, context);
            }
        });
        return popup;
    }

    // Go to File and Search Everywhere.

    /// <summary>The folder's files, read now off the window's thread.</summary>
    private Task<FileIndex> IndexAsync(string root) => BackgroundWork.Run(() => FileIndex.Of(root));

    /// <summary>The recent files but the one shown.</summary>
    private List<string> RecentButShown() => [.. recentFiles.Where(p => SourcePath is null || !PathNames.Same(p, SourcePath)).Where(File.Exists)];

    /// <summary>Go to File: the folder's files by name, the recent ones before anything is typed.</summary>
    public async Task<GoToFilePopup?> ShowGoToFileAsync()
    {
        if (Session is not { } session)
        {
            return null;
        }
        string? word = SelectedWord();
        FileIndex index = await IndexAsync(session.Root);
        if (Session != session)
        {
            return null;
        }
        GoToFilePopup popup = new(index, RecentButShown(), word);
        ShowSearch(popup, popup, hit =>
        {
            if (hit is not null)
            {
                GoToFile(hit.FullPath, popup.At);
            }
        });
        return popup;
    }

    /// <summary>Search Everywhere: types first, then files, then members; the recent files before anything is typed.</summary>
    public SymbolPopup? ShowSearchEverywhere()
    {
        if (Session is not { } session)
        {
            return null;
        }
        Task<FileIndex> index = IndexAsync(session.Root);
        return ShowSymbols("Search Everywhere", "A class, member or file", async (query, cancel) =>
        {
            if (query.Trim().Length == 0)
            {
                return [.. RecentButShown().Select(p => FileRow(p, FileIndex.Shown(p, session.Root), null))];
            }
            (string named, TextPosition? at) = GoToFilePopup.PlaceIn(query);
            IReadOnlyList<SymbolRow> symbols = [];
            string? why = null;
            try
            {
                symbols = await WorkspaceSymbolsAsync(named, cancel);
            }
            catch (SearchFailed failed)
            {
                // The files still match without a server.
                why = failed.Message;
            }
            FileIndex files = await index;
            List<SymbolRow> found = [.. symbols.Where(r => r.IsType), .. files.Search(named, 20).Select(p => FileRow(Path.Combine(files.Root, p), p, at)), .. symbols.Where(r => !r.IsType)];
            return found.Count == 0 && why is not null ? throw new SearchFailed($"Nothing matches among the files; {why}") : found;
        });
    }

    private static SymbolRow FileRow(string path, string shown, TextPosition? at) =>
        new(Path.GetFileName(shown), null, Path.GetDirectoryName(shown), SymbolKind.File, path, at ?? new TextPosition(0, 0)) { };

    // Go to Symbol and the language servers' symbols.

    /// <summary>Go to Symbol: the project's classes and members by name or CamelHumps, as the language servers find them.</summary>
    public SymbolPopup? ShowGoToSymbol() =>
        Session is null ? null : ShowSymbols("Go to Symbol", "A class or member, by name or CamelHumps", WorkspaceSymbolsAsync);

    private SymbolPopup ShowSymbols(string title, string placeholder, Func<string, CancellationToken, Task<IReadOnlyList<SymbolRow>>> search)
    {
        SymbolPopup popup = new(title, placeholder, search, SelectedWord());
        ShowSearch(popup, popup, row =>
        {
            if (row is null)
            {
                return;
            }
            if (row.Kind == SymbolKind.File && row.Position == default)
            {
                GoToFile(row.Path, null);
            }
            else
            {
                NavigateTo(row.Path, row.Position);
            }
        });
        return popup;
    }

    /// <summary>The project's classes and members a query names, from every language server running, the project's own
    /// files' first; none for an empty query. Fails, saying why, where no server can answer.</summary>
    private async Task<IReadOnlyList<SymbolRow>> WorkspaceSymbolsAsync(string query, CancellationToken cancel)
    {
        if (query.Trim().Length == 0)
        {
            return [];
        }
        if (Session is not { } session)
        {
            throw new SearchFailed(Refusals.NoFolder);
        }
        if (!session.IsTrusted)
        {
            throw new SearchFailed("Symbols are found once the folder is trusted");
        }
        List<JvmLanguageClient> clients = [.. languageClients.Values.Where(c => !c.HasExited)];
        if (clients.Count == 0)
        {
            throw new SearchFailed(session.Snapshot is null ? "Symbols are found once the project is read" : "No language server is running yet");
        }
        List<SymbolRow> rows = [];
        string? failed = null;
        foreach (JvmLanguageClient client in clients)
        {
            try
            {
                JsonElement answer = await client.RequestAsync("workspace/symbol", new { query = query.Trim() }, cancel);
                rows.AddRange(WorkspaceSymbols(answer, session.Root));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failed = $"the language server could not search: {error.Message}";
            }
        }
        if (rows.Count == 0 && failed is not null)
        {
            throw new SearchFailed(char.ToUpperInvariant(failed[0]) + failed[1..]);
        }
        return [.. rows
            .OrderBy(r => PathNames.IsUnder(r.Path, session.Root) ? 0 : 1)
            .ThenBy(r => FuzzyMatch.Label(query.Trim(), r.Name) is { } score ? -score : int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Take(200)];
    }

    /// <summary>A workspace/symbol answer's symbols in files on disk (a library's class inside a jar has none to open), as
    /// rows: "name", its container, "path:line".</summary>
    public static IEnumerable<SymbolRow> WorkspaceSymbols(JsonElement answer, string? root)
    {
        if (answer.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (JsonElement symbol in answer.EnumerateArray())
        {
            if (!symbol.TryGetProperty("location", out JsonElement location) || !location.TryGetProperty("uri", out JsonElement uri)
                || !Uri.TryCreate(uri.GetString(), UriKind.Absolute, out Uri? file) || !file.IsFile)
            {
                continue;
            }
            TextPosition at = location.TryGetProperty("range", out JsonElement range) ? PositionOf(range.GetProperty("start")) : new TextPosition(0, 0);
            string path = file.LocalPath;
            string? container = symbol.TryGetProperty("containerName", out JsonElement held) && held.GetString() is { Length: > 0 } c ? c : null;
            yield return new SymbolRow(symbol.GetProperty("name").GetString() ?? "", container, $"{FileIndex.Shown(path, root)}:{at.Line + 1}",
                KindOf(symbol), path, at);
        }
    }

    private static TextPosition PositionOf(JsonElement position) =>
        new(position.GetProperty("line").GetInt32(), position.GetProperty("character").GetInt32());

    private static SymbolKind KindOf(JsonElement symbol) =>
        symbol.TryGetProperty("kind", out JsonElement kind) && kind.TryGetInt32(out int number) && Enum.IsDefined(typeof(SymbolKind), number) ? (SymbolKind)number : SymbolKind.Object;

    // File Structure.

    private string? WhyNoStructure(ActionContext _) =>
        SourcePath is not { } path ? "No file is open"
        : Language(path) is null ? $"{Path.GetFileName(path)} has no outline: only Java and Kotlin files have one"
        : Session is not { IsTrusted: true, Snapshot: not null } ? "The outline is read once the folder is trusted and its project read"
        : null;

    /// <summary>File Structure: the open file's classes and members, indented, filtered as typed with the containers of
    /// what matches kept, the caret's innermost member chosen as it opens.</summary>
    public SymbolPopup? ShowFileStructure()
    {
        if (SourcePath is not { } path || WhyNoStructure(Actions.Registry.Context()) is { } why)
        {
            StatusText = WhyNoStructure(Actions.Registry.Context());
            return null;
        }
        IReadOnlyList<SymbolRow>? outline = null;
        int caretLine = Editor.CaretPosition.Line;
        SymbolPopup popup = new($"File Structure: {Path.GetFileName(path)}", "Filter by name, CamelHumps too", async (query, cancel) =>
        {
            if (outline is null)
            {
                try
                {
                    JvmLanguageClient client = await LanguageClientAsync(path, cancel);
                    JsonElement answer = await client.RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri = new Uri(path).AbsoluteUri } }, cancel);
                    outline = DocumentSymbols(answer, path);
                }
                catch (Exception error) when (error is not (OperationCanceledException or SearchFailed))
                {
                    throw new SearchFailed($"The outline could not be read: {error.Message}");
                }
            }
            return FilterOutline(outline, query.Trim());
        }, initial: rows => rows.Select((row, i) => (row, i)).LastOrDefault(r => r.row.Spans(caretLine)).i);
        ShowSearch(popup, popup, row =>
        {
            if (row is not null)
            {
                NavigateTo(row.Path, row.Position);
            }
        });
        return popup;
    }

    /// <summary>A documentSymbol answer as rows in the file's order, each member under its container: the hierarchical
    /// answer's children, or the flat answer's containers.</summary>
    public static IReadOnlyList<SymbolRow> DocumentSymbols(JsonElement answer, string path)
    {
        List<SymbolRow> rows = [];
        if (answer.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }
        foreach (JsonElement symbol in answer.EnumerateArray())
        {
            if (symbol.TryGetProperty("location", out JsonElement location))
            {
                // Flat: each says what it is in, by name.
                TextPosition at = PositionOf(location.GetProperty("range").GetProperty("start"));
                int end = location.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32();
                string? container = symbol.TryGetProperty("containerName", out JsonElement held) && held.GetString() is { Length: > 0 } c ? c : null;
                int depth = container is null ? 0 : rows.LastOrDefault(r => r.Name == container) is { } owner ? owner.Depth + 1 : 1;
                rows.Add(new SymbolRow(symbol.GetProperty("name").GetString() ?? "", null, $"{at.Line + 1}", KindOf(symbol), path, at, depth, end));
            }
            else
            {
                Add(symbol, 0);
            }
        }
        return [.. rows.OrderBy(r => r.Position.Line).ThenBy(r => r.Depth)];

        void Add(JsonElement symbol, int depth)
        {
            JsonElement range = symbol.GetProperty("range");
            TextPosition at = PositionOf(symbol.TryGetProperty("selectionRange", out JsonElement named) ? named.GetProperty("start") : range.GetProperty("start"));
            string? detail = symbol.TryGetProperty("detail", out JsonElement d) && d.GetString() is { Length: > 0 } text ? text : null;
            rows.Add(new SymbolRow(symbol.GetProperty("name").GetString() ?? "", detail, $"{at.Line + 1}", KindOf(symbol), path, at, depth,
                range.GetProperty("end").GetProperty("line").GetInt32()));
            if (symbol.TryGetProperty("children", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    Add(child, depth + 1);
                }
            }
        }
    }

    /// <summary>The rows whose names a filter matches, each with the containers above it, so it reads in its place.</summary>
    public static IReadOnlyList<SymbolRow> FilterOutline(IReadOnlyList<SymbolRow> outline, string query)
    {
        if (query.Length == 0)
        {
            return outline;
        }
        List<SymbolRow> shown = [];
        List<SymbolRow> path = [];
        int pathShown = 0;
        foreach (SymbolRow row in outline)
        {
            while (path.Count > 0 && path[^1].Depth >= row.Depth)
            {
                path.RemoveAt(path.Count - 1);
            }
            pathShown = Math.Min(pathShown, path.Count);
            bool matches = row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || FuzzyMatch.Score(query, row.Name) is not null;
            if (matches)
            {
                shown.AddRange(path.Skip(pathShown));
                shown.Add(row);
            }
            path.Add(row);
            if (matches)
            {
                pathShown = path.Count;
            }
        }
        return shown;
    }

    // Go to Line and Recent Files.

    /// <summary>Go to Line:Column in the file shown.</summary>
    public GoToLinePopup? ShowGoToLine()
    {
        if (SourcePath is null)
        {
            return null;
        }
        GoToLinePopup popup = new(Editor.CaretPosition, Editor.Document.Snapshot.LineCount);
        ShowSearch(popup, popup, position =>
        {
            if (position is { } at && SourcePath is { } path)
            {
                NavigateTo(path, at);
            }
        });
        return popup;
    }

    /// <summary>Recent Files: the files shown last, the latest first, narrowed as a name is typed; a file opens where its
    /// caret was left. Its Show edited only lists the files edited, which Recently Changed Files opens it on.</summary>
    public SymbolPopup ShowRecentFiles(bool editedOnly = false)
    {
        string? root = Session?.Root;
        bool edited = editedOnly;
        SymbolPopup popup = new(editedOnly ? "Recently Changed Files" : "Recent Files", "A file's name", (query, _) =>
        {
            IEnumerable<string> listed = (edited ? editedFiles : recentFiles).Where(p => SourcePath is null || !PathNames.Same(p, SourcePath));
            if (query.Trim().Length > 0)
            {
                listed = listed.Where(p => FuzzyMatch.Score(query.Trim(), Path.GetFileName(p)) is not null);
            }
            return Task.FromResult<IReadOnlyList<SymbolRow>>([.. listed.Select(p => FileRow(p, FileIndex.Shown(p, root), null))]);
        }, option: ("Show edited only", editedOnly));
        popup.OptionChanged += (_, on) => edited = on;
        ShowSearch(popup, popup, row =>
        {
            if (row is not null)
            {
                GoToFile(row.Path, null);
            }
        });
        return popup;
    }

    // Going to places.

    /// <summary>What a search starts from: the editor's selection, if it is one word.</summary>
    private string? SelectedWord()
    {
        if (!Editor.IsVisible || Editor.Selections.Primary is not { IsEmpty: false } primary)
        {
            return null;
        }
        string selected = Editor.Document.Snapshot.GetText(primary.Start, primary.Length);
        return selected.Length is > 0 and < 80 && selected.All(c => char.IsLetterOrDigit(c) || c == '_') ? selected : null;
    }

    /// <summary>A file opened where a list asked (a line after its name), else where its caret was left.</summary>
    private void GoToFile(string path, TextPosition? at)
    {
        if (at is { } place)
        {
            NavigateTo(path, place);
            return;
        }
        if (!TryOpen(path))
        {
            return;
        }
        if (leftCarets.TryGetValue(Path.GetFullPath(path), out int caret))
        {
            Editor.GoTo(Editor.Document.Snapshot.GetPosition(Math.Min(caret, Editor.Document.Snapshot.Length)));
        }
    }

    /// <summary>Goes to a file and position (a symbol, a line), remembering where it came from for Back.</summary>
    public void NavigateTo(string path, TextPosition position)
    {
        (string Path, int Offset)? from = Here();
        if (!TryOpen(path))
        {
            return;
        }
        Editor.GoTo(position);
        // A file left is kept by the switch itself; a jump within the file is kept here.
        if (from is { } left && PathNames.Same(left.Path, SourcePath!)
            && Editor.Document.Snapshot.GetLineFromOffset(Math.Min(left.Offset, Editor.Document.Snapshot.Length)) != Editor.CaretPosition.Line)
        {
            PushPlace(placesBack, left);
            placesForward.Clear();
        }
        Editor.Focus();
    }

    /// <summary>Opens a file to go to, saying in the status line why one cannot be (a binary file, one gone).</summary>
    private bool TryOpen(string path)
    {
        try
        {
            OpenSource(path);
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"{Path.GetFileName(path)} could not be opened: {error.Message}";
            return false;
        }
    }

    /// <summary>The caret's place in the file shown.</summary>
    private (string Path, int Offset)? Here() => SourcePath is { } path && Editor.IsVisible ? (path, Editor.Selections.Primary.Caret) : null;

    /// <summary>A file about to be shown in place of another: the place left goes behind for Back and its caret is kept.</summary>
    private void Leaving(string next)
    {
        if (Here() is not { } here || PathNames.Same(here.Path, next))
        {
            return;
        }
        leftCarets[here.Path] = here.Offset;
        if (!steppingPlaces)
        {
            PushPlace(placesBack, here);
            placesForward.Clear();
        }
    }

    /// <summary>A file shown: first among the recent files, and its edits followed once.</summary>
    private void Visited(string path, TextDocument document, bool opened)
    {
        Keep(recentFiles, path, PlacesKept);
        if (opened)
        {
            document.Changed += (_, _) =>
            {
                if (documents.FirstOrDefault(d => ReferenceEquals(d.Value.Document, document)).Key is { } now && document.IsModified)
                {
                    Keep(editedFiles, now, PlacesKept);
                }
            };
        }
    }

    /// <summary>Back (a step of -1) or Forward (1): the caret goes to the place kept, the one it leaves kept the other way.</summary>
    private void StepPlaces(int step)
    {
        List<(string Path, int Offset)> from = step < 0 ? placesBack : placesForward;
        List<(string Path, int Offset)> to = step < 0 ? placesForward : placesBack;
        while (from.Count > 0)
        {
            (string path, int offset) = from[0];
            from.RemoveAt(0);
            if (!documents.ContainsKey(path) && !File.Exists(path))
            {
                continue;
            }
            if (Here() is { } here)
            {
                PushPlace(to, here);
            }
            steppingPlaces = true;
            try
            {
                if (!TryOpen(path))
                {
                    return;
                }
            }
            finally
            {
                steppingPlaces = false;
            }
            Editor.GoTo(Editor.Document.Snapshot.GetPosition(Math.Min(offset, Editor.Document.Snapshot.Length)));
            Editor.Focus();
            return;
        }
    }

    private static void PushPlace(List<(string Path, int Offset)> places, (string Path, int Offset) place)
    {
        places.Insert(0, place);
        if (places.Count > PlacesKept)
        {
            places.RemoveAt(places.Count - 1);
        }
    }

    /// <summary>Puts an item first in a list of the latest, once, keeping at most <paramref name="most"/>.</summary>
    private static void Keep(List<string> latest, string item, int most)
    {
        latest.RemoveAll(i => string.Equals(i, item, StringComparison.Ordinal));
        latest.Insert(0, item);
        if (latest.Count > most)
        {
            latest.RemoveRange(most, latest.Count - most);
        }
    }

    /// <summary>The folder closed or another opened: its places, files and carets are forgotten.</summary>
    private void ForgetPlaces()
    {
        CloseSearch(focusEditor: false);
        placesBack.Clear();
        placesForward.Clear();
        recentFiles.Clear();
        editedFiles.Clear();
        leftCarets.Clear();
    }
}
