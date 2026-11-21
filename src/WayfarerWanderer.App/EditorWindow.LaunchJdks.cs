using Avalonia.Controls;
using Passenger.Core;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>The JDK a run configuration runs on: Run and Debug take the configuration saved for the current main class,
/// a JDK it names that no JDK here meets shows the missing-JDK banner instead of starting, and Choose JDK for Run…
/// saves a JDK by name for it.</summary>
public sealed partial class EditorWindow
{
    /// <summary>The run configuration whose JDK is missing, shown in the banner beside the model's own.</summary>
    private MissingJdk? launchMissing;

    /// <summary>Asks for the JDK a run configuration runs on: a name, empty for the module's JDK, null to leave it; the
    /// dialog unless a test sets another.</summary>
    public Func<JvmLaunchConfiguration, Task<string?>> PickRunJdk { get; set; } = null!;

    private void AddLaunchJdkActions()
    {
        PickRunJdk = configuration => new RunJdkDialog(configuration).ShowDialog<string?>(this);
        Actions.Add(new EditorAction(EditorActions.ChooseRunJdk, "Choose JDK for Run…", () => Session is { IsTrusted: true } && CurrentLaunch() is not null,
            () => UnawaitedWork.Observe(ChooseRunJdkAsync())));
    }

    /// <summary>Run: the current main class on its saved configuration's JDK, or the banner when that JDK is missing.</summary>
    public async Task RunCurrentAsync()
    {
        if (Session is not { } session || await CurrentLaunchAsync() is not { } configuration)
        {
            return;
        }
        if (!LaunchJdkReady(session, configuration, out string? refused))
        {
            if (refused is not null)
            {
                BuildTool.Reset();
                BuildTool.Append(refused);
            }
            return;
        }
        await session.RunAsync(configuration);
    }

    private async Task DebugAsync(ProjectSession session, JvmLaunchConfiguration configuration)
    {
        JvmLaunchConfiguration chosen = await CurrentLaunchAsync() ?? configuration;
        if (!LaunchJdkReady(session, chosen, out string? refused))
        {
            if (refused is not null)
            {
                DebugTool.Status = refused;
            }
            return;
        }
        await session.DebugAsync(chosen, Breakpoints.Snapshot());
    }

    /// <summary>Whether a configuration's JDK can be launched on: a missing one shows the banner, one too old for the
    /// module says so in <paramref name="refused"/>.</summary>
    private bool LaunchJdkReady(ProjectSession session, JvmLaunchConfiguration configuration, out string? refused)
    {
        refused = null;
        launchMissing = null;
        if (configuration.Jdk.Length == 0 || session.Snapshot?.Module(configuration.Module) is not { } module)
        {
            ShowMissingJdks();
            return true;
        }
        try
        {
            LaunchJdks.Home(module, configuration, JdkRegistry.Current);
            ShowMissingJdks();
            return true;
        }
        catch (LaunchJdkMissingException missing)
        {
            launchMissing = new MissingJdk(missing.Configuration, missing.Requirement);
            ShowMissingJdks();
            return false;
        }
        catch (InvalidDataException tooOld)
        {
            refused = tooOld.Message;
            ShowMissingJdks();
            return false;
        }
    }

    /// <summary>Choose JDK for Run…: a JDK by name (or the module's) saved in the current main class's configuration.</summary>
    public async Task ChooseRunJdkAsync()
    {
        if (Session is not { } session || await CurrentLaunchAsync() is not { } configuration || await PickRunJdk(configuration) is not { } name)
        {
            return;
        }
        string main = configuration.MainClass[(configuration.MainClass.LastIndexOf('.') + 1)..];
        await session.Launches.SaveAsync(configuration with
        {
            Name = configuration.Name is "Current" or "" ? main : configuration.Name,
            Jdk = name,
            JavaHome = string.Empty,
        });
        launchMissing = null;
        ShowMissingJdks();
    }
}

/// <summary>Choose JDK for Run: the module's own JDK, or one of the JDK table by name; closes with the name (empty for
/// the module's), or null.</summary>
public sealed class RunJdkDialog : IdeWindow
{
    public const string ModulesJdk = "The module's JDK";

    public RunJdkDialog(JvmLaunchConfiguration configuration)
    {
        Title = "Choose JDK for Run";
        Width = 520;
        List<string> names = [ModulesJdk, .. JdkRegistry.Current.All().Select(jdk => jdk.Name)];
        ListBox list = new() { ItemsSource = names, Height = 220 };
        list.SelectedItem = configuration.Jdk.Length > 0 && names.Contains(configuration.Jdk) ? configuration.Jdk : ModulesJdk;
        string Chosen() => list.SelectedItem as string is { } name && name != ModulesJdk ? name : string.Empty;
        Button choose = DialogFooter.Default("Choose");
        choose.Click += (_, _) => Close(Chosen());
        list.DoubleTapped += (_, _) => Close(Chosen());
        DialogFooter.Question(this, Title,
            [DialogFooter.Paragraph($"The JDK {configuration.MainClass} runs and debugs on. A JDK chosen by name travels with the run configuration; another machine finds one of the same release."), list],
            DialogFooter.Of(choose, DialogFooter.Cancel(() => Close(null))));
    }
}
