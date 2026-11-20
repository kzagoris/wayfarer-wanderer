namespace WayfarerWanderer.App;

/// <summary>The desktop app's command line: <c>WayfarerWanderer.App [FOLDER] [--new-window]</c>. FOLDER opens in the
/// window in place of the folder open as Wayfarer last quit, as a recent folder of the taskbar's jump list, the Dock's menu
/// or the desktop's recently used list starts it; <c>--new-window</c> is a window with nothing open, the jump list's New
/// Window. A flag it does not know, or a second folder, is fatal, never ignored; Velopack's own <c>--velo…</c> flags are
/// Velopack's.</summary>
public sealed record LaunchArguments(string? Folder, bool NewWindow)
{
    public const string NewWindowFlag = "--new-window";

    public static LaunchArguments None { get; } = new(null, false);

    public static LaunchArguments Parse(IReadOnlyList<string> arguments)
    {
        string? folder = null;
        bool newWindow = false;
        foreach (string argument in arguments)
        {
            if (argument == NewWindowFlag)
            {
                newWindow = true;
            }
            else if (argument.StartsWith("--velo", StringComparison.Ordinal))
            {
                continue;
            }
            else if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new FormatException($"'{argument}' is no flag Wayfarer takes: WayfarerWanderer.App [FOLDER] [{NewWindowFlag}]");
            }
            else if (folder is not null)
            {
                throw new FormatException($"Wayfarer opens one folder, not both '{folder}' and '{argument}'");
            }
            else
            {
                folder = Path.GetFullPath(argument);
            }
        }
        if (folder is not null && newWindow)
        {
            throw new FormatException($"{NewWindowFlag} opens nothing, so it takes no folder ('{folder}')");
        }
        return new LaunchArguments(folder, newWindow);
    }
}
