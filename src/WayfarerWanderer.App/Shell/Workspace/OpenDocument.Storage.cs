// From Passenger 8a1df7e4 src/Passenger.App/Workspace/OpenDocument.cs, MIT (notices/Passenger/LICENSE): how the file is
// stored and indented, as the status bar's widgets show and change it. Its .editorconfig is read for what it says, but a
// save here keeps how the file was read, so the tips say what it asks rather than what the next save does.
using Passenger.Core;
using Passenger.Text;

namespace WayfarerWanderer.App.Shell.Workspace;

public sealed partial class OpenDocument
{
    private EditorConfig? conventions;
    private IndentOptions? detected;
    private bool detectedRead;
    private IndentOptions? chosenIndent;
    private FileIndent? indentation;

    /// <summary>Raised when how the file is stored or indented changes: its encoding, its line separator, its indentation,
    /// what its .editorconfig says.</summary>
    public event EventHandler? StorageChanged;

    /// <summary>Raised when an undo or redo of a change of how the file is stored could not write it, with why.</summary>
    public event EventHandler<string>? WriteFailed;

    /// <summary>The file's name, as the status bar words it.</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Whether the text cannot be edited: a file marked read-only on disk opens so.</summary>
    public bool IsReadOnly => Text.IsReadOnly;

    /// <summary>Why every change of it is refused while it is read-only (<see cref="IsReadOnly"/>); null while it is not.</summary>
    public string? ReadOnlyWhy => IsReadOnly ? $"{Name} is read-only" : null;

    /// <summary>What the .editorconfig files over the file say of it, as last read, and read again once it moves.</summary>
    public EditorConfig Conventions
    {
        get
        {
            if (conventions is null || conventionsOf != Path)
            {
                conventions = EditorConfig.For(Path);
                conventionsOf = Path;
                indentation = null;
            }
            return conventions;
        }
    }

    private string? conventionsOf;

