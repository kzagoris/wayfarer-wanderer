using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Passenger.Editor;
using Passenger.Text;

namespace WayfarerWanderer.App;

public class EditorApplication : Application
{
    public override void Initialize()
    {
        FallbackFonts.Install(Passenger.App.PlatformFallback.Named, _ => { });
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
    }

    public static Window CreateWindow(TextDocument document) => new()
    {
        Title = "Wayfarer Wanderer",
        Content = new CodeEditor { Document = document, Keymap = DefaultKeymaps.ForPlatform() }
    };

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = CreateWindow(new TextDocument());

        base.OnFrameworkInitializationCompleted();
    }
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args) => AppBuilder.Configure<EditorApplication>()
        .UsePlatformDetect().UseHarfBuzz().StartWithClassicDesktopLifetime(args);
}
