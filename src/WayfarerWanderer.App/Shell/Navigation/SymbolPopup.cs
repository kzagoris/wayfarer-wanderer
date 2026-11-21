// From Passenger 2044e49b src/Passenger.App/Navigation/SymbolPopup.cs, MIT (notices/Passenger/LICENSE).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell.Explorer;

namespace WayfarerWanderer.App.Shell.Navigation;

/// <summary>The kind of a symbol, as the language server protocol numbers them; <see cref="File"/> (none of the
/// protocol's) marks a file among symbols.</summary>
public enum SymbolKind
{
    File = 0,
    Module = 2,
    Namespace = 3,
    Package = 4,
    Class = 5,
    Method = 6,
    Property = 7,
    Field = 8,
    Constructor = 9,
    Enum = 10,
    Interface = 11,
    Function = 12,
    Variable = 13,
    Constant = 14,
    String = 15,
    Number = 16,
    Boolean = 17,
    Array = 18,
    Object = 19,
    Key = 20,
    Null = 21,
    EnumMember = 22,
    Struct = 23,
    Event = 24,
    Operator = 25,
    TypeParameter = 26,
}

/// <summary>A place a symbol popup offers: a type, a member, a file. <see cref="Depth"/> indents it under its container
/// (File Structure's members under their type).</summary>
public sealed record SymbolRow(string Name, string? Detail, string? Where, SymbolKind Kind, string Path, TextPosition Position, int Depth = 0, int? EndLine = null)
{
    /// <summary>Whether a line (from zero) is inside the declaration: the caret's member is chosen when File Structure
    /// opens.</summary>
    public bool Spans(int line) => Position.Line <= line && line <= (EndLine ?? Position.Line);

    /// <summary>Whether it is a type, which Search Everywhere lists before files.</summary>
    public bool IsType => Kind is SymbolKind.Class or SymbolKind.Enum or SymbolKind.Interface or SymbolKind.Struct;
}

/// <summary>A search a popup could not answer (no language server yet, a server that failed), whose message it shows.</summary>
public sealed class SearchFailed(string message) : Exception(message);

