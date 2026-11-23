// From Passenger 8a1df7e4 src/Passenger.App/Navigation/ActionPopup.cs, MIT (notices/Passenger/LICENSE).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace WayfarerWanderer.App.Shell.Navigation;

/// <summary>Find Action: every action by name, the recently run ones first, narrowed as words are typed, each with its
/// group and shortcuts and greyed where it cannot run; Enter or a double-click runs the chosen one, Escape gives up.</summary>
public sealed class ActionPopup : SearchPopup<IdeAction>
{
    private readonly ActionRegistry actions;
    private readonly ActionContext context;
    private readonly IReadOnlyList<string> recent;

    public ActionPopup(ActionRegistry actions, ActionContext context, IReadOnlyList<string> recent, string? initialQuery = null)
        : base("Actions", "Type an action's name", PopupFrame.Wide, initialQuery)
    {
        this.actions = actions;
        this.context = context;
        this.recent = recent;
        QueryChanged();
    }

    /// <summary>The context the actions are listed and run in: where the keys were as the popup opened.</summary>
    public ActionContext Context => context;

    protected override void QueryChanged()
    {
        if (actions is null)
        {
            return;
        }
        IReadOnlyList<IdeAction> found = actions.Find(Query);
        if (Query.Trim().Length == 0)
        {
            found = [.. recent.Select(id => found.FirstOrDefault(a => a.Id == id)).OfType<IdeAction>(), .. found.Where(a => !recent.Contains(a.Id))];
        }
        Show(found, "No actions match");
    }

    /// <summary>An action's row: its title, greyed where it cannot run, its group, and its shortcuts at the right.</summary>
    protected override Control Row()
    {
        TextBlock title = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock group = new() { Classes = { StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Wide, 0, 0, 0) };
        group.Classes.Add(StyleClasses.Muted);
        TextBlock keys = ListRow.DetailText();
        DockPanel row = new() { Height = ListRow.Height, Margin = ListRow.Inset };
        row.Children.Add(keys);
        row.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { title, group },
        });
        return row.Showing<IdeAction>(action =>
        {
            title.Text = action.TitleWhere(context);
            // Listed, so it can be found, but greyed: it cannot run here.
            title.Classes.Set(StyleClasses.Muted, !action.CanRun(context));
            group.Text = action.Group;
            keys.Text = actions.ShortcutsShown(action.Id) ?? string.Empty;
        });
    }
}
