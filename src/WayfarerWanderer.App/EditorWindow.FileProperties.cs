// From Passenger 8a1df7e4 src/Passenger.App/MainWindow.FileProperties.cs, MIT (notices/Passenger/LICENSE): over Wayfarer's
// one editor, its file the one shown.
using Avalonia.Controls;
using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Workspace;
using A = WayfarerWanderer.App.Shell.AppActions;

namespace WayfarerWanderer.App;

/// <summary>The line separator, encoding and indentation: the status bar's widgets, and File › File Encoding, File
/// Indentation and Line Separators. A line separator chosen converts every break and writes the file. An encoding chosen is
/// taken at once when nothing changes; otherwise a question, Reload (the file's bytes read again in it) or Convert (the
/// text written in it), warning before either loses something. Each is one undoable step, its undo and redo reaching the
/// file as it did. An indentation chosen is how the file indents from then on, its text left as it is; a conversion writes
/// every line's indentation in tabs or spaces as one undoable step.</summary>
public sealed partial class EditorWindow
{
    private const string FileGroup = "File";

    private void AddFileProperties()
    {
        Actions.Registry.Add(new IdeAction(A.FileEncoding, "File Encoding", FileGroup, _ => ShowEncodingMenu()) { WhyNot = Unchangeable });
        Actions.Registry.Add(new IdeAction(A.FileIndentation, "File Indentation", FileGroup, _ => ShowIndentMenu())
        {
            WhyNot = _ => ShownFile is null ? Refusals.NoFile : null,
            Words = ["Indent Using Tabs", "Indent Using Spaces", "Tab Size"],
        });
        foreach ((string id, bool tabs) in (ReadOnlySpan<(string, bool)>)[(A.ConvertIndentationToTabs, true), (A.ConvertIndentationToSpaces, false)])
        {
            Actions.Registry.Add(new IdeAction(id, tabs ? "Convert Indentation to Tabs" : "Convert Indentation to Spaces", FileGroup, _ => ConvertIndentation(ShownFile!, tabs))
            {
                WhyNot = _ => ShownFile is not { } file ? Refusals.NoFile : file.WhyNotConverted(tabs),
                Words = [tabs ? "Tabify" : "Untabify"],
            });
        }
        foreach (LineSeparator separator in LineSeparators.All)
        {
            // The file's own is checked, and choosing it again changes nothing.
            Actions.Registry.Add(new IdeAction(LineSeparatorAction(separator), separator.Title(), "Line Separators", _ =>
            {
                if (ShownFile is { } file && file.Separator != separator)
                {
                    ConvertLineSeparators(file, separator);
                }
            })
            {
                WhyNot = Unchangeable,
                IsChecked = _ => ShownFile?.Separator == separator,
            });
        }
    }

    private static string LineSeparatorAction(LineSeparator separator) => separator switch
    {
        LineSeparator.CRLF => A.LineSeparatorsCrlf,
        LineSeparator.CR => A.LineSeparatorsCr,
        _ => A.LineSeparatorsLf,
    };

    /// <summary>Why the file's line separator and encoding cannot change: none is open, or it is read-only.</summary>
    private string? Unchangeable(ActionContext _) =>
        ShownFile is not { } file ? Refusals.NoFile : file.ReadOnlyWhy;

    /// <summary>A menu over a status bar widget, opening upward from its right edge.</summary>
    private static void ShowOver(Control widget, IEnumerable<Control> items)
    {
        MenuFlyout flyout = new() { Placement = PlacementMode.TopEdgeAlignedRight };
        foreach (Control item in items)
        {
            flyout.Items.Add(item);
        }
        flyout.ShowAt(widget);
    }

    /// <summary>The line separator popup: the Line Separators menu, the file's own checked.</summary>
    private void ShowLineSeparatorMenu()
    {
        ActionContext context = Actions.Registry.Context();
        ShowOver(lineSeparatorButton, [.. LineSeparators.All.Select(s => FlyoutEntry(new MenuEntry(LineSeparatorAction(s)), context))]);
    }

    /// <summary>The indentation popup over the status bar's widget: a tab and the widths of spaces offered, then the one the
    /// file has with none chosen, the one in force checked; then the conversions.</summary>
    private void ShowIndentMenu()
    {
        if (ShownFile is not { } document)
        {
            return;
        }
        IndentOptions now = document.Indentation.Options;
        bool chosen = document.ChosenIndent is not null;
        List<Control> items = [.. FileIndent.Offered(now).Select(offered => MenuChoice.Of(FileIndent.NameOf(offered),
            chosen && offered.UseTabs == now.UseTabs && (offered.UseTabs || offered.Size == now.Size), () => ChooseIndent(document, offered)))];
        items.Add(new Separator());
        items.Add(MenuChoice.Of($"Automatic: {document.Unchosen.Shown}", !chosen, () => ChooseIndent(document, null)));
        ActionContext context = Actions.Registry.Context();
        items.Add(new Separator());
        items.Add(FlyoutEntry(new MenuEntry(A.ConvertIndentationToTabs), context));
        items.Add(FlyoutEntry(new MenuEntry(A.ConvertIndentationToSpaces), context));
        ShowOver(indentButton, items);
    }

    /// <summary>Converts every line's indentation to tabs or spaces at the file's indent size, as one undoable step, and says
    /// how many lines it changed and how the file indents now.</summary>
    public void ConvertIndentation(OpenDocument document, bool tabs)
    {
        string kind = tabs ? "tabs" : "spaces";
        (int lines, int inStrings) = document.ConvertIndentation(tabs, Carets(document));
        string said = lines == 0
            ? $"Every line of {document.Name} outside its text blocks already indents with {kind}"
            : $"Converted the indentation of {Plural.Of(lines, "line")} of {document.Name} to {kind}, {(tabs ? "a tab" : Plural.Of(document.Indentation.Options.Size, "space"))} a level";
        said = inStrings == 0 ? said : $"{said}; {Plural.Of(inStrings, "line")} inside text blocks left as {Plural.Agreeing(inStrings, "it is", "they are")}";
        StatusText = said;
        ActionWork.Tell(true, said);
    }

