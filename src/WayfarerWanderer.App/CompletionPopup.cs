using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;

namespace WayfarerWanderer.App;

/// <summary>
/// The list of suggestions under the caret. It never takes the keyboard: the window hands it the keys that move and
/// choose (<see cref="TakeKey"/>) and the editor keeps the rest, so typing goes on narrowing the list. A click chooses.
/// Rows are a fixed height, so the list is as tall as the rows it shows, up to <see cref="RowsShown"/>.
/// </summary>
public sealed class CompletionPopup
{
    private const int RowsShown = 10;
    private const double RowHeight = 22;

    /// <summary>The most rows built; a longer list is narrowed by typing before the rest matter.</summary>
    private const int MostRows = 200;

    private readonly Control editor;
    private readonly Border frame;
    private readonly StackPanel rows = new();
    private readonly ScrollViewer scroller;
    private IReadOnlyList<CompletionChoice> shown = [];
    private int selected = -1;

    public CompletionPopup(Control editor)
    {
        scroller = new ScrollViewer
        {
            Content = rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = RowsShown * RowHeight,
        };
        this.editor = editor;
        frame = new Border
        {
            Classes = { StyleClasses.Popup }, Padding = new Thickness(2), MinWidth = 300, MaxWidth = 640, Child = scroller,
            Focusable = false, IsVisible = false,
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

    public CompletionChoice? Selected => IsOpen && selected >= 0 && selected < shown.Count ? shown[selected] : null;

    /// <summary>Lists <paramref name="choices"/> under the caret's row (<paramref name="caretTop"/>, in the editor's
    /// coordinates), keeping the selected one when it is still listed.</summary>
    public void Show(IReadOnlyList<CompletionChoice> choices, Point caretTop, double lineHeight)
    {
        CompletionChoice? keep = Selected;
        shown = choices.Count > MostRows ? [.. choices.Take(MostRows)] : choices;
        rows.Children.Clear();
        int index = 0;
        for (int i = 0; i < shown.Count; i++)
        {
            rows.Children.Add(Row(shown[i], i));
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
        Select(index);
    }

    /// <summary>Puts the list in the window's overlay under the caret's row, or over it where the window has no room
    /// below, and slid left to stay inside. False where the editor is in no window.</summary>
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
        double left = Math.Max(0, Math.Min(at.X, room.Width - size.Width));
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
        row.Background = frame.TryFindResource("Passenger.Selected", frame.ActualThemeVariant, out object? brush) && brush is IBrush ground ? ground : Brushes.Transparent;
        // Keep the chosen row in view: scroll just enough to show it.
        double top = index * RowHeight, bottom = top + RowHeight;
        double viewTop = scroller.Offset.Y, viewBottom = viewTop + Math.Min(scroller.MaxHeight, shown.Count * RowHeight);
        if (top < viewTop)
        {
            scroller.Offset = new Vector(0, top);
        }
        else if (bottom > viewBottom)
        {
            scroller.Offset = new Vector(0, bottom - Math.Min(scroller.MaxHeight, shown.Count * RowHeight));
        }
    }

    private Border Row(CompletionChoice choice, int index)
    {
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("20,Auto,*") };
        TextBlock badge = new() { Text = choice.Badge, Classes = { StyleClasses.Muted }, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        TextBlock label = new() { Text = choice.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock detail = new()
        {
            Text = choice.Detail, Classes = { StyleClasses.Muted }, Margin = new Thickness(16, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        content.Children.Add(badge);
        content.Children.Add(label);
        content.Children.Add(detail);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(detail, 2);
        Border row = new() { Height = RowHeight, Background = Brushes.Transparent, CornerRadius = new CornerRadius(3), Child = content };
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
}
