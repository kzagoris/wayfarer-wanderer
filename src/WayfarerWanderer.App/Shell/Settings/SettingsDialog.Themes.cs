// From Passenger 2044e49b src/Passenger.App/Settings/SettingsDialog.Themes.cs, MIT (notices/Passenger/LICENSE): the theme,
// dark or light and the code colour scheme; the theme sources and the VS Code themes explorer cut.
namespace WayfarerWanderer.App.Shell.Settings;

/// <summary>Appearance's themes: the theme and the code colour scheme, each chosen from those Wayfarer carries and any the
/// settings keep (<see cref="ThemePicker"/>), and dark, light or the system's between them.</summary>
public sealed partial class SettingsDialog
{
    /// <summary>The theme's drop-down.</summary>
    public ThemePicker ThemePicker { get; private set; } = null!;

    /// <summary>The code colour scheme's drop-down.</summary>
    public ThemePicker SchemePicker { get; private set; } = null!;

    private void AddThemes()
    {
        ThemePicker = new ThemePicker(Declared.Look, () => Working, Change);
        SchemePicker = new ThemePicker(Declared.ColourScheme, () => Working, Change);
        Add(Declared.Look, ThemePicker);
        Add(Declared.Theme);
        Add(Declared.ColourScheme, SchemePicker);
    }
}
