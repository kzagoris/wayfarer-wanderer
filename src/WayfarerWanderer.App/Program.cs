using Avalonia;
using Avalonia.Headless;
using Velopack;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Passenger.Core;
using Avalonia.Controls.ApplicationLifetimes;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App.Shell.Theme;

namespace WayfarerWanderer.App;

public partial class EditorApplication : Application
{
    public override void Initialize()
    {
        BackgroundWork.Install(work => Dispatcher.UIThread.Post(work));
        FallbackFonts.Install(PlatformFallback.Named, _ => { });
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
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<EditorApplication>()
        .UseSkia().UseHarfBuzz();

    /// <summary>The product's name as its windows and the shared libraries' messages say it, and its app host's file name,
    /// which git's askpass and gpg are told to start.</summary>
    public const string Name = "Wayfarer Wanderer";

    public const string ProgramName = "WayfarerWanderer.App";

    [STAThread]
    private static int Main(string[] args)
    {
        // Before anything reads the program's path or the state folder, so both are Wayfarer's.
        Product.Identify(Name, ProgramName);
        if (OperatingSystem.IsWindows())
        {
            VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false).Run();
        }
        AcceptanceOptions? acceptance;
        try
        {
            acceptance = AcceptanceOptions.Parse(args);
        }
        catch (FormatException error)
        {
            Console.Error.WriteLine($"wayfarer: {error.Message}");
            return 2;
        }
        if (acceptance is not null)
        {
            return AcceptanceRunner.Run(acceptance);
        }

        return BuildAvaloniaApp().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}
