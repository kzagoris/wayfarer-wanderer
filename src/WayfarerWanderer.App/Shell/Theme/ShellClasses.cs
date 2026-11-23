namespace WayfarerWanderer.App.Shell;

/// <summary>Style classes Wayfarer's own Theme/Shell.axaml still takes that Passenger's StyleClasses no longer names:
/// StyleClasses is generated from Passenger's theme XAML, and Passenger v0.4.2 restyled these (a hero title, one badge
/// look) where Wayfarer's window, from an older Passenger (2044e49b), keeps them until its theme follows.</summary>
public static class ShellClasses
{
    public const string PopupTitle = "popupTitle";
    public const string TabPin = "tabPin";
    public const string ProjectBadge = "projectBadge";
    public const string ProjectBadgeText = "projectBadgeText";
}