    /// <summary>Reads the .editorconfig files over the file again (the file moved, one was saved) and follows them.</summary>
    public void ReadConventions()
    {
        conventions = null;
        indentation = null;
        StorageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The encoding as the widget names it: "UTF-8", "UTF-8 BOM", "windows-1252".</summary>
    public string EncodingName => FileEncoding.Of(Encoding, HasBom).Name;

    /// <summary>The line separator widget's tip, with what the .editorconfig says of it.</summary>
    public string SeparatorTip
    {
        get
        {
            string tip = $"Line Separator: {Separator.Escaped()}";
            return Conventions.EndOfLine is not { } asked ? tip
                : asked == Separator ? $"{tip}\nAs the .editorconfig asks: {Conventions.Said("end_of_line")}"
                : $"{tip}\nThe .editorconfig asks for {asked}: {Conventions.Said("end_of_line")}";
        }
    }

    /// <summary>The encoding widget's tip, with what the .editorconfig says of it.</summary>
    public string EncodingTip
    {
        get
        {
            string tip = $"File Encoding: {EncodingName}";
            return Conventions.Charset is not { } asked ? tip
                : asked.Name == EncodingName ? $"{tip}\nAs the .editorconfig asks: {Conventions.Said("charset")}"
                : $"{tip}\nThe .editorconfig asks for {asked.Name}: {Conventions.Said("charset")}";
        }
    }

    /// <summary>How Enter, Tab and a paste indent the file, and why: the indentation chosen for it, else its .editorconfig's,
    /// else what its own lines show, else four spaces.</summary>
    public FileIndent Indentation
    {
        get
        {
            EditorConfig said = Conventions;
            return indentation ??= FileIndent.Choose(said, Detected, FileIndent.Default, chosenIndent);
        }
    }

    /// <summary>The indentation the file has with none chosen.</summary>
    public FileIndent Unchosen => FileIndent.Choose(Conventions, Detected, FileIndent.Default);

    /// <summary>What the file's lines showed as it was first asked, read once: the indentation it was written with.</summary>
    private IndentOptions? Detected
    {
        get
        {
            if (!detectedRead)
            {
                detectedRead = true;
                detected = Passenger.Text.Indentation.Detect(Text.Snapshot.ToString(), FileIndent.Default.TabWidth,
                    markup: Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            }
            return detected;
        }
    }

    /// <summary>The indentation chosen for the file on the indent widget, over every other, until the file closes; null to
    /// follow its .editorconfig, its own lines and the default.</summary>
    public IndentOptions? ChosenIndent
    {
        get => chosenIndent;
        set
        {
            chosenIndent = value;
            indentation = null;
            StorageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Every line's indentation written in tabs, or in spaces, at the file's indent size, alignment within a line
    /// and each line that begins inside a text block left as they are, as one undoable step that makes it the indentation
    /// chosen for the file, the choice before back on Undo. How many lines changed and how many inside text blocks were
    /// left.</summary>
    public (int Changed, int InStrings) ConvertIndentation(bool toTabs, SelectionSet carets)
    {
        if (WhyNotConverted(toTabs) is { } why)
        {
            throw new IOException(why);
        }
        IndentOptions from = Indentation.Options;
        // Tabs as wide as a level, so the lines stand where they stood.
        IndentOptions to = new(from.Size, toTabs, toTabs ? from.Size : from.TabWidth);
        TextSnapshot before = Text.Snapshot;
        (List<TextEdit> edits, int inStrings) = Passenger.Text.Indentation.Convert(before, from, to, TextBlockLines(before));
        if (edits.Count == 0)
        {
            return (0, inStrings);
        }
        IndentOptions? was = chosenIndent;
        int length = before.Length + edits.Sum(e => e.Text.Length - e.Length);
        Text.Perform(edits, carets, TextEditing.MapThrough(carets, edits).Clamp(length), () => ChosenIndent = was, () => ChosenIndent = to);
        ChosenIndent = to;
        return (edits.Count, inStrings);
    }

    /// <summary>Why converting the indentation would be refused: the file is read-only, or tabs in a language that takes only
    /// spaces.</summary>
    public string? WhyNotConverted(bool toTabs) =>
        ReadOnlyWhy is { } readOnly ? readOnly
        : toTabs && SpacesAlone ? $"{Name} indents with spaces only: {System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant()} takes no tabs"
        : null;

    /// <summary>Whether its language reads a tab as an error: YAML.</summary>
    private bool SpacesAlone => Path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);

    /// <summary>The lines (from zero) that begin inside a Java, Kotlin or Groovy text block (<c>"""</c>), whose leading
    /// whitespace is the string's own text; none in a file of another kind.</summary>
    public IReadOnlySet<int>? TextBlockLines(TextSnapshot text)
    {
        string extension = System.IO.Path.GetExtension(Path).ToLowerInvariant();
        if (extension is not (".java" or ".kt" or ".kts" or ".groovy" or ".gradle"))
        {
            return null;
        }
        HashSet<int> inside = [];
        string all = text.ToString();
        bool open = false;
        int line = 0;
        for (int i = 0; i < all.Length; i++)
        {
            char c = all[i];
            if (c == '\n')
            {
                line++;
                if (open)
                {
                    inside.Add(line);
                }
            }
            else if (c == '\\' && open)
            {
                i++;
            }
            else if (c == '"' && i + 2 < all.Length && all[i + 1] == '"' && all[i + 2] == '"')
            {
                open = !open;
                i += 2;
            }
            else if (!open && c == '/' && i + 1 < all.Length && all[i + 1] == '/')
            {
                // A line comment's quotes open nothing.
                while (i + 1 < all.Length && all[i + 1] != '\n')
                {
                    i++;
                }
            }
            else if (!open && c == '"')
            {
                // An ordinary string, which ends on its line.
                for (i++; i < all.Length && all[i] != '"' && all[i] != '\n'; i++)
                {
                    if (all[i] == '\\')
                    {
                        i++;
                    }
                }
                if (i < all.Length && all[i] == '\n')
                {
                    i--;
                }
            }
        }
        return inside;
    }

    /// <summary>Line Separators: every break made <paramref name="to"/>'s and the file written so, as one undoable step whose
    /// undo and redo write it too.</summary>
    public void ConvertLineSeparators(LineSeparator to, SelectionSet carets)
    {
        IReadOnlyList<TextEdit> edits = TextEditing.Saving(Text.Snapshot.ToString(), carets, false, null, to.Held(), Text.LineEnding)?.Edits ?? [];
        Change(edits, carets, Current with { Separator = to }, write: true);
    }

    /// <summary>What another encoding would do to the file, read again or written in it.</summary>
    public EncodingChange Weigh(FileEncoding to)
    {
        byte[]? bytes;
        try
        {
            bytes = System.IO.File.Exists(Path) ? FileText.Read(Path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            bytes = null;
        }
        return EncodingChange.Weigh(bytes, Text.Snapshot.ToString(), Encoding, File.CrBreaks, to);
    }

    /// <summary>Reload: the file's bytes read again in the encoding replace the text, as one undoable step; the file is left
    /// as it is.</summary>
    public void ReloadIn(FileEncoding to, SelectionSet carets)
    {
        FileText read = FileText.Decode(FileText.Read(Path), to.Encoding);
        LineSeparator separator = read.Text.Contains('\n', StringComparison.Ordinal) ? SeparatorOf(read) : Separator;
        // UTF-16 is written with its BOM, the only way it is read back as UTF-16.
        bool bom = read.HasBom || to.CodePage is 1200 or 1201;
        Change(LineDiff.Edits(Text.Snapshot.ToString(), read.Text), carets, Current with { Encoding = to.Encoding, Bom = bom, Separator = separator }, write: false);
    }

    /// <summary>Convert: the text written in the encoding, as one undoable step whose undo and redo write it too. A character
    /// it cannot hold, converted anyway, becomes '?' in the text as on disk.</summary>
    public void ConvertTo(FileEncoding to, SelectionSet carets) =>
        Change(EncodingChange.Lost(to.Encoding, Text.Snapshot.ToString()), carets, Current with { Encoding = to.Encoding, Bom = to.Bom }, write: true);

    /// <summary>An encoding taken as it is, one that reads the file's bytes as the text it has and writes the text as the
    /// bytes it has (an ASCII file), with nothing read or written, as one undoable step.</summary>
    public void TakeEncoding(FileEncoding to, SelectionSet carets) =>
        Change([], carets, Current with { Encoding = to.Encoding, Bom = to.Bom }, write: false);

    /// <summary>The line separator a file read from disk has, by its first break.</summary>
    private static LineSeparator SeparatorOf(FileText file) =>
        file.CrBreaks ? LineSeparator.CR : TextDocument.DetectLineEnding(file.Text) == "\r\n" ? LineSeparator.CRLF : LineSeparator.LF;

    /// <summary>How the file is stored, which an undoable change of it puts back.</summary>
    private sealed record Stored(System.Text.Encoding Encoding, bool Bom, LineSeparator Separator);

    private Stored Current => new(Encoding, HasBom, Separator);

    /// <summary>One undoable step that changes how the file is stored, and its text with it: undone or redone, the file goes
    /// back to how it was or on again, written when the step wrote it, else marked saved as far as the file, read in the
    /// encoding taken, is the text.</summary>
    private void Change(IReadOnlyList<TextEdit> edits, SelectionSet carets, Stored after, bool write)
    {
        if (ReadOnlyWhy is { } readOnly)
        {
            throw new IOException(readOnly);
        }
        Stored before = Current;
        TextEdit[] sorted = [.. edits.OrderBy(e => e.Start)];
        string changed = Text.Snapshot.Apply(sorted).ToString();
        // Written before anything changes, so a write that fails leaves the file and the editor as they were.
        FileText? written = write ? FileText.Save(Path, changed, after.Encoding, after.Bom, after.Separator == LineSeparator.CR, makeFolder: false).Written : null;
        Text.Perform(sorted, carets, TextEditing.MapThrough(carets, sorted).Clamp(changed.Length),
            () => Settle(before, write),
            () => Settle(after, write));
        Restore(after);
        if (written is not null)
        {
            Text.MarkSaved();
            OnSaved(written);
        }
        else
        {
            MarkSavedIfOnDisk();
        }
    }

    /// <summary>The file as an undo or redo leaves it: stored so, written when the step wrote it.</summary>
    private void Settle(Stored stored, bool write)
    {
        Restore(stored);
        if (!write)
        {
            MarkSavedIfOnDisk();
            return;
        }
        try
        {
            FileText written = FileText.Save(Path, Text.Snapshot.ToString(), Encoding, HasBom, File.CrBreaks, makeFolder: false).Written;
            Text.MarkSaved();
            OnSaved(written);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            WriteFailed?.Invoke(this, $"{Name} could not be saved: {error.Message}");
        }
    }

    private void Restore(Stored stored)
    {
        Text.LineEnding = stored.Separator.Held();
        File = File with { Encoding = stored.Encoding, HasBom = stored.Bom, CrBreaks = stored.Separator == LineSeparator.CR };
        StorageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marks the text saved when the file's bytes, read in the encoding taken, are the text as it stands.</summary>
    private void MarkSavedIfOnDisk()
    {
        try
        {
            if (System.IO.File.Exists(Path) && FileText.Decode(FileText.Read(Path), Encoding).Text == Text.Snapshot.ToString())
            {
                Text.MarkSaved();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Left modified: the next save writes it.
        }
    }
}
