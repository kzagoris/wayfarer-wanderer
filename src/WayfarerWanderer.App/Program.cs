using Avalonia;
using Avalonia.Headless;
using Velopack;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Passenger.Core;
using Avalonia.Controls.ApplicationLifetimes;
using Passenger.Editor;
using Passenger.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.State;

namespace WayfarerWanderer.App;

public partial class EditorApplication : Application
{
    private static bool classHandlersAdded;

    /// <summary>The person's state, settings first, held while the desktop app runs; none for a headless run.</summary>
    public static StateStore? State { get; private set; }

    public override void Initialize()
    {
        BackgroundWork.Install(work => Dispatcher.UIThread.Post(work));
        AvaloniaXamlLoader.Load(this);
        // The shell's look as Passenger's App sets it up: the accent from the tokens, the fonts, the scale and the caret.
        Themes.TakeAccentFromTokens(this);
        FallbackFonts.Install(PlatformFallback.Named, FaceTracking.Set);
        CodeFonts.Install(PlatformWidths.Monospaced);
        CodeFonts.Use(this, CodeEditor.CodeFont);
        WindowFonts.Use(this, WindowFonts.Carried);
        InterfaceScale.Use(this, 1);
        Carets.UseWidth(this, Carets.DefaultWidth);
        if (classHandlersAdded)
        {
            return;
        }
        classHandlersAdded = true;
        InterfaceScale.Install();
        MenuShadow.Install();
        // In-window menus write keys as the keymap does (Shift+Ctrl+R as ⇧⌃R on macOS), not as Fluent writes a KeyGesture.
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<MenuItem>((item, e) =>
        {
            if (item.InputGesture is { } keys && e.NameScope.Find<TextBlock>("PART_InputGestureText") is { } shown)
            {
                shown.Text = new Keystroke(keys.Key, keys.KeyModifiers).Display();
            }
        });
    }

    public static EditorWindow CreateWindow(TextDocument? document) => new(document);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Only the person's desktop app reads and writes the state folder; a headless run keeps the defaults.
            IdeErrors.Install();
            State = StateStore.Open(StateStore.DefaultRoot);
            State.Settings.PutInForce();
            desktop.Exit += (_, _) => State.Release();
            desktop.MainWindow = CreateWindow(new TextDocument());
        }

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
