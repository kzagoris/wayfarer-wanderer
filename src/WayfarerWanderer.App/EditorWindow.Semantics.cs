using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

public sealed partial class EditorWindow
{
    internal const string CompleteAction = "app.jvm.complete", RenameAction = "app.jvm.rename", UndoRenameAction = "app.jvm.undoRename";
    private readonly Dictionary<string, JvmLanguageClient> languageClients = [];
    private readonly Dictionary<string, (int Version, TextSnapshot Text)> sentSources = new(PathNames.Comparer);
    private CancellationTokenSource semanticLifetime = new();
    private IReadOnlyList<SemanticFileChange>? lastRename;
    private KotlinClassBridge? kotlinClasses;
    private Task kotlinRefresh = Task.CompletedTask;
    private bool kotlinRefreshQueued;
    private bool semanticBusy;
    private readonly Dictionary<string, Task<JvmLanguageClient>> startingClients = [];
    private readonly SemaphoreSlim sourceSync = new(1, 1);
    private CompletionPopup? completionPopup;
    private CompletionSession? completing;
    private CancellationTokenSource? completionAsk;

    /// <summary>What the open suggestion list was asked for: the document and where the word began, and the server's
    /// answer, narrowed locally as the word grows.</summary>
    private sealed record CompletionSession(string Path, TextDocument Document, int WordStart, JvmLanguageClient Client,
        IReadOnlyList<CompletionChoice> Choices, bool Incomplete);

    /// <summary>How long typing a letter waits for the next before asking the server, so a word typed quickly asks once.</summary>
    internal static TimeSpan CompletionDelay { get; set; } = TimeSpan.FromMilliseconds(120);

    /// <summary>The suggestion list under the caret: for tests.</summary>
    public CompletionPopup Completion => completionPopup ??= NewCompletionPopup();

    private static string? Language(string path) => Path.GetExtension(path) switch { ".kt" => "kotlin", ".java" => "java", _ => null };
    private bool SemanticAvailable() => !semanticBusy && Session is { IsTrusted: true, Snapshot: not null } session &&
        SourcePath is { } path && Language(path) != null && PathNames.IsUnder(PathNames.Canonical(path), PathNames.Canonical(session.Root));

