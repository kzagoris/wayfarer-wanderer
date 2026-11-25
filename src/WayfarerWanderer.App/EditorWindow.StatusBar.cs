using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Workspace;
using A = WayfarerWanderer.App.Shell.AppActions;
using LanguageOf = WayfarerWanderer.App.Shell.Languages.Language;

namespace WayfarerWanderer.App;

/// <summary>The status bar, as Passenger's and IntelliJ IDEA's: at the left the file's path and the type and member the
/// caret is in (<see cref="ShowBreadcrumbs"/>), then what was said last beside a turning ring while work is under way; at
/// the right the branch, then the shown file's widgets: the caret's line and column (a click goes to a line), the line
/// separator, the encoding and the indentation (each a click offers the others), the language with its server's state,
/// and a lock while the file is read-only (a click offers to make it writable). File › File Encoding, Line Separators and
/// File Indentation are the widgets' menus.</summary>
public sealed partial class EditorWindow
{
    private readonly Button statusMessage = new() { Classes = { StyleClasses.StatusMessage } };
    private readonly WorkingRing statusWorking = new()
    {
        Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), IsVisible = false, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Panel breadcrumbHost = new() { VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
    private readonly Button updateButton = StatusWidget();
    private readonly Button caretButton = StatusWidget();
    private readonly Button lineSeparatorButton = StatusWidget();
    private readonly Button encodingButton = StatusWidget();
    private readonly Button indentButton = StatusWidget();

    private TextBlock caretText => (TextBlock)caretButton.Content!;
    private TextBlock lineEndingText => (TextBlock)lineSeparatorButton.Content!;
    private TextBlock encodingText => (TextBlock)encodingButton.Content!;
    private TextBlock indentText => (TextBlock)indentButton.Content!;
    private readonly TextBlock languageText = new() { Classes = { StyleClasses.Status }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button readOnlyButton = new()
    {
        Classes = { StyleClasses.StatusItem }, Focusable = false, IsVisible = false, Margin = new Thickness(-6, 0),
        Content = new PathIcon { Width = 13, Height = 13 },
    };

    /// <summary>What the caret's words say: a move that changes none of it words nothing again.</summary>
    private (TextPosition At, int Carets, int Selected) shownCaret;

    /// <summary>The caret's place as the status bar words it ("12:5", "12:5 (8 chars)", "3 carets"); empty with no file.</summary>
    public string CaretShown => caretText.Text ?? string.Empty;

    /// <summary>The line separator as the status bar shows it ("CRLF"); empty with no file.</summary>
    public string LineSeparatorShown => lineEndingText.Text ?? string.Empty;

    /// <summary>The encoding as the status bar shows it ("UTF-8 BOM"); empty with no file.</summary>
    public string EncodingShown => encodingText.Text ?? string.Empty;

    /// <summary>The indentation as the status bar shows it ("4 spaces, from .editorconfig"); empty with no file.</summary>
    public string IndentShown => indentText.Text ?? string.Empty;

    /// <summary>The language as the status bar shows it, with its server's state ("Java · Importing…").</summary>
    public string LanguageShown => languageText.Text ?? string.Empty;

    /// <summary>Whether the status bar shows the read-only lock.</summary>
    public bool ReadOnlyShown => readOnlyButton.IsVisible;

    /// <summary>The file the editor shows, with how it is stored; null with none.</summary>
    public OpenDocument? ShownDocument => ShownFile;

    /// <summary>Whether the status bar's ring turns: work is under way.</summary>
    public bool WorkingShown => statusWorking.IsVisible;

    /// <summary>The status bar's widget: a frame under the pointer around words, reaching into the gap beside it so its words
    /// stand where the other items' do.</summary>
    private static Button StatusWidget() => new()
    {
        Classes = { StyleClasses.StatusItem }, Margin = new Thickness(-6, 0), Focusable = false, IsVisible = false,
        Content = new TextBlock { Classes = { StyleClasses.Status } },
    };

    /// <summary>The bar along the window's foot.</summary>
    private Border StatusBar()
    {
        ((PathIcon)readOnlyButton.Content!).Bind(PathIcon.DataProperty, readOnlyButton.GetResourceObservable(IconKeys.Lock));
        statusText.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(statusWorking, Dock.Left);
        statusMessage.Content = new DockPanel { Children = { statusWorking, statusText } };
        // The whole of what was said on hover, which a narrow bar cuts.
        statusText.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty)
            {
                ToolTip.SetTip(statusMessage, string.IsNullOrEmpty(statusText.Text) ? null : statusText.Text);
            }
        };
        StackPanel widgets = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { updateButton, branchButton, caretButton, lineSeparatorButton, encodingButton, indentButton, languageText, readOnlyButton },
        };
        DockPanel.SetDock(widgets, Dock.Right);
        // A gap before the widgets, as between them, so neither a deep path nor what the bar says runs on against them.
        StatusLine line = new() { Margin = new Thickness(0, 0, 18, 0), Children = { breadcrumbHost, statusMessage } };
        Border bar = new()
        {
            Classes = { StyleClasses.StatusBar },
            Child = new DockPanel { Margin = new Thickness(12, 0), Children = { widgets, line } },
        };
        DockPanel.SetDock(bar, Dock.Bottom);
        statusBarFrame = bar;
        AddStatusBar();
        return bar;
    }

    private void AddStatusBar()
    {
        Opened += (_, _) => RefreshUpdateButton();
        updateButton.Click += (_, _) => IdeErrors.Observe(OpenUpdateNoticeAsync());
        caretButton.Click += (_, _) => Actions.TryExecute(A.GoToLine);
        lineSeparatorButton.Click += (_, _) => ShowLineSeparatorMenu();
        encodingButton.Click += (_, _) => Actions.TryExecute(A.FileEncoding);
        indentButton.Click += (_, _) => Actions.TryExecute(A.FileIndentation);
        readOnlyButton.Click += (_, _) => IdeErrors.Observe(MakeWritableAsync());
        Editor.SelectionChanged += (_, _) =>
        {
            ShowCaret();
            ShowBreadcrumbs();
        };
        Editor.AddHandler(CodeEditor.TypingRefusedEvent, (_, _) => StatusText = Editor.ReadOnlyWhy);
        Actions.Registry.Work.Changed += (_, change) =>
        {
            statusWorking.IsVisible = Actions.Registry.Work.Latest is not null;
            if (!change.Ended)
            {
                StatusText = change.Work.Stage;
            }
        };
        Actions.Registry.Work.Told += (_, told) => StatusText = told.Said;
        AddFileProperties();
        AddBreadcrumbs();
    }

    /// <summary>A file read from disk: the bar follows how it is stored, and it opens read-only when its file is.</summary>
    private void FileOpened(OpenDocument document)
    {
        document.Text.IsReadOnly = FileText.IsReadOnly(document.Path);
        document.StorageChanged += (_, _) =>
        {
            if (ShownFile == document)
            {
                FileShown();
            }
        };
        document.WriteFailed += (_, why) => StatusText = why;
        // An .editorconfig saved here: every open file reads what it says again.
        document.Saved += (_, _) =>
        {
            if (Path.GetFileName(document.Path) == EditorConfig.FileName)
            {
                foreach (OpenDocument open in documents.Values.ToList())
                {
                    open.ReadConventions();
                }
            }
        };
    }

    /// <summary>The file shown changed, or how it is stored: every widget worded again, the editor indenting as the file
    /// does, the path followed.</summary>
    private void FileShown()
    {
        OpenDocument? document = Editor.IsVisible ? ShownFile : null;
        lineSeparatorButton.IsVisible = encodingButton.IsVisible = indentButton.IsVisible = document is not null;
        lineEndingText.Text = document?.Separator.ToString() ?? string.Empty;
        encodingText.Text = document?.EncodingName ?? string.Empty;
        ToolTip.SetTip(lineSeparatorButton, document?.SeparatorTip);
        ToolTip.SetTip(encodingButton, document?.EncodingTip);
        if (document is not null)
        {
            FileIndent indent = document.Indentation;
            indentText.Text = indent.Shown;
            ToolTip.SetTip(indentButton, indent.Tip(LanguageOf.ForPath(document.Path).Name));
            Editor.Indent = indent.Options;
            Editor.IsReadOnly = document.IsReadOnly;
            Editor.ReadOnlyWhy = document.IsReadOnly ? $"{document.Name} is read-only: click the lock in the status bar to make it writable" : null;
        }
        else
        {
            indentText.Text = string.Empty;
            Editor.IsReadOnly = false;
            Editor.ReadOnlyWhy = null;
        }
        readOnlyButton.IsVisible = document?.IsReadOnly == true;
        ToolTip.SetTip(readOnlyButton, document is { IsReadOnly: true } ? $"{document.Name} is read-only. Click to make it writable" : null);
        shownCaret = default;
        caretText.Text = string.Empty;
        ShowCaret();
        ShowLanguage();
        BreadcrumbsFollowFile();
    }

    /// <summary>The caret's line and column, the carets there are, or what is selected; nothing with no file shown.</summary>
    private void ShowCaret()
    {
        bool shown = Editor.IsVisible && SourcePath is not null;
        caretButton.IsVisible = shown;
        if (!shown)
        {
            caretText.Text = string.Empty;
            shownCaret = default;
            return;
        }
        TextPosition at = Editor.CaretPosition;
        int carets = Editor.Selections.Count;
        int selected = carets > 1 ? 0 : Editor.Selections.Primary.Length;
        if (shownCaret == (at, carets, selected) && caretText.Text is { Length: > 0 })
        {
            return;
        }
        shownCaret = (at, carets, selected);
        string position = $"{at.Line + 1}:{at.Column + 1}";
        caretText.Text = carets > 1 ? $"{carets} carets" : selected > 0 ? $"{position} ({Plural.Of(selected, "char")})" : position;
        ToolTip.SetTip(caretButton, "Go to Line:Column");
    }

    /// <summary>The shown file's language, with its server's state beside it while it is not ready: starting, importing (in
    /// the server's words on hover), or stopped.</summary>
    private void ShowLanguage()
    {
        if (!Editor.IsVisible || SourcePath is not { } path)
        {
            languageText.Text = string.Empty;
            ToolTip.SetTip(languageText, null);
            return;
        }
        string name = LanguageOf.ForPath(path).Name;
        (string? state, string tip) = Language(path) is { } served ? ServerState(served, name) : (null, name);
        languageText.Text = state is null ? name : $"{name} · {state}";
        ToolTip.SetTip(languageText, tip);
    }

    /// <summary>A language's server as the language widget words it: its state while it is not ready (null once it is), and
    /// the tip.</summary>
    private (string? State, string Tip) ServerState(string language, string name)
    {
        if (startingClients.TryGetValue(language, out Task<Jvm.JvmLanguageClient>? starting) && !starting.IsCompleted)
        {
            return ("Starting…", $"{name}: the language server is starting");
        }
        if (!languageClients.TryGetValue(language, out Jvm.JvmLanguageClient? client))
        {
            return (null, Session is { IsTrusted: false } ? $"{name}: the language server starts once the folder is trusted" : $"{name}: the language server is not running");
        }
        if (client.HasExited)
        {
            return ("Stopped", $"{name}: the language server stopped; the next completion or rename starts it again");
        }
        // The Kotlin server says nothing of its import: it is ready once it answered.
        if (language == "java" && !client.IsReady)
        {
            return ("Importing…", $"{name}: {client.Status ?? "the language server is importing the project"}");
        }
        return (null, $"{name}: the language server is ready");
    }

    /// <summary>A language server started, said something of its state, or stopped: the language widget follows, and the
    /// path asks for the outline it may now have.</summary>
    private void LanguageServerChanged()
    {
        ShowLanguage();
        OutlineWhenUnseen();
    }

    /// <summary>The read-only lock clicked: asked, the file made writable and editable.</summary>
    private async Task MakeWritableAsync()
    {
        if (ShownFile is not { IsReadOnly: true } document)
        {
            return;
        }
        if (!await new ConfirmDialog("Make File Writable", $"{document.Name} is read-only on disk. Make it writable?", "Make Writable").ShowDialog<bool>(this))
        {
            return;
        }
        try
        {
            FileText.MakeWritable(document.Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText = $"{document.Name} could not be made writable: {error.Message}";
            return;
        }
        document.Text.IsReadOnly = FileText.IsReadOnly(document.Path);
        StatusText = document.IsReadOnly ? $"{document.Name} is still read-only" : $"{document.Name} is writable now";
        FileShown();
    }
}