    /// <summary>An indentation chosen for the file on the widget, or none to follow it again.</summary>
    public void ChooseIndent(OpenDocument document, IndentOptions? options)
    {
        document.ChosenIndent = options;
        StatusText = $"{document.Name} indents with {document.Indentation.Name} now";
    }

    /// <summary>The encoding popup over the status bar's widget: the encodings it lists first, then More, the file's own
    /// checked. Choosing one asks, when it changes anything, whether to reload the file in it or convert the file to it.</summary>
    private void ShowEncodingMenu()
    {
        if (ShownFile is not { } document)
        {
            return;
        }
        string current = document.EncodingName;
        MenuItem Item(FileEncoding encoding) => MenuChoice.Of(encoding.Name, encoding.Name == current,
            () => IdeErrors.Observe(ChooseEncodingAsync(document, encoding)));
        ShowOver(encodingButton, [.. FileEncoding.First.Select(Item), new Separator(), Shell.ToolWindows.ListMenus.Sub("More", [.. FileEncoding.More.Select(Item)])]);
    }

    /// <summary>An encoding chosen: taken at once when the bytes read as the same text and the text writes as the same bytes,
    /// or when only a UTF-8 BOM comes or goes; otherwise Reload or Convert, as the person answers, each warned against first
    /// when it would lose something.</summary>
    public async Task ChooseEncodingAsync(OpenDocument document, FileEncoding to)
    {
        if (document.ReadOnlyWhy is { } readOnly)
        {
            StatusText = readOnly;
            return;
        }
        string from = document.EncodingName;
        EncodingChange change = document.Weigh(to);
        try
        {
            if (EncodingChange.OnlyBom(document.Encoding, to))
            {
                document.ConvertTo(to, Carets(document));
                StatusText = $"{document.Name} was converted to {to.Name}";
                return;
            }
            if (change is { Reload: Safety.Same, Convert: Safety.Same })
            {
                document.TakeEncoding(to, Carets(document));
                StatusText = $"{document.Name}'s encoding is {to.Name} now: its text and its bytes are the same in it";
                return;
            }
            string? answer = await AskEncoding(document, to);
            if (answer == "Reload")
            {
                if (change.OtherBom)
                {
                    StatusText = $"File '{document.Name}' can't be reloaded in the '{to.Name}' encoding: it starts with the byte-order mark of another";
                    return;
                }
                if (change.Reload == Safety.Unsafe && !await new ConfirmDialog($"Incompatible Encoding: {to.Name}",
                    $"File '{document.Name}' most likely isn't stored in the '{to.Name}' encoding.\n\nCurrent encoding: '{from}'", "Reload Anyway").ShowDialog<bool>(this))
                {
                    return;
                }
                document.ReloadIn(to, Carets(document));
                StatusText = $"{document.Name} was reloaded in {to.Name}";
            }
            else if (answer == "Convert")
            {
                if (change.Convert == Safety.Unsafe && !await new ConfirmDialog($"Incompatible Encoding: {to.Name}",
                    $"Please do not convert to '{to.Name}'.\n\nEncoding '{to.Name}' does not support some characters from the text, '{change.Unheld}' the first of them: converting writes each as '?'.",
                    "Convert Anyway").ShowDialog<bool>(this))
                {
                    return;
                }
                document.ConvertTo(to, Carets(document));
                StatusText = $"{document.Name} was converted to {to.Name}";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Nothing changed: the file could not be read again, or written.
            StatusText = $"{document.Name} is still {from}: {error.Message}";
        }
    }

    /// <summary>Reload, Convert or Cancel for an encoding that would change something: the dialog's answer, or what a test
    /// answers in its place.</summary>
    private Task<string?> AskEncoding(OpenDocument document, FileEncoding to) =>
        EncodingAnswer is { } answer ? Task.FromResult(answer(document, to))
        : new ChoiceDialog($"{document.Name}: Reload or Convert to {to.Name}",
            $"The encoding you've chosen ('{to.Name}') may change the contents of '{document.Name}'. Do you want to\n"
            + $"1. Reload the file from disk in the new encoding '{to.Name}' and overwrite editor contents or\n"
            + "2. Convert the text and overwrite file in the new encoding?",
            ["Reload", "Convert"], null).ShowDialog<string?>(this);

    /// <summary>What answers Reload or Convert in place of the dialog: for tests.</summary>
    public Func<OpenDocument, FileEncoding, string?>? EncodingAnswer { get; set; }

    /// <summary>The carets an undoable change of the file keeps: the editor's while it shows the file.</summary>
    private SelectionSet Carets(OpenDocument document) =>
        ReferenceEquals(Editor.Document, document.Text) ? Editor.Selections : SelectionSet.Single(0);

    /// <summary>Converts the line separators: the file saved first, then every break made the one chosen and the file
    /// written so.</summary>
    public void ConvertLineSeparators(OpenDocument document, LineSeparator to)
    {
        LineSeparator from = document.Separator;
        if (document.IsModified && SourcePath is { } shown && PathNames.Same(shown, document.Path))
        {
            SaveSource();
        }
        try
        {
            document.ConvertLineSeparators(to, Carets(document));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText = $"{document.Name} could not be saved: {error.Message}";
            return;
        }
        StatusText = $"Changed Line Separators from {from} to {to}";
    }
}
