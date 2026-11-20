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
    private const string CompleteAction = "app.jvm.complete", RenameAction = "app.jvm.rename", UndoRenameAction = "app.jvm.undoRename";
    private readonly Dictionary<string, JvmLanguageClient> languageClients = [];
    private readonly Dictionary<string, (int Version, TextSnapshot Text)> sentSources = new(PathNames.Comparer);
    private CancellationTokenSource semanticLifetime = new();
    private IReadOnlyList<SemanticFileChange>? lastRename;
    private KotlinClassBridge? kotlinClasses;
    private Task kotlinRefresh = Task.CompletedTask;
    private bool kotlinRefreshQueued;
    private bool semanticBusy;

    private static string? Language(string path) => Path.GetExtension(path) switch { ".kt" => "kotlin", ".java" => "java", _ => null };
    private bool SemanticAvailable() => !semanticBusy && Session is { IsTrusted: true, Snapshot: not null } session &&
        SourcePath is { } path && Language(path) != null && PathNames.IsUnder(PathNames.Canonical(path), PathNames.Canonical(session.Root));

    private void AddSemanticActions()
    {
        void Add(string id, string title, Func<Task> run) =>
            Actions.Add(new(id, title, SemanticAvailable, () => UnawaitedWork.Observe(RunSemanticAsync(run))));
        Add(CompleteAction, "Complete Code", CompleteAsync);
        Add(RenameAction, "Rename Symbol…", RenameAsync);
        Actions.Add(new(UndoRenameAction, "Undo Workspace Rename", () => !semanticBusy && lastRename != null && Session?.IsTrusted == true,
            () => UnawaitedWork.Observe(RunSemanticAsync(UndoRenameAsync))));
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Space && args.KeyModifiers == KeyModifiers.Control)
                args.Handled = Actions.TryExecute(CompleteAction);
            if (args.Key == Key.F2 && args.KeyModifiers == KeyModifiers.None)
                args.Handled = Actions.TryExecute(RenameAction);
        }, RoutingStrategies.Tunnel);
    }

    private MenuItem SemanticMenu()
    {
        MenuItem menu = new() { Header = "Code", Items = { MenuAction(CompleteAction), MenuAction(RenameAction), MenuAction(UndoRenameAction) } };
        menu.SubmenuOpened += (_, _) => RefreshActions(menu);
        return menu;
    }

    private void ResetSemanticSession()
    {
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

    private async Task<JvmLanguageClient> LanguageClientAsync(string path, CancellationToken token)
    {
        string language = Language(path)!;
        if (!languageClients.TryGetValue(language, out var client) || client.HasExited)
        {
            if (client is not null)
            {
                languageClients.Remove(language);
                await client.DisposeAsync();
                foreach (string key in sentSources.Keys.Where(p => Language(p) == language).ToArray()) sentSources.Remove(key);
            }
            client = await JvmLanguageClient.StartAsync(Session!.Root, language, token);
            if (token.IsCancellationRequested)
            {
                await client.DisposeAsync();
                token.ThrowIfCancellationRequested();
            }
            languageClients.Add(language, client);
            if (language == "java") RefreshKotlinClasses();
        }
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
            token.ThrowIfCancellationRequested();
            sentSources[source] = (version, snapshot);
        }
        return client;
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

    private async Task CompleteAsync()
    {
        var token = semanticLifetime.Token;
        string path = SourcePath!;
        var snapshot = Editor.Document.Snapshot;
        var caret = Editor.CaretPosition;
        var client = await LanguageClientAsync(path, token);
        var result = await client.RequestAsync("textDocument/completion", new { textDocument = DocumentId(path), position = Point(caret) }, token);
        EnsureCurrent();
        JsonElement items = result.ValueKind == JsonValueKind.Object ? result.GetProperty("items") : result;
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            throw new InvalidOperationException("No completions returned at this position. The project may still be indexing.");
        var choices = items.EnumerateArray().Where(item => !item.TryGetProperty("insertTextFormat", out var format) || format.GetInt32() != 2).ToArray();
        ListBox list = new() { ItemsSource = choices.Select(item => item.GetProperty("label").GetString()).ToArray(), SelectedIndex = 0 };
        if (choices.Length == 0) throw new InvalidOperationException("The server returned only snippet completions.");
        var dialog = ChoiceDialog("Complete Code", list, "Insert", out var accept);
        list.DoubleTapped += (_, _) => dialog.Close(true);
        list.KeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; dialog.Close(true); } };
        accept.IsEnabled = choices.Length > 0;
        if (!await dialog.ShowDialog<bool>(this) || list.SelectedIndex < 0) return;
        EnsureCurrent();
        var choice = choices[list.SelectedIndex];
        List<TextEdit> edits = [];
        if (choice.TryGetProperty("textEdit", out var edit))
            edits.Add(JvmSemanticEdits.Edit(snapshot, edit.TryGetProperty("range", out var range) ? range : edit.GetProperty("replace"), edit.GetProperty("newText").GetString()!));
        else
        {
            int end = snapshot.GetOffset(caret), start = end;
            string text = snapshot.GetLineText(caret.Line);
            for (int i = caret.Column - 1; i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$'); i--) start--;
            edits.Add(new(start, end - start, choice.TryGetProperty("insertText", out var insert) ? insert.GetString()! : choice.GetProperty("label").GetString()!));
        }
        if (choice.TryGetProperty("additionalTextEdits", out var extra))
            edits.AddRange(extra.EnumerateArray().Select(e => JvmSemanticEdits.Edit(snapshot, e.GetProperty("range"), e.GetProperty("newText").GetString()!)));
        var chosen = edits[0];
        int offset = chosen.Start + chosen.Text.Length + edits.Skip(1).Where(e => e.Start < chosen.Start).Sum(e => e.Text.Length - e.Length);
        Editor.Document.Edit(edits, SelectionSet.Single(snapshot.GetOffset(caret)), SelectionSet.Single(offset));
        Editor.GoTo(Editor.Document.Snapshot.GetPosition(offset));

        void EnsureCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (SourcePath != path || !ReferenceEquals(snapshot, Editor.Document.Snapshot) || Editor.CaretPosition != caret)
                throw new InvalidOperationException("The completion position changed. Request completion again.");
        }
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
            documents[change.Path] = (doc, change.Before with { Text = text });
        }
    }
}

