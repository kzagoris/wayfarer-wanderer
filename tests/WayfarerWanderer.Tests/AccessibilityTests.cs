using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Settings;
using WayfarerWanderer.App.Shell.Welcome;

namespace WayfarerWanderer.Tests;

/// <summary>Wayfarer for the person's eyes and ears: High Contrast while the system asks for it.</summary>
public class AccessibilityTests
{
    private static uint Colour(string token, ThemeVariant variant) =>
        ThemeResources.Colours(Application.Current!, variant, [token])![token];

    [Fact]
    public void TheSystemsContrastIsReadFromEachSystemsWords()
    {
        Assert.Equal(ContrastAsked.None, ContrastAsked.Parse("none"));
        Assert.Equal(new ContrastAsked(true, true), ContrastAsked.Parse("high-dark"));
        Assert.Equal(new ContrastAsked(true, false), ContrastAsked.Parse(" high-light "));
        Assert.Equal(new ContrastAsked(true, null), ContrastAsked.Parse("high"));
        Assert.Throws<FormatException>(() => ContrastAsked.Parse("loud"));
        // Windows' contrast theme: dark or light by its window colour (0x00BBGGRR).
        Assert.Equal(new ContrastAsked(true, true), ContrastAsked.OfWindows(true, 0x00000000));
        Assert.Equal(new ContrastAsked(true, false), ContrastAsked.OfWindows(true, 0x00FFFFFF));
        Assert.Equal(ContrastAsked.None, ContrastAsked.OfWindows(false, 0x00000000));
        Assert.Equal(new ContrastAsked(true, null), ContrastAsked.OfGnome("true\n"));
        Assert.Equal(ContrastAsked.None, ContrastAsked.OfGnome("false"));
        Assert.Contains(ThemeSource.HighContrast, ThemeSource.Carried);
    }

    [AvaloniaFact]
    public void HighContrastShowsWhileTheSystemAsksAndTheSettingsFollow()
    {
        var window = EditorApplication.CreateWindow(null);
        window.Show();
        Application app = Application.Current!;
        uint ownPanel = Colour("Panel", ThemeVariant.Dark);
        try
        {
            using (SystemContrast.StandIn(new ContrastAsked(true, true)))
            {
                Assert.True(Themes.InHighContrast);
                Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
                // The carried High Contrast is laid: its panel, not the own theme's.
                Assert.Equal(0xFF1A1A1Au, Colour("Panel", ThemeVariant.Dark));
                Assert.Equal(0xFFF5F5F5u, Colour("Panel", ThemeVariant.Light));
                // Settings says why the theme's row is voided.
                Assert.False(Declared.Look.Row!.When!.Holds(window.SettingsKept.Value));

                // Followed no more: the chosen theme comes back while the system still asks.
                window.SettingsKept.Update(s => s with { FollowSystemContrast = false });
                Assert.False(Themes.InHighContrast);
                Assert.Equal(ownPanel, Colour("Panel", ThemeVariant.Dark));
                Assert.True(Declared.Look.Row!.When!.Holds(window.SettingsKept.Value));
                window.SettingsKept.Update(s => s with { FollowSystemContrast = true });
                Assert.True(Themes.InHighContrast);

                // The first start says the system's high contrast shows meanwhile.
                FirstStartView page = window.ShowFirstStart();
                window.UpdateLayout();
                string said = string.Join("\n", page.GetVisualDescendantsOfType<Avalonia.Controls.TextBlock>().Select(t => t.Text));
                Assert.Contains("Your system asks for high contrast", said);
                page.ChooseLook(page.Looks[0]);
                page.ChooseVariant(ThemeChoice.Light);
            }
            Assert.False(Themes.InHighContrast);
            Assert.Equal(ownPanel, Colour("Panel", ThemeVariant.Dark));
        }
        finally
        {
            window.SettingsKept.Update(_ => new AppSettings());
            window.Close();
        }
    }
}
