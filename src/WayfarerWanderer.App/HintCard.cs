using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Passenger.Editor;
using WayfarerWanderer.App.Shell;

namespace WayfarerWanderer.App;

/// <summary>What the language server says of an inlay hint the pointer rests on, as Passenger's hover card shows a hint's
/// tooltip: a card hung under the hint's line, or over it where the window has no room below. Its Markdown is read as
/// <see cref="HoverMarkdown"/> reads a hover: fenced code in the editor's font, the prose between in the window's. It
/// takes no pointer, so it never stands between the pointer and the code, and goes as the pointer leaves the hint.</summary>
public sealed class HintCard
{
    private readonly CodeEditor editor;
    private readonly StackPanel parts = new() { Spacing = Gaps.Small };
    private readonly Border frame;

    public HintCard(CodeEditor editor)
    {
        this.editor = editor;
        frame = new Border
        {
            Classes = { StyleClasses.Popup },
            Padding = PopupFrame.CardInset,
            MaxWidth = PopupFrame.Medium,
            Child = parts,
            IsHitTestVisible = false,
            IsVisible = false,
        };
        AutomationProperties.SetName(frame, "Hint");
    }

    public bool IsOpen => frame.IsVisible && frame.Parent is not null;

    /// <summary>What the card says, a line a part: for tests.</summary>
    public string Text => IsOpen ? string.Join('\n', parts.Children.OfType<TextBlock>().Select(part => part.Text)) : string.Empty;

    /// <summary>Shows Markdown in the card, its top at <paramref name="below"/> of the editor's (under the line whose top is
    /// <paramref name="lineTop"/>); nothing shown where the Markdown has no words.</summary>
    public void Show(string markdown, Point below, double lineTop)
    {
        Close();
        foreach ((IReadOnlyList<string> lines, bool code, _) in HoverMarkdown.ByKind(markdown, plain: false, words: true))
        {
            string text = string.Join('\n', lines).Trim('\n', ' ');
            if (text.Length > 0)
            {
                parts.Children.Add(code
                    ? new TextBlock { Text = text, FontFamily = editor.FontFamily, FontSize = editor.FontSize, TextWrapping = TextWrapping.Wrap }
                    : new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
            }
        }
        if (parts.Children.Count == 0 || OverlayLayer.GetOverlayLayer(editor) is not { } layer
            || editor.TranslatePoint(below, layer) is not { } place || editor.TranslatePoint(new Point(below.X, lineTop), layer) is not { } top)
        {
            parts.Children.Clear();
            return;
        }
        if (frame.Parent != layer)
        {
            (frame.Parent as Panel)?.Children.Remove(frame);
            layer.Children.Add(frame);
        }
        frame.IsVisible = true;
        frame.Measure(Size.Infinity);
        Size size = frame.DesiredSize;
        Size room = TopLevel.GetTopLevel(editor)?.ClientSize ?? layer.Bounds.Size;
        Canvas.SetLeft(frame, Math.Max(0, Math.Min(place.X, room.Width - size.Width)));
        Canvas.SetTop(frame, place.Y + size.Height > room.Height ? Math.Max(0, top.Y - size.Height - Gaps.Hair) : place.Y);
    }

    public void Close()
    {
        frame.IsVisible = false;
        parts.Children.Clear();
        (frame.Parent as Panel)?.Children.Remove(frame);
    }
}
