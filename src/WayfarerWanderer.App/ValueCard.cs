using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using WayfarerWanderer.App.Shell;

namespace WayfarerWanderer.App;

/// <summary>A value read in the editor, as IntelliJ's hover and Quick Evaluate show one: a card under the expression with
/// its row, which opens into fields and elements and takes the rows' actions (Set Value, Add to Watches…). A hovered
/// card goes as the mouse leaves it; one clicked, or one Quick Evaluate opened, stays until Escape or a click elsewhere.</summary>
public sealed class ValueCard
{
    private readonly Control anchor;
    private readonly Border frame;
    private readonly ObservableCollection<VariableNode> rows = [];
    private TopLevel? watched;

    public ValueCard(Control anchor)
    {
        this.anchor = anchor;
        View = new VariablesView(rows) { MinWidth = 280, MaxWidth = 640, MaxHeight = 320 };
        frame = new Border { Classes = { StyleClasses.Popup }, Padding = new Thickness(4), Child = View, IsVisible = false };
        frame.AddHandler(InputElement.PointerPressedEvent, (_, _) => IsSticky = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        frame.PointerExited += (_, _) =>
        {
            if (!IsSticky) Close();
        };
    }

    public VariablesView View { get; }

    public bool IsOpen => frame.IsVisible && frame.Parent is not null;

    /// <summary>Stays until Escape or a click elsewhere, rather than going with the mouse.</summary>
    public bool IsSticky { get; private set; }

    public bool IsPointerOver => IsOpen && frame.IsPointerOver;

    /// <summary>The value shown: for tests.</summary>
    public VariableNode? Node => IsOpen ? rows.FirstOrDefault() : null;

    /// <summary>Shows a value at a point of the anchor's (under the expression's line).</summary>
    public void Show(VariableNode node, Point at, bool sticky)
    {
        if (OverlayLayer.GetOverlayLayer(anchor) is not { } layer || anchor.TranslatePoint(at, layer) is not { } place) return;
        rows.Clear();
        rows.Add(node);
        IsSticky = sticky;
        if (frame.Parent != layer)
        {
            (frame.Parent as Panel)?.Children.Remove(frame);
            layer.Children.Add(frame);
        }
        Watch();
        frame.IsVisible = true;
        frame.Measure(Size.Infinity);
        Size size = frame.DesiredSize;
        Size room = TopLevel.GetTopLevel(anchor)?.ClientSize ?? layer.Bounds.Size;
        Canvas.SetLeft(frame, Math.Max(0, Math.Min(place.X, room.Width - size.Width)));
        Canvas.SetTop(frame, place.Y + size.Height > room.Height ? Math.Max(0, place.Y - size.Height - 20) : place.Y);
    }

    public void Close()
    {
        if (frame.Parent is null) return;
        frame.IsVisible = false;
        (frame.Parent as Panel)?.Children.Remove(frame);
        rows.Clear();
        IsSticky = false;
    }

    /// <summary>A click anywhere but the card, or the menus and editors it opened, closes it.</summary>
    private void Watch()
    {
        if (watched is not null || TopLevel.GetTopLevel(anchor) is not { } top) return;
        watched = top;
        top.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!IsOpen || frame.IsPointerOver) return;
            if (e.Source is Visual source && (source == frame || source.GetVisualAncestors().Any(a => a == frame || a is MenuItem or ContextMenu or PopupRoot)))
                return;
            Close();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            // Escape in the card's Set Value box leaves the box, not the card.
            bool editing = top.FocusManager?.GetFocusedElement() is Visual focused && focused.GetVisualAncestors().Contains(frame);
            if (e.Key == Key.Escape && IsOpen && !editing)
            {
                Close();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }
}
