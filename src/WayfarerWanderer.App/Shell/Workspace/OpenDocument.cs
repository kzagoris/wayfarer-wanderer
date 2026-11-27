// What Git needs of Passenger 494009d7 src/Passenger.App/Workspace/OpenDocument.cs, MIT (notices/Passenger/LICENSE): a file
// open in the editor, its text, how it was read, its change bars and blame, and taking what is on disk again.
using System.Text;
using Passenger.Editor;
using Passenger.Text;

namespace WayfarerWanderer.App.Shell.Workspace;

/// <summary>A file open in the window: its text, how it was read from disk (its encoding, BOM and breaks), and what Git
/// draws on it, its change bars against the last commit and its blame. A UI-thread object.</summary>
public sealed partial class OpenDocument(string path, TextDocument text, FileText file)
{
    private IReadOnlyList<ChangeMark> changeMarks = [];
    private IReadOnlyList<string?> gutterAnnotations = [];
    private DocumentDecorations? decorations;
    private string filePath = path;

    /// <summary>The file's path, which follows a move.</summary>
    public string Path
    {
        get => filePath;
        set
        {
            if (filePath != value)
            {
                filePath = value;
                PathChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Raised when the file's path changes (a rename or move).</summary>
    public event EventHandler? PathChanged;

    public TextDocument Text { get; } = text;

    /// <summary>The same text, by the name the window's code knows it by.</summary>
    public TextDocument Document => Text;

    /// <summary>The file as last read or written.</summary>
    public FileText File { get; set; } = file;

    public Encoding Encoding => File.Encoding;

    public bool HasBom => File.HasBom;

    public LineSeparator Separator => File.CrBreaks ? LineSeparator.CR : Text.LineEnding == "\r\n" ? LineSeparator.CRLF : LineSeparator.LF;

    /// <summary>Whether it has text for Git to compare: every file the window opens does; a binary one is never opened.</summary>
    public bool HoldsText => true;

    public bool IsModified => Text.IsModified;

    /// <summary>Raised once the file is written.</summary>
    public event EventHandler? Saved;

    /// <summary>The file written as <paramref name="written"/>.</summary>
    public void OnSaved(FileText written)
    {
        File = written;
        Saved?.Invoke(this, EventArgs.Empty);
        StorageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The decorations the editor draws the text with, once it has been shown: the change bars go there.</summary>
    public DocumentDecorations? Decorations
    {
        get => decorations;
        set
        {
            decorations = value;
            value?.SetChangeMarks(changeMarks);
        }
    }

    /// <summary>The change bars: the lines that differ from the last commit.</summary>
    public IReadOnlyList<ChangeMark> ChangeMarks
    {
        get => changeMarks;
        set
        {
            if (!ReferenceEquals(changeMarks, value))
            {
                changeMarks = value;
                decorations?.SetChangeMarks(value);
                ChangeMarksChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Raised when <see cref="ChangeMarks"/> change.</summary>
    public event EventHandler? ChangeMarksChanged;

    /// <summary>Text per line at the gutter's left (Git blame's), empty for none.</summary>
    public IReadOnlyList<string?> GutterAnnotations
    {
        get => gutterAnnotations;
        set
        {
            if (!ReferenceEquals(gutterAnnotations, value))
            {
                gutterAnnotations = value;
                GutterAnnotationsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Raised when <see cref="GutterAnnotations"/> change.</summary>
    public event EventHandler? GutterAnnotationsChanged;

    /// <summary>Takes the file's text from disk again when something else changed it there (a checkout, a rollback), never
    /// over unsaved changes unless asked; whether the text changed.</summary>
    public bool Reload(bool overUnsaved = false)
    {
        if (IsModified && !overUnsaved)
        {
            return false;
        }
        FileText? read;
        try
        {
            read = FileText.Load(Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (read is null)
        {
            return false;
        }
        File = read;
        StorageChanged?.Invoke(this, EventArgs.Empty);
        if (Text.Snapshot.ToString() == read.Text)
        {
            return false;
        }
        Text.SetText(read.Text, SelectionSet.Single(0));
        Text.MarkSaved();
        return true;
    }

    public void Deconstruct(out TextDocument document, out FileText file)
    {
        document = Text;
        file = File;
    }
}
