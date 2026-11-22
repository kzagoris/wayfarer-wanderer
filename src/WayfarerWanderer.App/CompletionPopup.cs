using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;

namespace WayfarerWanderer.App;

/// <summary>
/// The list of suggestions under the caret. It never takes the keyboard: the window hands it the keys that move and
/// choose (<see cref="TakeKey"/>) and the editor keeps the rest, so typing goes on narrowing the list. A click chooses.
/// Each row is a coloured mark for the suggestion's kind, its name in the editor's font with the letters typed marked,
/// what qualifies it (a package) faint beside it and its type at the right. Rows are a fixed height, so the list is as
/// tall as the rows it shows, up to <see cref="RowsShown"/>; a line under them says how to choose and how many match.
/// </summary>
public sealed class CompletionPopup
{
    private const int RowsShown = 10;

    /// <summary>The room round the rows inside the frame, each row's own inset, and the column its kind's mark stands in:
    /// together how far right of the frame's edge a name begins.</summary>
    private const double ListPadding = 4, RowPadding = 6, MarkColumn = 24, MarkSize = 16;

    /// <summary>The most rows built; a longer list is narrowed by typing before the rest matter.</summary>
    private const int MostRows = 200;

    private readonly Control editor;
    private readonly Border frame;
    private readonly StackPanel rows = new();
    private readonly ScrollViewer scroller;
    private readonly TextBlock count = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border footer;
    private IReadOnlyList<CompletionChoice> shown = [];
    private int selected = -1;
    private double rowHeight = 24;

    public CompletionPopup(Control editor)
    {
        this.editor = editor;
        scroller = new ScrollViewer
        {
            Content = rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(ListPadding),
        };
        TextBlock hint = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Text = "Enter or Tab to insert   ·   Esc to close" };
        Grid foot = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        foot.Children.Add(hint);
        foot.Children.Add(count);
        Grid.SetColumn(count, 1);
        footer = new Border { Padding = new Thickness(10, 4), BorderThickness = new Thickness(0, 1, 0, 0), Child = foot };
        DockPanel body = new() { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        body.Children.Add(scroller);
        frame = new Border
        {
            Classes = { StyleClasses.Popup }, MinWidth = 360, MaxWidth = 680, Child = body, Focusable = false, IsVisible = false,
        };
    }

    /// <summary>A suggestion was clicked.</summary>
    public event Action<CompletionChoice>? Chosen;

    public bool IsOpen => frame.IsVisible && frame.Parent is not null;

    /// <summary>The suggestions listed, in order: for tests.</summary>
    public IReadOnlyList<CompletionChoice> Shown => IsOpen ? shown : [];

    /// <summary>Where the list stands in the editor's coordinates while it is open: for tests.</summary>
    public Rect? Place => IsOpen && frame.Parent is Visual layer && layer.TranslatePoint(new Point(Canvas.GetLeft(frame), Canvas.GetTop(frame)), editor) is { } at
        ? new Rect(at, frame.DesiredSize) : null;

    /// <summary>How far right of the list's left edge a suggestion's name begins: the list stands that far left of the
    /// word so the names line up under what was typed.</summary>
    public double TextInset => frame.BorderThickness.Left + ListPadding + RowPadding + MarkColumn;

    public CompletionChoice? Selected => IsOpen && selected >= 0 && selected < shown.Count ? shown[selected] : null;

    /// <summary>Lists <paramref name="choices"/> under the caret's row (<paramref name="caretTop"/>, in the editor's
    /// coordinates), keeping the selected one when it is still listed and marking in each name the letters of
    /// <paramref name="typed"/> it matched.</summary>
    public void Show(IReadOnlyList<CompletionChoice> choices, Point caretTop, double lineHeight, string typed = "")
    {
        CompletionChoice? keep = Selected;
        shown = choices.Count > MostRows ? [.. choices.Take(MostRows)] : choices;
        Look look = new(this);
        rowHeight = look.RowHeight;
        scroller.MaxHeight = RowsShown * rowHeight;
        // The scroll bar lies over the rows; where there is one, the types at the right stand clear of it.
        rows.Margin = new Thickness(0, 0, shown.Count > RowsShown ? 10 : 0, 0);
        footer.BorderBrush = look.Divider;
        footer.Child!.SetValue(TextElement.ForegroundProperty, look.Muted);
        count.Text = choices.Count > shown.Count ? $"{shown.Count} of {choices.Count}" : choices.Count == 1 ? "1 match" : $"{choices.Count} matches";
        rows.Children.Clear();
        int index = 0;
        for (int i = 0; i < shown.Count; i++)
        {
            rows.Children.Add(Row(shown[i], i, typed, look));
            if (keep is not null && ReferenceEquals(shown[i], keep))
            {
                index = i;
            }
        }
        if (!PlaceAt(caretTop, lineHeight))
        {
            Close();
            return;
        }
        selected = -1;
        Select(index);
    }

