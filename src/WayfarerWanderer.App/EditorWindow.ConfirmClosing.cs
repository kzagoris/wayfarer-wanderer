// After Passenger 8a1df7e4 src/Passenger.App/MainWindow.ConfirmClosing.cs, MIT (notices/Passenger/LICENSE): the question
// and its rules kept, the agents' input and Hot Reload's held programs cut, the system's Quit asked once from here rather
// than from the application.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Passenger.Core;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Settings;

namespace WayfarerWanderer.App;

/// <summary>Confirm before closing (Settings, System Settings): as chosen, the person is asked before Wayfarer quits (Exit,
/// the system's Quit, the last window's close), once however many windows the quit closes, or before any window closes;
/// Cancel keeps it open, and Don't ask again sets the setting to Never. Nothing is asked in a quit already under way, at the
/// system's shutdown, or in a window that keeps no settings (a headless one's).</summary>
public sealed partial class EditorWindow
{
    /// <summary>What the person's quit is called here, the question's title and answer: Quit on macOS, Exit elsewhere.</summary>
    public static readonly string QuitWord = OperatingSystem.IsMacOS() ? "Quit" : "Exit";

    /// <summary>The answer of the question before a window closes.</summary>
    public const string CloseWord = "Close";

    /// <summary>The window asking whether to quit while it asks.</summary>
    private static EditorWindow? askingQuit;

    /// <summary>Whether the window closes as the person answered a question of its close, or as Wayfarer quits, which asks
    /// nothing more however long its close then takes (its session ending first).</summary>
    private bool closeAnswered;

    private bool askingClose;

    /// <summary>Whether the person is asked before Wayfarer quits from this window.</summary>
    internal bool ConfirmsQuit => Confirms(ConfirmClosing.WhenQuitting, ConfirmClosing.EveryWindow);

    private bool Confirms(params ConfirmClosing[] asking) => EditorApplication.State is not null && asking.Contains(SettingsKept.Value.ConfirmBeforeClosing);

    private static IEnumerable<EditorWindow> Windows =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop ? desktop.Windows.OfType<EditorWindow>() : [];

    /// <summary>Whether this window is the last of the person's open, whose close quits Wayfarer.</summary>
    private bool IsLastWindow => !Windows.Any(w => w != this && w.IsVisible);

    /// <summary>The question Confirm before closing asks before this close goes on, or null where it asks none: the system's
    /// Quit asks once for every window, the last window's close as a quit, any other's as Every window says.</summary>
    private Task? ConfirmFirst(WindowClosingEventArgs e)
    {
        if (closeAnswered || e.CloseReason == WindowCloseReason.OSShutdown)
        {
            return null;
        }
        if (e.CloseReason == WindowCloseReason.ApplicationShutdown)
        {
            // The system's Quit, never Wayfarer's own shutdown of its lifetime.
            return !e.IsProgrammatic && ConfirmsQuit ? QuitAskedAsync(this) : null;
        }
        bool last = IsLastWindow;
        return (last ? ConfirmsQuit : Confirms(ConfirmClosing.EveryWindow)) ? CloseConfirmedAsync(quits: last) : null;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (ConfirmFirst(e) is { } asking)
        {
            e.Cancel = true;
            IdeErrors.Observe(asking);
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>File, Exit, or the system's Quit where Confirm before closing asks: every window closes once the person
    /// agrees, asked once over the window they were in.</summary>
    private static async Task QuitAskedAsync(EditorWindow from)
    {
        if (askingQuit is { } asking)
        {
            asking.OwnedWindows.OfType<ConfirmDialog>().LastOrDefault()?.Activate();
            return;
        }
        EditorWindow over = Windows.FirstOrDefault(w => w.IsActive) ?? from;
        askingQuit = over;
        bool agreed;
        try
        {
            // Once the close in hand is over, as the system's Quit goes on to close each window, which waits on this.
            await Task.Yield();
            over.Activate();
            agreed = await over.ConfirmedAsync(quits: true);
        }
        finally
        {
            askingQuit = null;
        }
        if (agreed)
        {
            Quit();
        }
    }

    /// <summary>Every Wayfarer window closes, each ending its folder's session as its own close does, asking nothing more.</summary>
    internal static void Quit()
    {
        foreach (EditorWindow window in Windows)
        {
            window.closeAnswered = true;
        }
        foreach (Window window in Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop ? desktop.Windows.ToList() : [])
        {
            window.Close();
        }
    }

    /// <summary>The window was closed where Confirm before closing asks: it closes once the person agrees, else stays open.</summary>
    private async Task CloseConfirmedAsync(bool quits)
    {
        if (askingClose)
        {
            return;
        }
        askingClose = true;
        bool agreed;
        try
        {
            agreed = await ConfirmedAsync(quits);
        }
        finally
        {
            askingClose = false;
        }
        if (agreed)
        {
            CloseAsAnswered();
        }
    }

    /// <summary>Confirm before closing's question of a quit, or of this window's close, over this window: whether the person
    /// agreed. Don't ask again, ticked as they agree, sets the setting to Never.</summary>
    internal async Task<bool> ConfirmedAsync(bool quits)
    {
        ConfirmDialog asked = ClosingQuestion(quits, Session?.Root);
        bool agreed = await asked.ShowDialog<bool>(this);
        if (agreed && asked.NotAgain)
        {
            SettingsKept.Update(s => s with { ConfirmBeforeClosing = ConfirmClosing.Never });
        }
        return agreed;
    }

    /// <summary>The question of a quit, or of the close of a window on <paramref name="folder"/> (none for the Welcome
    /// screen's).</summary>
    public static ConfirmDialog ClosingQuestion(bool quits, string? folder) => quits
        ? new ConfirmDialog($"{QuitWord} {Product.Name}", $"{Product.Name}'s windows close, and the programs, builds and terminals they run end with them.",
            QuitWord, offersNotAgain: true)
        : new ConfirmDialog("Close Window", folder is null ? "This window closes."
            : $"The window on {Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))} closes, and the programs, builds and terminals it runs end with it.",
            CloseWord, offersNotAgain: true);

    /// <summary>Closes the window as the person answered a question of its close, asking nothing more of Confirm before
    /// closing.</summary>
    private void CloseAsAnswered()
    {
        closeAnswered = true;
        Close();
    }
}
