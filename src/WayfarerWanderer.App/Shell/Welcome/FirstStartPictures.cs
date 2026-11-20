// From Passenger 2044e49b src/Passenger.App/Welcome/FirstStartPictures.cs, MIT (notices/Passenger/LICENSE).
namespace WayfarerWanderer.App.Shell.Welcome;

/// <summary>The first start's pictures of Wayfarer in its own theme and each it carries, dark and light, carried in
/// Shell/Assets/FirstStart and taken by <c>tools/first-start-pictures.py</c>: what each file is named, where the app carries
/// it, and how wide a tile draws one.</summary>
public static class FirstStartPictures
{
    /// <summary>The widest a tile draws a picture, in the window's units.</summary>
    public const double Widest = 468;

    /// <summary>The largest screen scale a picture drawn at its widest stays sharp on: it holds this many pixels a unit.</summary>
    public const double Sharpest = 3;

    /// <summary>The file of a theme's picture, by the theme's name, in dark or light.</summary>
    public static string FileOf(string theme, bool dark) => $"{theme.ToLowerInvariant()}-{(dark ? "dark" : "light")}.webp";

    /// <summary>Where the app carries a theme's picture in dark or light.</summary>
    public static Uri Place(string theme, bool dark) => new($"avares://WayfarerWanderer.App/Shell/Assets/FirstStart/{FileOf(theme, dark)}");
}