    /// <summary>Puts the list in the window's overlay under the caret's row, or over it where the window has no room
    /// below, with its names under the word and slid left to stay inside. False where the editor is in no window.</summary>
    private bool PlaceAt(Point caretTop, double lineHeight)
    {
        if (OverlayLayer.GetOverlayLayer(editor) is not { } layer || editor.TranslatePoint(caretTop, layer) is not { } at)
        {
            return false;
        }
        if (frame.Parent != layer)
        {
            (frame.Parent as Panel)?.Children.Remove(frame);
            layer.Children.Add(frame);
        }
        frame.IsVisible = true;
        frame.Measure(Size.Infinity);
        Size size = frame.DesiredSize;
        // The overlay may be laid out at no size of its own; the window's client area is the room it draws in.
        Size room = TopLevel.GetTopLevel(editor)?.ClientSize ?? layer.Bounds.Size;
        double top = at.Y + lineHeight;
        if (top + size.Height > room.Height && at.Y - size.Height >= 0)
        {
            top = at.Y - size.Height;
        }
        double left = Math.Max(0, Math.Min(at.X - TextInset, room.Width - size.Width));
        Canvas.SetLeft(frame, left);
        Canvas.SetTop(frame, top);
        return true;
    }

    public void Close()
    {
        frame.IsVisible = false;
        (frame.Parent as Panel)?.Children.Remove(frame);
        rows.Children.Clear();
        shown = [];
        selected = -1;
    }

    /// <summary>Runs a key meant for the list: moving (the arrows, the page keys) and choosing (Enter, Tab), closing
    /// (Escape). False for any other key, which the editor then takes.</summary>
    public bool TakeKey(Key key, KeyModifiers modifiers)
    {
        if (!IsOpen || modifiers != KeyModifiers.None)
        {
            return false;
        }
        switch (key)
        {
            case Key.Down: Move(1); return true;
            case Key.Up: Move(-1); return true;
            case Key.PageDown: Move(RowsShown - 1); return true;
            case Key.PageUp: Move(-(RowsShown - 1)); return true;
            case Key.Enter or Key.Tab:
                if (Selected is { } choice)
                {
                    Chosen?.Invoke(choice);
                }
                return true;
            case Key.Escape: Close(); return true;
            default: return false;
        }
    }

    private void Move(int by)
    {
        if (shown.Count == 0)
        {
            return;
        }
        int at = selected + by;
        // A single step wraps round the ends; a page stops at them.
        Select(Math.Abs(by) == 1 ? (at + shown.Count) % shown.Count : Math.Clamp(at, 0, shown.Count - 1));
    }

    private void Select(int index)
    {
        if (selected >= 0 && selected < rows.Children.Count)
        {
            ((Border)rows.Children[selected]).Background = Brushes.Transparent;
        }
        selected = index;
        if (index < 0 || index >= rows.Children.Count)
        {
            return;
        }
        Border row = (Border)rows.Children[index];
        row.Background = Resource("Passenger.RowSelected") ?? Resource("Passenger.Selected") ?? Brushes.Transparent;
        // Keep the chosen row in view: scroll just enough to show it.
        double top = index * rowHeight, bottom = top + rowHeight;
        double view = Math.Min(scroller.MaxHeight, shown.Count * rowHeight);
        if (top < scroller.Offset.Y)
        {
            scroller.Offset = new Vector(0, top);
        }
        else if (bottom > scroller.Offset.Y + view)
        {
            scroller.Offset = new Vector(0, bottom - view);
        }
    }

    private Border Row(CompletionChoice choice, int index, string typed, Look look)
    {
        (string name, string qualifier, string trailing) = choice.Parts;
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions($"{MarkColumn},*,Auto") };
        content.Children.Add(Mark(choice, look));