/// <summary>A query over symbols, answered as it is typed (a moment after the last key, the previous answer given up):
/// Search Everywhere, Go to Symbol, File Structure and Recent Files, whose switch (Show edited only) asks again as it
/// turns. Up and Down move, Enter goes, Escape leaves. A search that cannot be answered fails with a
/// <see cref="SearchFailed"/>, whose message the popup shows.</summary>
public sealed class SymbolPopup : SearchPopup<SymbolRow>
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(120);

    private readonly Func<string, CancellationToken, Task<IReadOnlyList<SymbolRow>>> search;
    private readonly Debounce typing;
    private CancellationTokenSource? asking;
    private Task searching = Task.CompletedTask;

    public SymbolPopup(string title, string placeholder, Func<string, CancellationToken, Task<IReadOnlyList<SymbolRow>>> search, string? initialQuery = null,
        (string Label, bool Checked)? option = null)
        : base(title, placeholder, PopupFrame.Wide, initialQuery, option)
    {
        this.search = search;
        typing = new Debounce(Pause, Refresh);
        IdeErrors.Observe(Refresh());
    }

    /// <summary>The popup's switch turned, before it asks again; its search reads what it is now.</summary>
    public event EventHandler<bool>? OptionChanged;

    /// <summary>The answer to the query as it now is is in.</summary>
    public bool IsSettled { get; private set; }

    /// <summary>Which of the first rows to choose (the caret's member), before anything is typed.</summary>
    public Func<IReadOnlyList<SymbolRow>, int>? Initial { get; init; }

    /// <summary>Done once the answer to the query as it now is is in, for a test.</summary>
    public async Task SettledAsync()
    {
        while (!IsSettled)
        {
            if (typing.IsWaiting)
            {
                await Task.Delay(20);
                continue;
            }
            await searching;
            await Task.Yield();
        }
    }

    protected override void QueryChanged()
    {
        // The answer to the query before can no longer land over this one's.
        asking?.Cancel();
        IsSettled = false;
        typing.Restart();
    }

    protected override void OptionTurned()
    {
        OptionChanged?.Invoke(this, IsOptionChecked);
        IdeErrors.Observe(Refresh());
    }

    protected override Control Row() => ReusedRow();

    private Task Refresh() => searching = RefreshAsync();

    private async Task RefreshAsync()
    {
        asking?.Cancel();
        CancellationTokenSource mine = asking = new CancellationTokenSource();
        IsSettled = false;
        BackgroundWork.Begin();
        try
        {
            IReadOnlyList<SymbolRow> found = await search(Query, mine.Token);
            if (mine.IsCancellationRequested)
            {
                return;
            }
            Show(found, "Nothing matches", Initial is { } initial && Query.Length == 0 ? initial(found) : 0);
            IsSettled = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SearchFailed failed)
        {
            if (!mine.IsCancellationRequested)
            {
                Show([], failed.Message);
                IsSettled = true;
            }
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    /// <summary>A symbol's row as every symbol list draws it, made once for a list's container and showing each symbol it
    /// is handed: its badge (a file's icon for a file), its name and type, where it is.</summary>
    public static Control ReusedRow()
    {
        RowIcon icon = new() { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        TextBlock letter = new()
        {
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Border badge = new()
        {
            Width = 16,
            Height = 16,
            Classes = { StyleClasses.KindBadge },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Child = letter,
        };
        TextBlock where = new()
        {
            [DockPanel.DockProperty] = Dock.Right,
            FontSize = 12,
            Margin = new Thickness(12, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260,
            VerticalAlignment = VerticalAlignment.Center,
        };
        TextBlock name = new() { [DockPanel.DockProperty] = Dock.Left, VerticalAlignment = VerticalAlignment.Center };
        // A member's type follows its name close, as "sum(int): long"; a container stands apart.
        TextBlock detail = new()
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        string? letterBrush = null, badgeBrush = null, whereBrush = null, detailBrush = null;
        where.Resource(TextBlock.ForegroundProperty, ref whereBrush, TokenKeys.TextMuted);
        detail.Resource(TextBlock.ForegroundProperty, ref detailBrush, TokenKeys.TextMuted);
        letter.Resource(TextBlock.ForegroundProperty, ref letterBrush, TokenKeys.BadgeText);
        // The detail is cut short where the row is narrower than its words, never drawn under where it is.
        DockPanel named = new() { ClipToBounds = true, Children = { name, detail } };
        DockPanel row = new() { Height = ListRow.Height, Children = { where, icon, badge, named } };
        return row.Showing<SymbolRow>(symbol =>
        {
            bool file = symbol.Kind == SymbolKind.File;
            icon.IsVisible = file;
            badge.IsVisible = !file;
            if (file)
            {
                icon.Data = FileIcons.ForFile(symbol.Path);
                icon.Foreground = FileIcons.BrushForFile(symbol.Path);
            }
            else
            {
                (string kind, string brush) = Badge(symbol.Kind);
                letter.Text = kind;
                badge.Resource(Border.BackgroundProperty, ref badgeBrush, brush);
            }
            where.Text = symbol.Where ?? string.Empty;
            name.Text = symbol.Name;
            detail.Text = symbol.Detail ?? string.Empty;
            detail.Margin = new Thickness(symbol.Detail?.StartsWith(':') == true ? 0 : 8, 0, 0, 0);
            row.Margin = new Thickness(symbol.Depth * 16, 0, 0, 0);
        });
    }

    /// <summary>The letter and colour of a symbol of each kind.</summary>
    public static (string Letter, string Brush) Badge(SymbolKind kind) => kind switch
    {
        SymbolKind.Namespace or SymbolKind.Package or SymbolKind.Module => ("N", TokenKeys.Syntax_Namespace),
        SymbolKind.Class => ("C", TokenKeys.Syntax_Class),
        SymbolKind.Method or SymbolKind.Function or SymbolKind.Constructor => ("m", TokenKeys.Syntax_Method),
        SymbolKind.Property => ("p", TokenKeys.Syntax_Property),
        SymbolKind.Field => ("f", TokenKeys.Syntax_Field),
        SymbolKind.Enum => ("E", TokenKeys.Syntax_Enum),
        SymbolKind.Interface => ("I", TokenKeys.Syntax_Interface),
        SymbolKind.Variable => ("v", TokenKeys.TextMuted),
        SymbolKind.Constant => ("c", TokenKeys.Syntax_Constant),
        SymbolKind.EnumMember => ("e", TokenKeys.Syntax_EnumMember),
        SymbolKind.Struct => ("S", TokenKeys.Syntax_Struct),
        SymbolKind.Event => ("e", TokenKeys.Syntax_Event),
        SymbolKind.Operator => ("o", TokenKeys.TextMuted),
        SymbolKind.TypeParameter => ("T", TokenKeys.Syntax_TypeParameter),
        _ => ("·", TokenKeys.TextMuted),
    };
}
