namespace WayfarerWanderer.App.Updates;

/// <summary>The last answered check persists across launches. A failed check is eligible at the next timer tick.</summary>
public sealed class UpdateSchedule(string stateFolder)
{
    private readonly string stamp = Path.Combine(stateFolder, "last-update-check");

    public bool IsDue(DateTime now)
    {
        if (!File.Exists(stamp)) return true;
        TimeSpan age = now - File.GetLastWriteTimeUtc(stamp);
        return age < TimeSpan.Zero || age >= TimeSpan.FromDays(1);
    }

    public async Task RecordAsync(bool answered, CancellationToken cancel)
    {
        if (answered)
        {
            Directory.CreateDirectory(stateFolder);
            await File.WriteAllTextAsync(stamp, string.Empty, cancel).ConfigureAwait(false);
        }
        else if (File.Exists(stamp))
        {
            File.Delete(stamp);
        }
    }
}
