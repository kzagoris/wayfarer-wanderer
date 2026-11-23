// From Passenger 2044e49b src/Passenger.App/Navigation/GoToLinePopup.axaml(.cs), MIT (notices/Passenger/LICENSE).
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Passenger.Text;

namespace WayfarerWanderer.App.Shell.Navigation;

/// <summary>Go to Line:Column: "LINE" or "LINE:COLUMN", both from one as the status line shows them, the caret's place as
/// its starting text.</summary>
public sealed class GoToLinePopup : UserControl, IAnswers<TextPosition?>
{
    private readonly int lineCount;
    private readonly TextBox query;
    private readonly TextBlock problem;

    public GoToLinePopup(TextPosition current, int lineCount)
    {
        this.lineCount = lineCount;
        Width = PopupFrame.Narrow;
        query = new TextBox { Classes = { StyleClasses.Search }, Text = $"{current.Line + 1}:{current.Column + 1}" };
        problem = new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 12, IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Content = new Border
        {
            Classes = { StyleClasses.Popup },
            Padding = new Thickness(12, 10),
            Child = new StackPanel
            {
                Spacing = 8,
                Children = { new TextBlock { Classes = { ShellClasses.PopupTitle }, Text = "Go to Line:Column" }, query, problem },
            },
        };
        query.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        Dispatcher.UIThread.Post(() =>
        {
            query.Focus();
            query.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Where to go (from zero), or null when given up.</summary>
    public event EventHandler<TextPosition?>? Done;

    public string Query
    {
        get => query.Text ?? string.Empty;
        set => query.Text = value;
    }

    /// <summary>Why the query cannot be gone to, once Enter found it could not; null while it says nothing.</summary>
    public string? Problem => problem.IsVisible ? problem.Text : null;

    /// <summary>Goes where the query says, as Enter does, or says why it cannot.</summary>
    public void Go()
    {
        if (Parse(Query) is { } position)
        {
            Done?.Invoke(this, position);
            return;
        }
        problem.Text = $"A line from 1 to {lineCount}, and a column after a colon if wanted";
        problem.IsVisible = true;
    }

    public void GiveUp() => Done?.Invoke(this, null);

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            GiveUp();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Go();
            e.Handled = true;
        }
    }

    private TextPosition? Parse(string text)
    {
        string[] parts = text.Trim().Split(':', ',');
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out int line) || line < 1 || line > lineCount)
        {
            return null;
        }
        int column = 1;
        if (parts.Length == 2 && parts[1].Length > 0 && (!int.TryParse(parts[1], CultureInfo.InvariantCulture, out column) || column < 1))
        {
            return null;
        }
        return new TextPosition(line - 1, column - 1);
    }
}
