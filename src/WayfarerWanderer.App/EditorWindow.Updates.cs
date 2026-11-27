using Avalonia.Threading;
using Passenger.Core;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Updates;

namespace WayfarerWanderer.App;

public sealed partial class EditorWindow
{
    private static Updater? installer;
    private static TaskCompletionSource? checkFinished;
    private static bool checking => checkFinished is not null;
    private static CancellationTokenSource? active;
    private static bool restart;
    private static FileStream? updateLock;
    private static readonly CancellationTokenSource Ending = new();

    private static bool confirmingRestart;
    private static LatestRelease? available;
    private static bool maintenanceDue = true;
    private static Action? stopWatching;

    internal void WatchUpdates(string root)
    {
        installer = Updater.OfThisCopy(root, out _);
        if (installer is InPlaceUpdater inPlace)
            inPlace.BeforeRestart = ReleaseUpdateLock;
        DispatcherTimer timer = new() { Interval = TimeSpan.FromHours(1) };
        timer.Tick += (_, _) =>
        {
            if (Windows.FirstOrDefault(window => window.IsVisible) is { } window)
                IdeErrors.Observe(window.CheckUpdatesAsync(automatic: true));
        };
        var settings = SettingsKept;
        void SettingsChanged(object? sender, Shell.Settings.SettingsChange change)
        {
            if (change.Before.CheckForUpdates == change.After.CheckForUpdates) return;
            maintenanceDue = true;
            active?.Cancel();
            if (!checking)
                IdeErrors.Observe((Windows.FirstOrDefault(window => window.IsVisible) ?? this).CheckUpdatesAsync(automatic: true, force: true));
        }
        settings.Changed += SettingsChanged;
        stopWatching = () =>
        {
            timer.Stop();
            settings.Changed -= SettingsChanged;
        };
        timer.Start();
        Opened += (_, _) => IdeErrors.Observe(CheckUpdatesAsync(automatic: true));
    }