    private void AddSemanticActions()
    {
        Actions.Add(new(CompleteAction, "Complete Code", SemanticAvailable,
            () => UnawaitedWork.Observe(AskCompletionAsync(trigger: null, explicitly: true, delay: TimeSpan.Zero))));
        // A workspace rename edits files from the model; while it is older than the build files (an import failed or is
        // running) the rename waits. Completion only suggests, so it stays.
        Actions.Add(new(RenameAction, "Rename Symbol…", () => SemanticAvailable() && Session?.IsStale == false,
            () => UnawaitedWork.Observe(RunSemanticAsync(RenameAsync))));
        Actions.Add(new(UndoRenameAction, "Undo Workspace Rename", () => !semanticBusy && lastRename != null && Session?.IsTrusted == true,
            () => UnawaitedWork.Observe(RunSemanticAsync(UndoRenameAsync))));
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (completionPopup?.TakeKey(args.Key, args.KeyModifiers) == true)
            {
                args.Handled = true;
                return;
            }
            // The editor's keys: elsewhere (the Debug window's evaluate box and variables) they are the focused control's.
            if (args.Source is Avalonia.Visual source && source != Editor && !Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(Editor, source))
                return;
            if (args.Key == Key.Space && args.KeyModifiers == KeyModifiers.Control)
                args.Handled = Actions.TryExecute(CompleteAction);
            if (args.Key == Key.F2 && args.KeyModifiers == KeyModifiers.None)
                args.Handled = Actions.TryExecute(RenameAction);
        }, RoutingStrategies.Tunnel);
        Editor.Typed += (_, text) => OnTypedForCompletion(text);
        Editor.SelectionChanged += (_, _) => { if (completing is not null) NarrowCompletion(); };
        Editor.LostFocus += (_, _) => { if (!IsActive) CloseCompletion(); };
        Deactivated += (_, _) => CloseCompletion();
    }

    private void ResetSemanticSession()
    {
        CloseCompletion();
        startingClients.Clear();
        semanticLifetime.Cancel();
        semanticLifetime.Dispose();
        semanticLifetime = new();
        foreach (var server in languageClients.Values) UnawaitedWork.Observe(server.DisposeAsync().AsTask());
        languageClients.Clear();
        sentSources.Clear();
        kotlinClasses?.Dispose();
        kotlinClasses = null;
        kotlinRefreshQueued = false;
        lastRename = null;
    }

    private void ReloadLanguageModels(WorkspaceSnapshot snapshot)
    {
        CancellationToken token = semanticLifetime.Token;
        foreach (JvmLanguageClient client in languageClients.Values.Where(client => !client.HasExited))
            UnawaitedWork.Observe(client.ReloadModelAsync(snapshot, token));
        RefreshKotlinClasses();
        StartLanguageServers();
    }

    /// <summary>Whether a trusted folder's language servers start as soon as its model is read, rather than on the first
    /// completion or rename. Tests turn it off so opening a fixture starts no server.</summary>
    public static bool StartsLanguageServersOnOpen { get; set; } = true;

    /// <summary>Starts the server of each language the project's sources are written in, one after the other, so the
    /// first completion finds it running. A server that cannot start says nothing here; the first ask tries again and
    /// reports why.</summary>
    private void StartLanguageServers()
    {
        if (!StartsLanguageServersOnOpen || Session is not { IsTrusted: true, Snapshot: { } snapshot } session) return;
        CancellationToken lifetime = semanticLifetime.Token;
        UnawaitedWork.Observe(StartAsync());

        async Task StartAsync()
        {
            try
            {
                IReadOnlyList<string> languages = await Task.Run(() => SourceLanguages(snapshot, lifetime), lifetime);
                foreach (string language in languages)
                {
                    if (lifetime.IsCancellationRequested || Session != session || !session.IsTrusted) return;
                    try { await StartedClientAsync(language); }
                    catch (Exception error) when (error is not OperationCanceledException) { /* reported on the first ask */ }
                }
            }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>The languages, of those with a server, that the model's source roots hold files of: Java first, since on
    /// Maven the Kotlin class bridge waits for the Java server.</summary>
    private static IReadOnlyList<string> SourceLanguages(WorkspaceSnapshot snapshot, CancellationToken token)
    {
        EnumerationOptions everywhere = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
        HashSet<string> found = [];
        foreach (string root in snapshot.Modules.SelectMany(m => m.SourceRoots).Select(s => s.Path).Distinct(PathNames.Comparer))
        {
            if (!Directory.Exists(root)) continue;
            foreach (string file in Directory.EnumerateFiles(root, "*", everywhere))
            {
                token.ThrowIfCancellationRequested();
                if (Language(file) is { } language && found.Add(language) && found.Count == 2)
                    return [.. found.Order()];
            }
        }
        return [.. found.Order()];
    }

    private async Task RunSemanticAsync(Func<Task> action)
    {
        semanticBusy = true;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ToolDock.ShowBehind(Shell.ToolWindowIds.Build);
            BuildTool.Append(error.Message);
        }
        finally { semanticBusy = false; }
    }

    /// <summary>The running server for <paramref name="path"/>'s language, started once however many asks wait on it, with
    /// every open source of that language sent to it as it reads now. Sends go one at a time, so versions arrive in order.</summary>
    private async Task<JvmLanguageClient> LanguageClientAsync(string path, CancellationToken token)
    {
        string language = Language(path)!;
        JvmLanguageClient client = await StartedClientAsync(language).WaitAsync(token);
        await sourceSync.WaitAsync(token);
        try
        {
            foreach (string source in OpenSourcePaths.Where(p => Language(p) == language))
            {
                TextDocument document = FindOpenDocument(source)!;
                string uri = new Uri(source).AbsoluteUri;
                var snapshot = document.Snapshot;
                int version = document.Version;
                if (sentSources.TryGetValue(source, out var previous))
                {
                    if (ReferenceEquals(previous.Text, snapshot)) continue;
                    var end = previous.Text.GetPosition(previous.Text.Length);
                    await client.NotifyAsync("textDocument/didChange", new
                    {
                        textDocument = new { uri, version },
                        contentChanges = new[] { new { range = new { start = Point(default), end = Point(end) }, text = snapshot.ToString() } }
                    }, token);
                }
                else
                    await client.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = language, version, text = snapshot.ToString() } }, token);
                sentSources[source] = (version, snapshot);
            }
        }
        finally { sourceSync.Release(); }
        token.ThrowIfCancellationRequested();
        return client;
    }

    /// <summary>The server of a language, started on the first ask and again after it exits. Starting belongs to the
    /// session, not to the ask that began it, so a keystroke's ask given up does not stop the server coming up.</summary>
    private Task<JvmLanguageClient> StartedClientAsync(string language)
    {
        if (languageClients.TryGetValue(language, out var running) && !running.HasExited)
            return Task.FromResult(running);
        if (startingClients.TryGetValue(language, out var starting) && !starting.IsCompleted)
            return starting;
        starting = StartAsync();
        startingClients[language] = starting;
        ShowLanguage();
        return starting;

        async Task<JvmLanguageClient> StartAsync()
        {
            CancellationToken lifetime = semanticLifetime.Token;
            if (languageClients.Remove(language, out var exited))
            {
                await exited.DisposeAsync();
                foreach (string key in sentSources.Keys.Where(p => Language(p) == language).ToArray()) sentSources.Remove(key);
            }
            string message = language == "kotlin" ? "Starting the Kotlin language server…" : "Starting the Java language server…";
            string? was = StatusText;
            StatusText = message;
            try
            {
                JvmLanguageClient client = await JvmLanguageClient.StartAsync(Session!.Root, language, lifetime, Session.Snapshot);
                if (lifetime.IsCancellationRequested)
                {
                    await client.DisposeAsync();
                    lifetime.ThrowIfCancellationRequested();
                }
                languageClients[language] = client;
                client.StatusChanged += () => Shell.UiThread.Post(LanguageServerChanged);
                if (language == "java") RefreshKotlinClasses();
                return client;
            }
            finally
            {
                if (StatusText == message) StatusText = was;
                Shell.UiThread.Post(LanguageServerChanged);
            }
        }
    }

    /// <summary>On Maven, recompiles Kotlin and hands its classes to the Java server, so Java sees Kotlin declarations.
    /// Saves during a refresh coalesce into one more refresh.</summary>
    private void RefreshKotlinClasses()
    {
        if (Session is not { IsTrusted: true, Snapshot: { } snapshot } || !KotlinClassBridge.Applies(snapshot) ||
            !languageClients.TryGetValue("java", out var java) || kotlinRefreshQueued) return;
        kotlinRefreshQueued = true;
        kotlinClasses ??= new();
        KotlinClassBridge bridge = kotlinClasses;
        CancellationToken token = semanticLifetime.Token;
        kotlinRefresh = RunAsync(kotlinRefresh);
        UnawaitedWork.Observe(kotlinRefresh);

        async Task RunAsync(Task previous)
        {
            try { await previous; } catch (Exception) { /* reported by the earlier refresh */ }
            if (token.IsCancellationRequested) return;
            kotlinRefreshQueued = false;
            try { await bridge.RefreshAsync(java, snapshot, cancel: token); }
            catch (OperationCanceledException) { }
            catch (Exception error) when (!token.IsCancellationRequested) { BuildTool.Append(error.Message); }
        }
    }

    private static object Point(TextPosition p) => new { line = p.Line, character = p.Column };
    private static object DocumentId(string path) => new { uri = new Uri(path).AbsoluteUri };

    private CompletionPopup NewCompletionPopup()
    {
        CompletionPopup popup = new(Editor);
        popup.Chosen += choice => UnawaitedWork.Observe(AcceptCompletionAsync(choice));
        return popup;
    }

    /// <summary>Typing opens suggestions: a dot at once, a letter that starts or grows a word after a short pause; with the
    /// list open a letter narrows it (asking again only if the server said its list was partial) and anything else closes it.</summary>
    private void OnTypedForCompletion(string text)
    {
        if (text.Length == 0 || semanticBusy || !SemanticAvailable()) return;
        char last = text[^1];
        if (completing is not null)
        {
            if (CompletionModel.IsWordCharacter(last))
            {
                NarrowCompletion();
                if (completing is { Incomplete: true })
                    UnawaitedWork.Observe(AskCompletionAsync(null, explicitly: false, CompletionDelay));
                return;
            }
            CloseCompletion();
        }
        if (last == '.')
        {
            UnawaitedWork.Observe(AskCompletionAsync(last, explicitly: false, TimeSpan.Zero));
            return;
        }
        if (!CompletionModel.IsWordCharacter(last)) return;
        // A number is not a name to complete.
        var snapshot = Editor.Document.Snapshot;
        int caret = Editor.Selections.Primary.Caret;
        int start = CompletionModel.WordStart(snapshot, caret);
        if (start < caret && !char.IsDigit(snapshot[start]))
            UnawaitedWork.Observe(AskCompletionAsync(null, explicitly: false, CompletionDelay));
    }

    /// <summary>Asks the server for suggestions at the caret and lists them. An ask made by typing gives way to the next
    /// keystroke's and fails quietly; one asked for (Ctrl+Space) says why it found nothing.</summary>
    private async Task AskCompletionAsync(char? trigger, bool explicitly, TimeSpan delay)
    {
        completionAsk?.Cancel();
        using CancellationTokenSource ask = CancellationTokenSource.CreateLinkedTokenSource(semanticLifetime.Token);
        completionAsk = ask;
        CancellationToken token = ask.Token;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            if (SourcePath is not { } path || !SemanticAvailable()) return;
            TextDocument document = Editor.Document;
            var client = await LanguageClientAsync(path, token);
            int caret = Editor.Selections.Primary.Caret;
            var snapshot = document.Snapshot;
            int wordStart = CompletionModel.WordStart(snapshot, caret);
            object context = trigger is { } character ? new { triggerKind = 2, triggerCharacter = character.ToString() } : new { triggerKind = 1 };
            var result = await client.RequestAsync("textDocument/completion",
                new { textDocument = DocumentId(path), position = Point(snapshot.GetPosition(caret)), context }, token);
            token.ThrowIfCancellationRequested();
            // The answer is for the word asked about; typing that grew the same word keeps it, anything else drops it.
            if (SourcePath != path || !ReferenceEquals(Editor.Document, document) ||
                CompletionModel.WordStart(document.Snapshot, Editor.Selections.Primary.Caret) != wordStart)
                return;
            var choices = CompletionModel.Parse(result, out bool incomplete);
            completing = new(path, document, wordStart, client, choices, incomplete);
            NarrowCompletion();
            if (completionPopup is not { IsOpen: true } && explicitly)
                StatusText = choices.Count == 0 ? "No suggestions here. The project may still be indexing." : "No suggestions match.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (explicitly)
        {
            ToolDock.ShowBehind(Shell.ToolWindowIds.Build);
            BuildTool.Append(error.Message);
        }
        catch (Exception) { /* a suggestion asked by typing that fails shows nothing; Ctrl+Space reports it */ }
        finally
        {
            if (completionAsk == ask) completionAsk = null;
        }
    }

    /// <summary>Shows the suggestions that match the word typed so far, or closes the list once the caret has left the word.</summary>
    private void NarrowCompletion()
    {
        if (completing is not { } session) return;
        int caret = Editor.Selections.Primary.Caret;
        var snapshot = session.Document.Snapshot;
        if (SourcePath != session.Path || !ReferenceEquals(Editor.Document, session.Document) || Editor.Selections.Count > 1 ||
            caret < session.WordStart || caret > snapshot.Length || CompletionModel.WordStart(snapshot, caret) != session.WordStart)
        {
            CloseCompletion();
            return;
        }
        var shown = CompletionModel.Narrow(session.Choices, snapshot.GetText(session.WordStart, caret - session.WordStart));
        if (shown.Count == 0)
        {
            // A partial list may still have matches the server did not send; the next keystroke asks again.
            if (!session.Incomplete) CloseCompletion();
            else completionPopup?.Close();
            return;
        }
        Completion.Show(shown, Editor.PointOf(session.WordStart), Editor.LineHeight, snapshot.GetText(session.WordStart, caret - session.WordStart));
    }

    private void CloseCompletion()
    {
        completionAsk?.Cancel();
        completing = null;
        completionPopup?.Close();
    }

    /// <summary>Takes a suggestion into the text: resolved first when the server holds back its extra edits (JDT LS adds a
    /// type's import only then), and applied to the text as it reads now.</summary>
    private async Task AcceptCompletionAsync(CompletionChoice choice)
    {
        if (completing is not { } session) return;
        CloseCompletion();
        JsonElement item = choice.Item;
        if (!item.TryGetProperty("additionalTextEdits", out _) && item.TryGetProperty("data", out _))
        {
            try
            {
                using CancellationTokenSource resolving = CancellationTokenSource.CreateLinkedTokenSource(semanticLifetime.Token);
                resolving.CancelAfter(TimeSpan.FromSeconds(2));
                JsonElement resolved = await session.Client.RequestAsync("completionItem/resolve", item, resolving.Token);
                if (resolved.ValueKind == JsonValueKind.Object) item = resolved.Clone();
            }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or IOException) { /* insert it unresolved */ }
        }
        if (SourcePath != session.Path || !ReferenceEquals(Editor.Document, session.Document)) return;
        var snapshot = session.Document.Snapshot;
        int caret = Editor.Selections.Primary.Caret;
        if (caret < session.WordStart || CompletionModel.WordStart(snapshot, caret) != session.WordStart) return;
        try
        {
            var (edits, after) = CompletionModel.Edits(snapshot, item, session.WordStart, caret);
            session.Document.Edit(edits, SelectionSet.Single(caret), SelectionSet.Single(after));
            Editor.GoTo(session.Document.Snapshot.GetPosition(after));
        }
        catch (InvalidDataException error)
        {
            ToolDock.ShowBehind(Shell.ToolWindowIds.Build);
            BuildTool.Append(error.Message);
        }
        Editor.Focus();
    }

    private static Window ChoiceDialog(string title, Control content, string acceptText, out Button accept)
    {
        Window dialog = new() { Title = title, Width = 580, Height = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Button ok = new() { Content = acceptText }, cancel = new() { Content = "Cancel" };
        accept = ok;
        ok.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ok, cancel } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        dialog.Content = new DockPanel { Margin = new(12), Children = { buttons, content } };
        return dialog;
    }

    private async Task RenameAsync()
    {
        var token = semanticLifetime.Token;
        var snapshot = Session!.Snapshot!;
        var path = SourcePath!;
        var caret = Editor.CaretPosition;
        var versions = OpenSourcePaths.ToDictionary(p => p, p => FindOpenDocument(p)!.Version, PathNames.Comparer);
        TextBox name = new() { PlaceholderText = "New symbol name", VerticalAlignment = VerticalAlignment.Top };
        var prompt = ChoiceDialog("Rename Symbol", name, "Preview", out var preview);
        prompt.Height = 150;
        preview.IsEnabled = false;
        name.TextChanged += (_, _) => preview.IsEnabled = !string.IsNullOrWhiteSpace(name.Text);
        if (!await prompt.ShowDialog<bool>(this) || string.IsNullOrWhiteSpace(name.Text)) return;
        Validate();
        var client = await LanguageClientAsync(path, token);
        var result = await client.RequestAsync("textDocument/rename", new { textDocument = DocumentId(path), position = Point(caret), newName = name.Text }, token);
        Validate();
        var files = JvmSemanticEdits.Documents(result, snapshot.Root, p => sentSources.TryGetValue(p, out var value) ? value.Version : null);
        List<SemanticFileChange> changes = [];
        List<string> lines = [];
        foreach (var (file, edits) in files)
        {
            var open = FindOpenDocument(file);
            byte[] bytes = File.ReadAllBytes(file);
            FileText disk = FileText.Load(file) ?? throw new IOException("Cannot read " + file);
            if (disk.IsBinary || open != null && (open.IsModified || open.Snapshot.ToString() != disk.Text))
                throw new InvalidOperationException("Save or reload " + file + " before renaming.");
            var source = TextSnapshot.From(disk.Text);
            var replacements = edits.EnumerateArray().Select(e => JvmSemanticEdits.Edit(source, e.GetProperty("range"), e.GetProperty("newText").GetString()!)).OrderBy(e => e.Start).ToArray();
            foreach (var change in replacements)
            {
                var location = source.GetPosition(change.Start);
                lines.Add($"{Path.GetRelativePath(snapshot.Root, file)}  {location.Line + 1}:{location.Column + 1}  {source.GetText(change.Start, change.Length)} → {change.Text}");
            }
            changes.Add(new(file, disk, source.Apply(replacements).ToString(), bytes));
        }
        var mixed = snapshot.Modules.SelectMany(m => m.SourceRoots)
            .Where(s => Directory.Exists(s.Path)).SelectMany(s => Directory.EnumerateFiles(s.Path, "*", new EnumerationOptions { RecurseSubdirectories = true }))
            .Select(Language).Where(l => l != null).Distinct().Take(2).Count() == 2;
        if (mixed) lines.Insert(0, "Preview only. Cross-language reference coverage has not been established.");
        var review = ChoiceDialog("Review Rename", new ListBox { ItemsSource = lines }, "Apply", out var apply);
        apply.IsEnabled = !mixed;
        if (!await review.ShowDialog<bool>(this) || mixed) return;
        Validate();
        ApplyRename(changes, false);
        lastRename = changes;

        void Validate()
        {
            token.ThrowIfCancellationRequested();
            WorkspaceTrust.Check(snapshot.Root);
            if (!ReferenceEquals(Session?.Snapshot, snapshot) || SourcePath != path || OpenSourcePaths.Count != versions.Count ||
                versions.Any(kv => FindOpenDocument(kv.Key)?.Version != kv.Value))
                throw new InvalidOperationException("The workspace changed. Request a new preview.");
        }
    }

    private Task UndoRenameAsync()
    {
        ApplyRename(lastRename!, true);
        lastRename = null;
        return Task.CompletedTask;
    }

    private void ApplyRename(IReadOnlyList<SemanticFileChange> changes, bool undo)
    {
        foreach (var change in changes)
        {
            var doc = FindOpenDocument(change.Path);
            if (doc != null && (doc.IsReadOnly || doc.IsModified || doc.Snapshot.ToString() != (undo ? change.After : change.Before.Text)))
                throw new InvalidOperationException("The document changed: " + change.Path);
        }
        SemanticFileTransaction.Write(changes, undo);
        foreach (var change in changes)
        {
            if (FindOpenDocument(change.Path) is not { } doc) continue;
            string text = undo ? change.Before.Text : change.After;
            doc.SetText(text, SelectionSet.Single(0));
            doc.MarkSaved();
            documents[change.Path].File = change.Before with { Text = text };
        }
    }
}

