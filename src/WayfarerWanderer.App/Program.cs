using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Controls.ApplicationLifetimes;
using Passenger.Editor;
using Passenger.Text;

namespace WayfarerWanderer.App;

public partial class EditorApplication : Application
{
    public override void Initialize()
    {
        FallbackFonts.Install(Passenger.App.PlatformFallback.Named, _ => { });
        AvaloniaXamlLoader.Load(this);
    }

    public static EditorWindow CreateWindow(TextDocument? document) => new(document);

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