    private async Task CheckUpdatesAsync(bool automatic, bool force = false)
    {
        if (EditorApplication.State is not { } state || Ending.IsCancellationRequested)
            return;
        var schedule = new UpdateSchedule(state.Root);
        if (checking)
        {
            if (!automatic) StatusText = "An update check is already running.";
            return;
        }
        bool enabled = SettingsKept.Value.CheckForUpdates;
        bool due = !automatic || enabled && (force || schedule.IsDue(DateTime.UtcNow));
        if (!due && !maintenanceDue) return;
        if (!automatic) StatusText = "Checking for updates…";
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        checkFinished = finished;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(Ending.Token);
        active = operation;
        CancellationToken cancel = operation.Token;
        try
        {
            Directory.CreateDirectory(state.Root);
            // Separate settings profiles can still update the same installation.
            string lockFolder = installer is null ? state.Root : UserTemporary.Own(UserTemporary.Of(installer.Installed, "wayfarer-update"));
            try
            {
                updateLock ??= new FileStream(Path.Combine(lockFolder, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when (Shell.State.StateFile.HeldElsewhere(error))
            {
                if (!automatic) StatusText = "Another Wayfarer instance is managing updates.";
                return;
            }
            if (maintenanceDue)
            {
                maintenanceDue = false;
                if (installer is not null)
                {
                    if (enabled) await Task.Run(installer.TidyAsync);
                    else await Task.Run(installer.DiscardAsync);
                }
                if (!enabled) available = null;
                string receipt = Path.Combine(state.Root, "installing-version");
                if (File.Exists(receipt))
                {
                    string expected = await File.ReadAllTextAsync(receipt, cancel);
                    StatusText = expected == OwnVersion.Shown ? $"Updated to version {expected}."
                        : $"Update to {expected} did not finish. Running version {OwnVersion.Shown}.";
                    IdeErrors.Write(StatusText!);
                    File.Delete(receipt);
                }
            }
            cancel.ThrowIfCancellationRequested();
            if (!due) return;
            ReleaseAnswer answer = await Releases.AskAsync(ReleaseFeed.Default, OwnVersion.Shown, cancel);
            cancel.ThrowIfCancellationRequested();
            bool answered = answer is not ReleaseAnswer.Failed;
            if (answer is ReleaseAnswer.Newer offered) available = offered.Release;
            else if (answer is ReleaseAnswer.Current && installer?.Ready is null) available = null;
            string message = answer.Said;
            // A portable copy, or a package manager's, says what puts the release in its place.
            if (answer is ReleaseAnswer.Newer && installer is null && Updater.LeftElsewhere() is { } leftElsewhere)
                message += $" {leftElsewhere.Why}";
            else if (answer is ReleaseAnswer.Newer newer && installer is not null && SettingsKept.Value.CheckForUpdates)
            {
                if (installer.Ready == newer.Release.Version)
                    message = ReadyMessage(newer.Release);
                else if (await installer.ElsewhereAsync(cancel) is { } elsewhere)
                    message += $" {elsewhere.Why}";
                else
                {
                    bool reporting = true;
                    var progress = new Progress<int>(percent =>
                    {
                        if (reporting && !cancel.IsCancellationRequested)
                            StatusText = $"Downloading {newer.Release.Tag} ({percent}%)";
                    });
                    try
                    {
                        UpdateOutcome outcome = await installer.DownloadAsync(newer.Release, ((IProgress<int>)progress).Report, cancel);
                        answered = outcome is not UpdateOutcome.Failed;
                        message = outcome is UpdateOutcome.Ready ? ReadyMessage(newer.Release) : outcome.Said;
                    }
                    finally { reporting = false; }
                }
            }
            cancel.ThrowIfCancellationRequested();
            await schedule.RecordAsync(answered, cancel);
            IdeErrors.Write(message);
            if (!automatic || answer is ReleaseAnswer.Newer)
                StatusText = message;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write(error.Message);
            if (!automatic) StatusText = error.Message;
        }
        finally
        {
            active = null;
            checkFinished = null;
            finished.SetResult();
            RefreshUpdateButtons();
            if (installer?.Ready is null)
            {
                ReleaseUpdateLock();
            }
            if (maintenanceDue && cancel.IsCancellationRequested && !Ending.IsCancellationRequested)
                IdeErrors.Observe((Windows.FirstOrDefault(window => window.IsVisible) ?? this).CheckUpdatesAsync(automatic: true, force: true));
        }
    }

    private static string ReadyMessage(LatestRelease release)
    {
        return $"{(release.SecurityUpdate ? "Security update" : "Version")} {release.Version} is ready. Choose Help → Restart to Update, or close Wayfarer to install.";
    }

    private static void RefreshUpdateButtons()
    {
        foreach (var window in Windows) window.RefreshUpdateButton();
    }

    private void RefreshUpdateButton()
    {
        updateButton.IsVisible = available is not null;
        bool ready = installer?.Ready is not null && SettingsKept.Value.CheckForUpdates;
        updateButton.Content = ready
            ? available?.SecurityUpdate == true ? "Restart for security update" : "Restart to update"
            : $"View {(available?.SecurityUpdate == true ? "security release" : "release")} {available?.Version}";
    }

    private async Task OpenUpdateNoticeAsync()
    {
        if (installer?.Ready is not null && SettingsKept.Value.CheckForUpdates) await RestartToUpdateAsync();
        else if (available is { } offered) await Launcher.LaunchUriAsync(offered.Page);
    }

    private async Task RestartToUpdateAsync()
    {
        if (installer?.Ready is not { } version || confirmingRestart || askingQuit is not null) return;
        confirmingRestart = true;
        try
        {
            IReadOnlyList<int> others = await Task.Run(installer.OtherProcesses);
            if (others.Count != 0)
            {
                StatusText = $"Close the other Wayfarer processes before restarting ({string.Join(", ", others)}).";
                return;
            }
            if (ConfirmsQuit && !await ConfirmedAsync(quits: true)) return;
            if (installer.Ready != version || !SettingsKept.Value.CheckForUpdates) return;
            restart = true;
            Quit();
            if (Windows.Any(window => window.IsVisible && !window.closing)) restart = false;
        }
        finally { confirmingRestart = false; }
    }

    private static Task FinishUpdateCheck()
    {
        if (EditorApplication.State is null) return Task.CompletedTask;
        Ending.Cancel();
        return checkFinished?.Task ?? Task.CompletedTask;
    }

    internal static void InstallUpdate(bool enabled)
    {
        stopWatching?.Invoke();
        Ending.Cancel();
        if (enabled && installer?.Ready is { } version && EditorApplication.State is { } state)
        {
            try { File.WriteAllText(Path.Combine(state.Root, "installing-version"), version); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { IdeErrors.Write(error.Message); }
            installer.Install(restart);
        }
        ReleaseUpdateLock();
    }

    private static void ReleaseUpdateLock()
    {
        updateLock?.Dispose();
        updateLock = null;
    }
}
