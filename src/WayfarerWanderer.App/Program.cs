using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Passenger.Core;
using Avalonia.Controls.ApplicationLifetimes;
using Passenger.Editor;
using Passenger.Text;

namespace WayfarerWanderer.App;

public partial class EditorApplication : Application
{
    public override void Initialize()
    {
        BackgroundWork.Install(work => Dispatcher.UIThread.Post(work));
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
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<EditorApplication>()
        .UseSkia().UseHarfBuzz();

    /// <summary>The product's name as its windows say it, and its app host's file name, which git's askpass and gpg are told to start.</summary>
    public const string Name = "Wayfarer Wanderer";

    public const string ProgramName = "WayfarerWanderer.App";

    [STAThread]
    private static int Main(string[] args)
    {
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

        // Passenger's whole shell, Velopack's hooks and its command line included, as Wayfarer Wanderer.
        return Passenger.App.PassengerShell.Run(Name, ProgramName, args);
    }
}
