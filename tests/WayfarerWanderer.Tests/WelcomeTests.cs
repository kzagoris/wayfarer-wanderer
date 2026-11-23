using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Passenger.Core;
using Passenger.Text;
using WayfarerWanderer.App.Shell.State;
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
        Repo.At("tests/fixtures/jvm", relative);

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
            // Wayfarer's own version, not the vendored libraries'.
            Assert.Equal("0.1.0", OwnVersion.Shown);
            Assert.Contains($"Version {OwnVersion.Shown}.", window.Welcome.Heading);
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
            Assert.Equal([Themes.OwnName, "Cubicle", ThemeSource.HighContrast], page.Looks.Select(l => l.Name));
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

    [AvaloniaFact]
    public void MenusShowTheirShortcutsAndEveryActionHasItsOwnName()
    {
        var window = EditorApplication.CreateWindow(null);
        window.Show();
        try
        {
            MenuItem file = MenuNamed(window, "File");
            MenuItem settings = file.Items.OfType<MenuItem>().Single(i => (string?)i.Tag == AppActions.ShowSettings);
            var expected = AppActions.Keymap().ShortcutsFor(AppActions.ShowSettings).First(s => s.Second is null).First;
            Assert.Equal(new KeyGesture(expected.Key, expected.Modifiers), settings.InputGesture);
            MenuItem toggle = MenuNamed(window, "Run").Items.OfType<MenuItem>().Single(i => (string?)i.Tag == EditorActions.ToggleBreakpoint);
            Assert.Equal(Key.F9, toggle.InputGesture?.Key);

            // Find Action and Settings' Keymap page list each action by a name no other action has.
            string[] titles = [.. window.Actions.Registry.Find("").Select(a => a.Title)];
            Assert.Empty(titles.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key));
            Assert.Contains("Debug Tool Window", titles);
        }
        finally
        {
            window.Close();
        }
    }

    private static MenuItem MenuNamed(EditorWindow window, string header) => window.MainMenuNamed(header);

    private static string[] Ids(MenuItem menu) => [.. menu.Items.Select(i => i is MenuItem item ? item.Tag as string ?? (string)item.Header! : "-")];

    [AvaloniaFact]
    public void MainMenuFollowsIdeasMenusAndGreysWhatCannotRun()
    {
        var window = EditorApplication.CreateWindow(null);
        window.Show();
        try
        {
            // The menus the actions name a place in, in IntelliJ IDEA's order; none empty.
            Assert.Equal(["File", "Edit", "View", "Navigate", "Code", "Refactor", "Build", "Run", "Tests", "Tools", "Git", "Help"],
                window.MainMenuEntries.Select(m => m.Title));
            // File: projects (Recent Projects among them, then Close Project), the file and how it is stored, settings, Exit;
            // split by separators.
            Assert.Equal([EditorActions.NewProject, EditorActions.OpenWorkspace, EditorActions.OpenSource, AppActions.CloneRepository,
                MainMenu.RecentProjects, AppActions.CloseSolution, "-", EditorActions.SaveSource, AppActions.FileEncoding, "Line Separators",
                AppActions.FileIndentation, "-", AppActions.ShowSettings,
                EditorActions.ProjectStructure, "-", AppActions.Exit], Ids(MenuNamed(window, "File")));
            // Nothing is open: Close Project is greyed and says why.
            MenuItem close = MenuNamed(window, "File").Items.OfType<MenuItem>().Single(i => (string?)i.Tag == AppActions.CloseSolution);
            Assert.Equal("Close Project", close.Header);
            Assert.False(close.IsEnabled);
            Assert.Equal(Refusals.NoFolder, ToolTip.GetTip(close));
            // View lists every tool window.
            MenuItem toolWindows = MenuNamed(window, "View").Items.OfType<MenuItem>().Single(i => (string?)i.Header == MainMenu.ToolWindows);
            Assert.Contains("Project", toolWindows.Items.OfType<MenuItem>().Select(i => (string?)i.Header));
            // Settings' Keymap page says where the menu lists an action.
            Assert.Equal("File", window.Actions.Registry[AppActions.CloseSolution]!.Menus.Single().Path);
            // The toolbar's widgets say nothing is open.
            Assert.Equal("No project", Assert.IsType<DockPanel>(window.ProjectWidget.Content).Children.OfType<TextBlock>().Single().Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CloseProjectLeavesTheFolderForTheWelcomeScreen()
    {
        string folder = Directory.CreateTempSubdirectory("wayfarer-close-").FullName;
        var window = EditorApplication.CreateWindow(null);
        window.Show();
        try
        {
            await window.OpenWorkspaceAsync(folder);
            window.UpdateToolbar();
            Assert.Equal(Path.GetFileName(folder), Assert.IsType<DockPanel>(window.ProjectWidget.Content).Children.OfType<TextBlock>().Single().Text);
            Assert.True(MenuNamed(window, "File").Items.OfType<MenuItem>().Single(i => (string?)i.Tag == AppActions.CloseSolution).IsEnabled);

            await window.CloseProjectAsync();
            Assert.Null(window.Session);
            Assert.True(window.Welcome.IsVisible);
            Assert.Equal("Wayfarer Wanderer", window.Title);
            Assert.Equal("No project", Assert.IsType<DockPanel>(window.ProjectWidget.Content).Children.OfType<TextBlock>().Single().Text);
            Assert.False(MenuNamed(window, "File").Items.OfType<MenuItem>().Single(i => (string?)i.Tag == AppActions.CloseSolution).IsEnabled);
        }
        finally
        {
            window.Close();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ProjectBadgeTakesTheFirstLettersOfItsWords() =>
        Assert.Equal(["WW", "TE", "A"], new[] { "wayfarer-wanderer", "test", "a" }.Select(EditorWindow.Badge));

    [Fact]
    public void TrustTakenBackInSettingsEndsTheRepositorysTrustAndKeepsOtherAnswers()
    {
        string repository = PathNames.Canonical(Directory.CreateTempSubdirectory("wayfarer-trust-").FullName);
        string project = Path.Combine(repository, "app");
        string other = PathNames.Canonical(Directory.CreateTempSubdirectory("wayfarer-other-").FullName);
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Directory.CreateDirectory(project);
        try
        {
            // Trusting the project trusts its repository, and that is what is kept.
            WorkspaceTrust.Trust(project);
            string keptFolder = EditorWindow.TrustKeptFor(project);
            Assert.True(PathNames.Same(repository, keptFolder));
            TrustState shown = new TrustState().With(keptFolder);

            // Another Wayfarer declined a folder after the page opened: Apply keeps that answer.
            TrustState keptNow = shown.Declined(other);
            TrustState applied = EditorWindow.PutInForce(keptNow, shown, shown.Forgotten(keptFolder));
            Assert.False(WorkspaceTrust.Trusts(project));
            Assert.Empty(applied.Trusted);
            Assert.Equal([other], applied.Untrusted);
        }
        finally
        {
            WorkspaceTrust.TakeBack(repository);
            Directory.Delete(repository, recursive: true);
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void RecentFoldersKeepTheNewestFirstAndWhichWasOpen()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // Two opened on a clock that has not moved: the second is kept as the later one.
        RecentFoldersState one = new RecentFoldersState().Opened("/work/one", "one", now);
        RecentFoldersState kept = one
            .Opened("/work/two", "two", one.Next(now, DateTimeOffset.MinValue))
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
        // A copy outside the repository, so the pictures name no branch of the repository they were taken in.
        string maven = Path.Combine(Directory.CreateTempSubdirectory("wayfarer-pictures-").FullName, "maven");
        CopyFolder(Fixture("maven"), maven);
        try
        {
            await window.OpenWorkspaceAsync(maven);
            window.TrustBanner.IsVisible = false;
            window.OpenSource(Path.Combine(maven, "app", "src", "main", "java", "example", "Main.java"));
            foreach (FirstStartView.Look look in FirstStartView.Candidates())
            {
                foreach (bool dark in (bool[])[true, false])
                {
                    window.SettingsKept.Update(s => s with { Look = look.Theme, Theme = dark ? ThemeChoice.Dark : ThemeChoice.Light });
                    window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                    // A theme's layout is laid as it comes: Build shows over it.
                    window.ToolDock.ShowBehind(ToolWindowIds.Build);
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
            Directory.Delete(Path.GetDirectoryName(maven)!, recursive: true);
        }
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string folder in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, folder)));
        }
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
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
