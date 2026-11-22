using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.App.Shell.Workspace;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

public sealed partial class EditorWindow
{
    private readonly Dictionary<string, OpenDocument> documents = new(PathNames.Comparer);
    private bool closing;
    /// <summary>The file the editor shows, or null; the status bar follows it.</summary>
    public string? SourcePath
    {
        get => sourcePath;
        private set
        {
            sourcePath = value;
            FileShown();
        }
    }

    private string? sourcePath;
    public IReadOnlyCollection<string> OpenSourcePaths =>
        documents.Keys.Where(path => Session is { } session && PathNames.IsUnder(path, session.Root)).ToArray();

    public TextDocument? FindOpenDocument(string absolutePath) =>
        documents.TryGetValue(absolutePath, out var source) ? source.Document : null;

    public void OpenSource(string path)
    {
        path = Path.GetFullPath(path);
        bool opened = !documents.TryGetValue(path, out var source);
        if (opened)
        {
            FileText file = FileText.Load(path) ?? throw new InvalidDataException($"Cannot open binary file '{path}'.");
            Leaving(path);
            documents[path] = source = new OpenDocument(path, new TextDocument(file.Text), file);
            FileOpened(source);
            ShowDocument(path, source.Document);
            source.Decorations = Decorated(source.Document);
            DocumentOpened(source);
        }
        else
        {
            Leaving(path);
            ShowDocument(path, source!.Document);
        }
        Editor.IsVisible = true;
        SourcePath = path;
        ShowBlameOf(source);
        Breakpoints.Select(path);
        Title = $"Wayfarer Wanderer - {path}";
        Visited(path, source.Document, opened);
    }

    private void SaveSource()
    {
        if (SourcePath is not { } path) return;
        var (document, file) = documents[path];
        var saved = FileText.SaveOver(path, file, document.Snapshot.ToString());
        document.MarkSaved();
        documents[path].OnSaved(saved.Written);
        if (Language(path) == "kotlin") RefreshKotlinClasses();
    }

    private async Task PickWorkspaceAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open Folder" });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } root)
            await OpenWorkspaceAsync(root);
    }

    private async Task PickSourceAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Source",
            SuggestedStartLocation = Session is null ? null : await StorageProvider.TryGetFolderFromPathAsync(Session.Root),
            FileTypeFilter = [new FilePickerFileType("JVM source") { Patterns = ["*.java", "*.kt", "*.kts"] }, FilePickerFileTypes.All]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            OpenSource(path);
    }

    private void AddDebugAndFileActions()
    {
        void AddStep(string id, string title, Func<JvmInteractiveDebugger, Task> command) =>
            Actions.Add(new EditorAction(id, title, () => Session?.IsPaused == true,
                () => UnawaitedWork.Observe(Session!.ResumeAsync(command))));

        AddStep(EditorActions.Continue, "Continue", debug => debug.ContinueAsync());
        AddStep(EditorActions.StepInto, "Step Into", debug => debug.StepIntoAsync());
        AddStep(EditorActions.StepOver, "Step Over", debug => debug.StepOverAsync());
        AddStep(EditorActions.StepOut, "Step Out", debug => debug.StepOutAsync());
        AddDebugValueActions();
        Actions.Add(new EditorAction(EditorActions.OpenWorkspace, "Open Folder…", () => true,
            () => UnawaitedWork.Observe(PickWorkspaceAsync())));
        Actions.Add(new EditorAction(EditorActions.OpenSource, "Open Source…", () => true,
            () => UnawaitedWork.Observe(PickSourceAsync())));
        Actions.Add(new EditorAction(EditorActions.SaveSource, "Save Source", () => SourcePath != null, SaveSource));
    }

    private void ToggleCurrentBreakpoint()
    {
        Breakpoints.Toggle(Editor.CaretPosition.Line);
        if (SourcePath is { } path && Session is { } session)
            UnawaitedWork.Observe(session.UpdateBreakpointsAsync(path, Breakpoints.DebugLines()));
    }

    private void OpenFrame(JvmDebugFrame frame)
    {
        if (frame.Path is not { } path || !File.Exists(path) || frame.Line < 1) return;
        OpenSource(path);
        Editor.GoTo(new TextPosition(frame.Line - 1, 0));
    }

    private JvmLaunchConfiguration? CurrentLaunch()
    {
        if (Session?.Snapshot is not { } snapshot) return null;
        var modules = snapshot.Modules.Where(module => module.MainClasses.Count > 0).ToArray();
        var selected = modules.FirstOrDefault(module => SourcePath != null && PathNames.IsUnder(SourcePath, module.Root))
            ?? modules.FirstOrDefault();
        if (selected is null) return null;
        var main = selected.MainClasses.FirstOrDefault(main => SourcePath != null && PathNames.Same(SourcePath, main.SourcePath))
            ?? selected.MainClasses[0];
        return new JvmLaunchConfiguration
        {
            Name = "Current", Module = selected.Id, MainClass = main.ClassName,
            WorkingDirectory = selected.Root
        };
    }

    /// <summary>The configuration Run and Debug use for the current main class: the one saved for it (its JDK and
    /// arguments), else the module's defaults.</summary>
    private async Task<JvmLaunchConfiguration?> CurrentLaunchAsync()
    {
        if (CurrentLaunch() is not { } current || Session is not { } session)
        {
            return null;
        }
        IReadOnlyList<JvmLaunchConfiguration> saved = await session.Launches.ListAsync();
        return saved.FirstOrDefault(item => item.MainClass == current.MainClass && item.Module == current.Module) ?? current;
    }

    private void DebugCurrent()
    {
        ToolDock.ShowBehind(Shell.ToolWindowIds.Debug);
        if (Session is not { } session) return;
        if (CurrentLaunch() is not { } configuration)
        {
            DebugTool.Status = "No main class was found in this workspace.";
            return;
        }
        if (OpenSourcePaths.FirstOrDefault(path => documents[path].Document.IsModified) is { } modified)
        {
            DebugTool.Status = $"Save {Path.GetRelativePath(session.Root, modified)} before debugging.";
            return;
        }
        lastLaunchDebugs = true;
        DebugTool.Console.Reset();
        UnawaitedWork.Observe(DebugAsync(session, configuration));
    }

    private async void CloseSession(object? sender, WindowClosingEventArgs args)
    {
        if (closing) { args.Cancel = true; return; }
        ResetSemanticSession();
        if (Session is not { } session) return;
        args.Cancel = true;
        closing = true;
        try
        {
            await session.DisposeAsync();
        }
        finally
        {
            Session = null;
            Closing -= CloseSession;
            Avalonia.Threading.Dispatcher.UIThread.Post(Close);
        }
    }
}
