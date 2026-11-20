using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Passenger.Text;
using SkiaSharp;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Settings;
using WayfarerWanderer.App.Shell.Welcome;

namespace WayfarerWanderer.Tests;

/// <summary>The window's start as Wayfarer's own: the Welcome screen while nothing is open, the first start's question of
/// the theme, Settings' pages, and the recent folders kept.</summary>
public class WelcomeTests
{
    private static string Fixture(string relative) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../tests/fixtures/jvm", relative));

    [AvaloniaFact]
    public void WelcomeShowsWhileNothingIsOpenAndSaysWayfarer()
    {
        var window = EditorApplication.CreateWindow(null);
        window.Show();
        try
        {
            Assert.True(window.Welcome.IsEffectivelyVisible);
            Assert.False(window.Editor.IsVisible);
            Assert.Contains("Welcome to Wayfarer Wanderer", window.Welcome.Heading);
            Assert.DoesNotContain("Passenger", window.Welcome.Heading);
            Assert.Equal("New Project", Words(window.Welcome.NewProjectButton).First());
            Assert.Equal("Open Folder", Words(window.Welcome.OpenFolderButton).First());

            window.OpenSource(Fixture("maven/app/src/main/java/example/Main.java"));
            Assert.False(window.Welcome.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task OpeningAFolderPutsTheWelcomeAway()
    {
        string folder = Directory.CreateTempSubdirectory("wayfarer-welcome-").FullName;
        var window = EditorApplication.CreateWindow(null);
        window.Show();
        try
        {
            await window.OpenWorkspaceAsync(folder);
            Assert.NotNull(window.Session);
            Assert.False(window.Welcome.IsVisible);
        }
        finally
        {
            window.Close();
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public void FirstStartAsksTheThemeInWayfarerAndKeepsTheAnswers()
    {
        var window = EditorApplication.CreateWindow(null);
        window.Show();
        try
        {
            FirstStartView page = window.ShowFirstStart();
            window.UpdateLayout();
            Assert.Equal(FirstStartView.Screen.Theme, page.Shown);
            Assert.Equal([Themes.OwnName, "Cubicle"], page.Looks.Select(l => l.Name));
            foreach (FirstStartView.Look look in page.Looks)
            {
                Assert.True(AssetLoader.Exists(look.Picture(dark: true)), look.Name);
                Assert.True(AssetLoader.Exists(look.Picture(dark: false)), look.Name);
            }
            string said = string.Join("\n", page.GetVisualDescendantsOfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("Welcome to Wayfarer Wanderer", said);
            Assert.DoesNotContain("Passenger", said);

            // The window under the page takes no keys: Find Action does not open.
            Assert.False(window.Welcome.IsEffectivelyEnabled);
            window.KeyPress(Key.A, (OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control) | RawInputModifiers.Shift, PhysicalKey.A, "A");
            Assert.NotNull(window.FirstStart);

            page.ChooseLook(page.Looks[1]);
            Assert.Equal("Cubicle", window.SettingsKept.Value.Look?.Name);
            Assert.Equal(FirstStartView.Screen.DarkOrLight, page.Shown);
            page.ChooseVariant(ThemeChoice.Dark);
            Assert.Null(window.FirstStart);
            Assert.Equal(ThemeChoice.Dark, window.SettingsKept.Value.Theme);
            Assert.True(window.Welcome.IsEffectivelyEnabled);
        }
        finally
        {
            window.SettingsKept.Update(_ => new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SettingsListsWayfarersPagesAndItsOwnTheme()
    {
        var window = EditorApplication.CreateWindow(null);
        window.Show();
        SettingsDialog dialog = window.ShowSettings();
        try
        {
            Assert.Equal([SettingsDialog.Appearance, SettingsDialog.SystemPage, SettingsDialog.EditorPage, SettingsDialog.KeymapPage], dialog.Pages);
            Assert.Contains(Themes.OwnName, dialog.ThemePicker.Listed);
            Assert.DoesNotContain("Passenger", dialog.ThemePicker.Listed);
            Assert.Contains("Reopen projects on startup", ShownOn(dialog, SettingsDialog.SystemPage));

            // The keymap page lists the window's own actions with their keys.
            dialog.Page = SettingsDialog.KeymapPage;
            Assert.Contains("F9", dialog.ShortcutsShown(EditorActions.ToggleBreakpoint).Single());
            dialog.Rebind(EditorActions.ToggleBreakpoint, "shift F9");
            dialog.Ok();
            Assert.Equal(EditorActions.ToggleBreakpoint, AppActions.Keymap().ActionFor(new Passenger.Editor.Shortcut(new Passenger.Editor.Keystroke(Key.F9, KeyModifiers.Shift))));
        }
        finally
        {
            dialog.Close();
            window.SettingsKept.Update(_ => new AppSettings());
            window.Close();
        }
    }

    [Fact]
    public void RecentFoldersKeepTheNewestFirstAndWhichWasOpen()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RecentFoldersState kept = new RecentFoldersState()
            .Opened("/work/one", "one", now)
            .Opened("/work/two", "two", now)
            .Closed("/work/one");
        Assert.Equal(["/work/two", "/work/one"], kept.Newest().Select(r => r.Path));
        Assert.Equal(["/work/two"], kept.StillOpen().Select(r => r.Path));
        Assert.Equal(["/work/one"], kept.Without("/work/two").Newest().Select(r => r.Path));

        Assert.Equal("Maven", RecentRow.Kind(Fixture("maven")));
        Assert.Equal("Gradle", RecentRow.Kind(Fixture("gradle")));
        Assert.Equal("Folder", RecentRow.Kind(Fixture("maven/app/src")));
        Assert.Equal("WW", WelcomeView.Initials("wayfarer-wanderer"));
    }

    public static bool TakePictures => Environment.GetEnvironmentVariable(PicturesVariable) is { Length: > 0 };

    private const string PicturesVariable = "WAYFARER_FIRST_START_PICTURES";

    /// <summary>The first start's pictures of Wayfarer in each theme it offers, dark and light: a Maven folder open with a Java
    /// file and Build showing, rendered at <see cref="FirstStartPictures.Sharpest"/> pixels a unit of a tile's widest and
    /// written as WebP where tools/first-start-pictures.py asks.</summary>
    [AvaloniaFact(SkipUnless = nameof(TakePictures), Skip = "Set WAYFARER_FIRST_START_PICTURES to a folder to take the first start's pictures.")]
    public async Task TakeFirstStartPictures()
    {
        string folder = Environment.GetEnvironmentVariable(PicturesVariable)!;
        Directory.CreateDirectory(folder);
        var window = EditorApplication.CreateWindow(null);
        // The window's units as wide as a tile's widest picture times the pixels it holds a unit; drawn at the window's own
        // scale, as the editor clips its lines to the window's pixels.
        const double Scale = 1;
        int width = (int)(FirstStartPictures.Widest * FirstStartPictures.Sharpest);
        int height = (int)Math.Round(width * 10 / 16.0);
        window.Width = width / Scale;
        window.Height = height / Scale;
        window.Show();
        try
        {
            await window.OpenWorkspaceAsync(Fixture("maven"));
            window.TrustBanner.IsVisible = false;
            window.OpenSource(Fixture("maven/app/src/main/java/example/Main.java"));
            window.ToolDock.ShowBehind(ToolWindowIds.Build);
            foreach (FirstStartView.Look look in FirstStartView.Candidates())
            {
                foreach (bool dark in (bool[])[true, false])
                {
                    window.SettingsKept.Update(s => s with { Look = look.Theme, Theme = dark ? ThemeChoice.Dark : ThemeChoice.Light });
                    window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                    // The code's colours come a moment after the theme changes: a frame drawn first lets them land.
                    for (int settle = 0; settle < 3; settle++)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        window.CaptureRenderedFrame()?.Dispose();
                        await Task.Delay(100);
                    }
                    using RenderTargetBitmap shot = new(new PixelSize(width, height), new Vector(96 * Scale, 96 * Scale));
                    shot.Render(window);
                    using MemoryStream png = new();
                    shot.Save(png, PngBitmapEncoderOptions.Default);
                    using SKBitmap pixels = SKBitmap.Decode(png.ToArray());
                    using SKData webp = pixels.Encode(SKEncodedImageFormat.Webp, 82);
                    string path = Path.Combine(folder, FirstStartPictures.FileOf(look.Name, dark));
                    File.WriteAllBytes(path, webp.ToArray());
                }
            }
        }
        finally
        {
            window.SettingsKept.Update(_ => new AppSettings());
            window.RequestedThemeVariant = null;
            window.Close();
        }
    }

    private static IEnumerable<string?> Words(Control control) => control.GetVisualDescendantsOfType<TextBlock>().Select(t => t.Text);

    private static IReadOnlyList<string> ShownOn(SettingsDialog dialog, string page)
    {
        dialog.Page = page;
        return dialog.Shown;
    }
}

internal static class VisualSearch
{
    public static IEnumerable<T> GetVisualDescendantsOfType<T>(this Visual root) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>();
}
