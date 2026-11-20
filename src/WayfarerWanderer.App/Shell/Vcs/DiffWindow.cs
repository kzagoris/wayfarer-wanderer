// From Passenger 2044e49b src/Passenger.App/Vcs/DiffWindow.cs, MIT (notices/Passenger/LICENSE).
using Avalonia.Controls;
using Avalonia.Input;

namespace WayfarerWanderer.App.Shell.Vcs;

/// <summary>A diff in a window of its own over the IDE's; Escape closes it.</summary>
public sealed class DiffWindow : IdeWindow, IDiffHost
{
    public DiffWindow(DiffContent shown, DiffView view)
    {
        Shown = shown;
        Title = shown.Title;
        Width = 1200;
        Height = 760;
        MinWidth = 600;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        View = view;
        Content = view;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                Close();
                e.Handled = true;
            }
        };
        Opened += (_, _) => view.Right.Focus();
    }

    public DiffContent Shown { get; }

    public DiffView View { get; }

    public void GiveWay() => Close();
}
