using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell;
using Passenger.Text;
using Passenger.Vcs;
using WayfarerWanderer.App.Shell.Settings;
using WayfarerWanderer.App.Shell.Vcs;
using WayfarerWanderer.App.Shell.Welcome;

namespace WayfarerWanderer.Tests;

/// <summary>Wayfarer for the person's eyes and ears: High Contrast while the system asks for it, and every state told by a
/// shape, sign or letter beside its colour, in the theme's colours or the colour-blind safe palette, and sound cues.</summary>
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

    [Fact]
    public void EveryStateHasASignOrLetterOfItsOwnBesideItsColour()
    {
        ChangeMarkKind[] marks = Enum.GetValues<ChangeMarkKind>();
        Assert.Equal(marks.Length, marks.Select(DiffSigns.SignOf).Distinct().Count());
        ChangeKind[] changes = Enum.GetValues<ChangeKind>();
        Assert.Equal(changes.Length, changes.Select(ChangedFileRow.LetterOf).Distinct().Count());
    }

    [AvaloniaFact]
    public void TheColourBlindSafePaletteIsLaidOverTheThemeAndReadsOnItsGrounds()
    {
        var window = EditorApplication.CreateWindow(null);
        window.Show();
        string[] states = ["Test.Passed", "Test.Failed", "Vcs.Added", "Vcs.Modified", "Vcs.Conflict", "Vcs.File.Added", "Vcs.File.Unversioned"];
        Dictionary<string, uint> own = states.ToDictionary(t => t, t => Colour(t, ThemeVariant.Dark));
        try
        {
            window.SettingsKept.Update(s => s with { StatePalette = StatePalette.ColourBlindSafe });
            foreach (ThemeVariant variant in (ThemeVariant[])[ThemeVariant.Dark, ThemeVariant.Light])
            {
                uint passed = Colour("Test.Passed", variant);
                uint failed = Colour("Test.Failed", variant);
                // Passed in blue, failed in vermilion: told apart without red and green.
                Assert.True((passed & 0xFF) > ((passed >> 16) & 0xFF), $"{passed:X8} is no blue");
                Assert.True(((failed >> 16) & 0xFF) > (failed & 0xFF), $"{failed:X8} is no vermilion");
                // A mark at 3:1 on the panel it is drawn on, a file's name at 4.5:1.
                uint panel = Colour("Panel", variant);
                Assert.True(ThemeColours.Ratio(passed, panel) >= 3, $"{variant}: passed {ThemeColours.Ratio(passed, panel)}");
                Assert.True(ThemeColours.Ratio(Colour("Vcs.File.Added", variant), panel) >= 4.5);
                Assert.True(ThemeColours.Ratio(Colour("Vcs.Added", variant), Colour("Editor.Gutter", variant)) >= 3);
            }
            Assert.NotEqual(own["Vcs.Added"], Colour("Vcs.Added", ThemeVariant.Dark));

            // Under High Contrast it is laid again, at High Contrast's 7:1 for words.
            using (SystemContrast.StandIn(new ContrastAsked(true, true)))
            {
                uint panel = Colour("Panel", ThemeVariant.Dark);
                Assert.Equal(0xFF1A1A1Au, panel);
                Assert.True(ThemeColours.Ratio(Colour("Vcs.File.Modified", ThemeVariant.Dark), panel) >= 7);
            }

            window.SettingsKept.Update(s => s with { StatePalette = StatePalette.Theme });
            Assert.Equal(own, states.ToDictionary(t => t, t => Colour(t, ThemeVariant.Dark)));
        }
        finally
        {
            window.SettingsKept.Update(_ => new AppSettings());
            window.Close();
        }
    }

    [Fact]
    public void SoundCuesPlayAsSettingsSayAndEachIsAWavOfItsOwn()
    {
        AppSettings settings = new();
        Assert.False(SoundCues.Play(settings, screenReaderRuns: false));
        Assert.True(SoundCues.Play(settings, screenReaderRuns: true));
        Assert.True(SoundCues.Play(settings with { SoundCues = SoundCuesPlayed.Always }, false));
        Assert.False(SoundCues.Play(settings with { SoundCues = SoundCuesPlayed.Never }, true));
        Assert.False(SoundCues.IsOn(settings with { SoundOnBuildEnd = false }, SoundCue.BuildFailed));
        Assert.True(SoundCues.IsOn(settings, SoundCue.SaveRefused));

        string folder = Directory.CreateTempSubdirectory("wayfarer-cues-").FullName;
        try
        {
            Dictionary<SoundCue, byte[]> made = Enum.GetValues<SoundCue>().ToDictionary(c => c, c => File.ReadAllBytes(SoundCues.FileOf(c, folder)));
            foreach ((SoundCue cue, byte[] wav) in made)
            {
                Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
                Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
                // The data's size as the header gives it is what follows the header.
                Assert.Equal(wav.Length - 44, BitConverter.ToInt32(wav, 40));
                Assert.True(wav.Length > 44 + 1000, cue.ToString());
            }
            Assert.Equal(made.Count, made.Values.Select(Convert.ToBase64String).Distinct().Count());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public void TheCaretComingToABreakpointOrAnErrorCuesWhileAScreenReaderRuns()
    {
        var window = EditorApplication.CreateWindow(null);
        window.Show();
        try
        {
            window.OpenSource(Repo.At("tests/fixtures/jvm", "maven/app/src/main/java/example/Main.java"));
            window.Editor.GoTo(new TextPosition(3, 0));
            window.Actions.TryExecute(EditorActions.ToggleBreakpoint);
            TextSnapshot text = window.Editor.Document.Snapshot;
            window.Editor.SetMarkers([new Passenger.Editor.Marker(new TextRange(text.GetLine(4).Start, 3), Passenger.Editor.MarkerKind.Error, "wrong")]);

            // No screen reader: nothing plays.
            using (ScreenReaders.StandIn(false))
            {
                window.Editor.GoTo(new TextPosition(0, 0));
                window.Editor.GoTo(new TextPosition(3, 0));
                Assert.Empty(window.CuesPlayed);
            }
            using (ScreenReaders.StandIn(true))
            {
                window.Editor.GoTo(new TextPosition(0, 0));
                window.Editor.GoTo(new TextPosition(3, 0));
                window.Editor.GoTo(new TextPosition(3, 4));
                window.Editor.GoTo(new TextPosition(4, 0));
                Assert.Equal([SoundCue.BreakpointLine, SoundCue.ErrorLine], window.CuesPlayed);

                window.SettingsKept.Update(s => s with { SoundOnErrorLine = false });
                window.Editor.GoTo(new TextPosition(0, 0));
                window.Editor.GoTo(new TextPosition(4, 0));
                Assert.Equal(2, window.CuesPlayed.Count);
                window.SettingsKept.Update(s => s with { SoundCues = SoundCuesPlayed.Never });
                window.Editor.GoTo(new TextPosition(3, 0));
                Assert.Equal(2, window.CuesPlayed.Count);
            }
        }
        finally
        {
            window.SettingsKept.Update(_ => new AppSettings());
            window.Close();
        }
    }
}
