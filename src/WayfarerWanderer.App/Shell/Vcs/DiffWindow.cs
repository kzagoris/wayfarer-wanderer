// From Passenger 494009d7 src/Passenger.App/Vcs/DiffWindow.cs, MIT (notices/Passenger/LICENSE).
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
        Content = new Panel { Children = { view, Assist } };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                Close();
                e.Handled = true;
            }
        };
        Opened += (_, _) => view.Keyed.Focus();
    }

    public DiffContent Shown { get; }

    public DiffView View { get; }

    /// <summary>The layer over the diff that the code assistance's cards stand on, as over the editors.</summary>
    public Canvas Assist { get; } = new() { ClipToBounds = false };

    public void GiveWay() => Close();
}