        TextBlock label = new()
        {
            FontFamily = look.Code, FontSize = look.CodeSize, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Inlines = [],
        };
        IReadOnlyList<int> matched = CompletionModel.MatchedAt(name, typed);
        TextDecorationCollection? struck = choice.Deprecated ? TextDecorations.Strikethrough : null;
        for (int start = 0; start < name.Length;)
        {
            bool hit = matched.Contains(start);
            int end = start + 1;
            while (end < name.Length && matched.Contains(end) == hit)
            {
                end++;
            }
            Run run = new(name[start..end]) { TextDecorations = struck };
            if (hit)
            {
                run.Foreground = look.Match;
                run.FontWeight = FontWeight.Bold;
            }
            label.Inlines.Add(run);
            start = end;
        }
        if (qualifier.Length > 0)
        {
            label.Inlines.Add(new Run("  " + qualifier) { Foreground = look.Muted });
        }
        content.Children.Add(label);
        Grid.SetColumn(label, 1);

        if (trailing.Length > 0)
        {
            TextBlock type = new()
            {
                Text = trailing, FontFamily = look.Code, FontSize = look.CodeSize, Foreground = look.Muted, MaxWidth = 240,
                Margin = new Thickness(20, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            content.Children.Add(type);
            Grid.SetColumn(type, 2);
        }

        Border row = new()
        {
            Height = look.RowHeight, Background = Brushes.Transparent, CornerRadius = new CornerRadius(4),
            Padding = new Thickness(RowPadding, 0), Child = content,
        };
        ToolTip.SetTip(row, choice.Detail.Length > 0 && choice.Detail != trailing ? choice.Detail : null);
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                Select(index);
                Chosen?.Invoke(choice);
            }
        };
        return row;
    }

    /// <summary>The suggestion's kind as a small letter in a disc (a type) or rounded square (a member, a value), tinted
    /// with the colour the editor gives that kind of name.</summary>
    private static Border Mark(CompletionChoice choice, Look look)
    {
        Color colour = look.KindColour(choice.Kind);
        bool type = choice.Kind is 7 or 8 or 13 or 22 or 25;
        return new Border
        {
            Width = MarkSize, Height = MarkSize, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(type ? MarkSize / 2 : 4),
            Background = new SolidColorBrush(colour, 0.2),
            BorderBrush = new SolidColorBrush(colour, 0.45), BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = choice.Badge, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(colour),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>A brush of the theme the editor shows in; the frame is out of the tree until it first opens.</summary>
    private IBrush? Resource(string key) =>
        editor.TryFindResource(key, editor.ActualThemeVariant, out object? found) && found is IBrush brush ? brush : null;

    /// <summary>What the rows of one showing are drawn with, looked up once for them all.</summary>
    private sealed class Look
    {
        private readonly CompletionPopup popup;
        private readonly Dictionary<int, Color> colours = [];

        public Look(CompletionPopup popup)
        {
            this.popup = popup;
            (Code, CodeSize) = popup.editor is CodeEditor code ? (code.FontFamily, code.FontSize) : (CodeEditor.CodeFont, 13);
            RowHeight = Math.Max(24, Math.Ceiling(CodeSize + 11));
            Muted = popup.Resource("Passenger.TextMuted") ?? Brushes.Gray;
            Match = popup.Resource("Passenger.Link") ?? popup.Resource("Passenger.Accent") ?? Brushes.DodgerBlue;
            Divider = popup.Resource("Passenger.Separator");
        }

        public FontFamily Code { get; }

        public double CodeSize { get; }

        public double RowHeight { get; }

        public IBrush Muted { get; }

        public IBrush Match { get; }

        public IBrush? Divider { get; }

        public Color KindColour(int kind)
        {
            if (!colours.TryGetValue(kind, out Color colour))
            {
                string key = kind switch
                {
                    2 or 3 or 4 => "Passenger.Syntax.Method",
                    5 => "Passenger.Syntax.Field",
                    10 => "Passenger.Syntax.Property",
                    6 or 18 => "Passenger.Syntax.Local",
                    7 => "Passenger.Syntax.Class",
                    8 => "Passenger.Syntax.Interface",
                    9 or 19 => "Passenger.Syntax.Namespace",
                    13 => "Passenger.Syntax.Enum",
                    20 => "Passenger.Syntax.EnumMember",
                    21 => "Passenger.Syntax.Constant",
                    14 => "Passenger.Syntax.Keyword",
                    15 => "Passenger.Syntax.String",
                    22 => "Passenger.Syntax.Struct",
                    25 => "Passenger.Syntax.TypeParameter",
                    _ => "Passenger.TextMuted",
                };
                colour = popup.Resource(key) is ISolidColorBrush solid ? solid.Color
                    : (popup.Resource("Passenger.TextMuted") as ISolidColorBrush)?.Color ?? Colors.Gray;
                colours[kind] = colour;
            }
            return colour;
        }
    }
}
