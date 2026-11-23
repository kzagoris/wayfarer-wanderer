// From Passenger 8a1df7e4 src/Passenger.App/SearchPopup.cs, MIT (notices/Passenger/LICENSE).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;

namespace WayfarerWanderer.App.Shell.Navigation;

/// <summary>A popup that ends with an answer: what was chosen, or null when it was given up.</summary>
public interface IAnswers<T>
{
    event EventHandler<T>? Done;
}

/// <summary>Every search popup's one frame (Find Action, Search Everywhere, Go to File, Go to Symbol, File Structure,
/// Recent Files): its title, a switch at its right when it has one, the query (its text chosen as the popup opens), the
/// rows found under it, the words for none, and its keys: Up, Down, Page Up and Page Down move, Enter or a double-click
/// chooses, Escape gives up. A popup of it says what a query finds and draws its rows.</summary>
public abstract class SearchPopup<T> : UserControl, IAnswers<T?>
    where T : class
{
    /// <summary>How tall the list grows before it scrolls: fifteen rows.</summary>
    public const double ListHeight = ListRow.Height * 15;

    private readonly TextBlock title;
    private readonly TextBox query;
    private readonly ListBox results;
    private readonly TextBlock empty;
    private readonly CheckBox? option;
    private readonly DockPanel heading = new();

    protected SearchPopup(string title, string placeholder, double width, string? initialQuery = null, (string Label, bool Checked)? option = null)
    {
        Width = width;
        this.title = new TextBlock { Classes = { StyleClasses.PaneTitle }, Text = title, VerticalAlignment = VerticalAlignment.Center };
        query = SearchBox.InPopup(title, placeholder);
        query.Text = initialQuery ?? string.Empty;
        results = new ListBox
        {
            Classes = { StyleClasses.Tree },
            MaxHeight = ListHeight,
            Margin = new Thickness(0, 0, 0, Gaps.Normal),
        };
        ReusedRows.Use(results, Row);
        empty = new TextBlock { Classes = { StyleClasses.Muted }, Margin = new Thickness(Gaps.Wide, 0, Gaps.Wide, Gaps.Wide), IsVisible = false };
        ListKeys.Act(results, new ListActs { Open = Choose });
        results.SelectionChanged += (_, _) => ChoiceMoved();
        query.TextChanged += (_, _) => QueryChanged();
        query.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        if (option is { } shown)
        {
            this.option = PopupFrame.Option(shown.Label, shown.Checked);
            this.option.IsCheckedChanged += (_, _) => OptionTurned();
            DockPanel.SetDock(this.option, Dock.Right);
            heading.Children.Add(this.option);
        }
        heading.Children.Add(this.title);
        StackPanel top = new() { Margin = new Thickness(Gaps.Wide, Gaps.Wide, Gaps.Wide, Gaps.Normal), Spacing = Gaps.Normal, Children = { heading, query } };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(empty, Dock.Top);
        Content = new Border { Classes = { StyleClasses.Popup }, Child = new DockPanel { Children = { top, empty, results } } };
        Dispatcher.UIThread.Post(FocusQuery, DispatcherPriority.Loaded);
    }

    /// <summary>The row chosen, or null when the popup was given up.</summary>
    public event EventHandler<T?>? Done;

    /// <summary>What is listed now.</summary>
    public IReadOnlyList<T> Rows => results.ItemsSource as IReadOnlyList<T> ?? [];

    public T? Selected
    {
        get => results.SelectedItem as T;
        set => results.SelectedItem = value;
    }

    public string Query
    {
        get => query.Text ?? string.Empty;
        set => query.Text = value;
    }

    /// <summary>The query's box, which has the keys while the popup is open.</summary>
    public TextBox QueryBox => query;

    /// <summary>What the popup is called over its query, and the query's name.</summary>
    public string Title
    {
        get => title.Text ?? string.Empty;
        protected set
        {
            title.Text = value;
            Avalonia.Automation.AutomationProperties.SetName(query, value);
        }
    }

    /// <summary>What the popup says in place of rows (nothing found, or why it could not look), or null when it lists some.</summary>
    public string? Said => empty.IsVisible ? empty.Text : null;

    /// <summary>Whether the popup's switch is on; false for a popup without one.</summary>
    public bool IsOptionChecked
    {
        get => option?.IsChecked == true;
        set
        {
            if (option is not null)
            {
                option.IsChecked = value;
            }
        }
    }

    /// <summary>The query given the keys, its text chosen.</summary>
    public void FocusQuery()
    {
        query.Focus();
        query.SelectAll();
    }

    /// <summary>Takes the row chosen, as Enter does.</summary>
    public void Choose()
    {
        if (Selected is { } row)
        {
            Done?.Invoke(this, row);
        }
    }

    /// <summary>Gives the popup up, as Escape does.</summary>
    public void GiveUp() => Done?.Invoke(this, null);

    /// <summary>A row made once for each of the list's containers, showing whichever found item the list hands it
    /// (<see cref="ReusedRows.Showing{T}"/>).</summary>
    protected abstract Control Row();

    /// <summary>The query changed: what it finds is listed again.</summary>
    protected abstract void QueryChanged();

    /// <summary>The switch turned.</summary>
    protected virtual void OptionTurned() => QueryChanged();

    /// <summary>The row chosen changed: by a key, a click, or what is listed.</summary>
    protected virtual void ChoiceMoved()
    {
    }

    /// <summary>Chooses the row at an index of those listed, brought into sight.</summary>
    protected void ChooseAt(int index)
    {
        results.SelectedIndex = index;
        results.ScrollIntoView(index);
    }

    /// <summary>Lists what a query found, <paramref name="chosen"/> chosen and in sight, or <paramref name="none"/> when it
    /// found nothing.</summary>
    protected void Show(IReadOnlyList<T> found, string none, int chosen = 0)
    {
        results.ItemsSource = found;
        results.SelectedIndex = found.Count > 0 ? Math.Clamp(chosen, 0, found.Count - 1) : -1;
        if (results.SelectedIndex > 0)
        {
            results.ScrollIntoView(results.SelectedIndex);
        }
        empty.Text = none;
        empty.IsVisible = found.Count == 0;
    }

    /// <summary>Says why nothing could be listed (a search that failed), over the list.</summary>
    protected void Say(string why)
    {
        empty.Text = why;
        empty.IsVisible = true;
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (ListKeys.Move(results, e))
        {
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Enter:
                Choose();
                e.Handled = true;
                break;
            case Key.Escape:
                GiveUp();
                e.Handled = true;
                break;
        }
    }
}
