using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Passenger.Core;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>The missing-JDK banner, as IntelliJ shows a missing SDK: after every import whose model asks for a JDK no JDK
/// here meets, it names the first and how many more, with Choose… (pick from the JDK table, or add one from disk) and
/// Download… (the Download JDK dialog), each followed by a new import. A second banner offers to download Temurin 21
/// when no JVM new enough to run Wayfarer's project host is found.</summary>
public sealed partial class EditorWindow
{
    public Border JdkBanner { get; private set; } = null!;

    public TextBlock JdkBannerText { get; } = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    public Button ChooseJdkButton { get; } = new() { Content = "Choose…" };

    public Button DownloadJdkButton { get; } = new() { Content = "Download…" };

    public Button ProjectStructureButton { get; } = new() { Content = "Project Structure…" };

    /// <summary>Shows Project Structure at a page and waits for it to close; the dialog unless a test sets another.</summary>
    public Func<ProjectStructureDialog, Task<bool>> ShowStructure { get; set; } = null!;

    /// <summary>Shown when no tooling JVM is found: Wayfarer cannot read the project until one is.</summary>
    public Border ToolingJvmBanner { get; private set; } = null!;

    public Button DownloadToolingJvmButton { get; } = new() { Content = "Download Temurin 21…" };

    public Button ChooseToolingJvmButton { get; } = new() { Content = "Choose…" };

    /// <summary>What the banner's model asks for that no JDK meets, in the order the banner names them.</summary>
    public IReadOnlyList<MissingJdk> MissingJdkList { get; private set; } = [];

    /// <summary>Asks the person for a JDK for a missing requirement; the picker dialog unless a test sets another.</summary>
    public Func<MissingJdk, Task<Jdk?>> PickJdk { get; set; } = null!;

    /// <summary>Downloads a JDK for a requirement, or null when the person cancels; the Download JDK dialog unless a test
    /// sets another.</summary>
    public Func<JdkRequirement, Task<Jdk?>> DownloadJdk { get; set; } = null!;

    private Control MissingJdkBanner()
    {
        PickJdk = missing => new JdkPickerDialog(missing).ShowDialog<Jdk?>(this);
        DownloadJdk = wanted => new JdkDownloadDialog(wanted).ShowDialog<Jdk?>(this);
        ChooseJdkButton.Click += (_, _) => UnawaitedWork.Observe(ChooseJdkAsync());
        DownloadJdkButton.Click += (_, _) => UnawaitedWork.Observe(DownloadMissingJdkAsync());
        ProjectStructureButton.Click += (_, _) => UnawaitedWork.Observe(ShowProjectStructureAsync(ProjectStructureDialog.ProblemsPage));
        ShowStructure = dialog => dialog.ShowDialog<bool>(this);
        JdkBanner = new Border
        {
            IsVisible = false,
            Padding = new(8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { JdkBannerText, ChooseJdkButton, DownloadJdkButton, ProjectStructureButton },
            },
        };
        DownloadToolingJvmButton.Click += (_, _) => UnawaitedWork.Observe(DownloadToolingJvmAsync());
        ChooseToolingJvmButton.Click += (_, _) => UnawaitedWork.Observe(ChooseToolingJvmAsync());
        ToolingJvmBanner = new Border
        {
            IsVisible = false,
            Padding = new(8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Wayfarer needs Java {ToolingJvms.Minimum} or later to read projects, and none was found.",
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    DownloadToolingJvmButton,
                    ChooseToolingJvmButton,
                },
            },
        };
        StackPanel banners = new() { Children = { ToolingJvmBanner, JdkBanner } };
        DockPanel.SetDock(banners, Dock.Top);
        return banners;
    }

    /// <summary>Shows the banner for the session's accepted model, or puts it away when nothing is missing.</summary>
    private void ShowMissingJdks()
    {
        MissingJdkList = Session is { IsTrusted: true } session
            ? [.. MissingJdks.Of(session.Snapshot), .. launchMissing is { } launch ? new[] { launch } : []]
            : [];
        JdkBannerText.Text = MissingJdks.Say(MissingJdkList);
        JdkBanner.IsVisible = MissingJdkList.Count > 0;
    }

    /// <summary>Whether an import failed for want of a tooling JVM: the tooling banner shows until one is found.</summary>
    private static bool ToolingJvmMissing(Exception failure) =>
        failure is ProjectHostFailed { InnerException: ToolingJvmMissingException };

    private void ShowToolingJvmMissing(bool missing) => ToolingJvmBanner.IsVisible = missing;

    /// <summary>Choose…: a JDK for the first missing requirement, put where that requirement looks, then the folder
    /// imported again through the session, as a changed build file would have it.</summary>
    public async Task ChooseJdkAsync()
    {
        if (Session is not { } session || MissingJdkList.Count == 0)
        {
            return;
        }
        MissingJdk missing = MissingJdkList[0];
        if (await PickJdk(missing) is not { } chosen || Session != session)
        {
            return;
        }
        await UseJdkAsync(session, missing, chosen);
    }

    /// <summary>Download…: the Download JDK dialog for the first missing requirement; the JDK it downloads is put where
    /// that requirement looks, as Choose… puts one, and the folder imported again.</summary>
    public async Task DownloadMissingJdkAsync()
    {
        if (Session is not { } session || MissingJdkList.Count == 0)
        {
            return;
        }
        MissingJdk missing = MissingJdkList[0];
        if (await DownloadJdk(missing.Requirement) is not { } downloaded || Session != session)
        {
            return;
        }
        await UseJdkAsync(session, missing, downloaded);
    }

    private async Task UseJdkAsync(ProjectSession session, MissingJdk missing, Jdk chosen)
    {
        MissingJdks.Choose(session.Root, missing, chosen, JdkRegistry.Current, ProjectJdkChoices.Current);
        if (missing.Requirement.Source == JdkRequirement.Launch)
        {
            // A run configuration's JDK: nothing the import reads changed.
            launchMissing = null;
            ShowMissingJdks();
            return;
        }
        await ImportAgainAsync(session);
    }

    private async Task ImportAgainAsync(ProjectSession session)
    {
        try
        {
            await session.ImportAsync();
            ShowToolingJvmMissing(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception failure) when (ToolingJvmMissing(failure))
        {
            ShowToolingJvmMissing(true);
            return;
        }
        if (Session != session)
        {
            return;
        }
        Explorer.Update(session.Snapshot);
        if (!session.IsStale && session.Snapshot is { } snapshot)
        {
            ReloadLanguageModels(snapshot);
        }
        ShowMissingJdks();
    }

    /// <summary>Project Structure, at <paramref name="page"/>; what it changed is imported again as it closes.</summary>
    public async Task ShowProjectStructureAsync(string? page = null)
    {
        if (Session is not { } session)
        {
            return;
        }
        ProjectStructureDialog dialog = new(session.Root, session.Snapshot, page) { OpenFile = OpenSource };
        if (await ShowStructure(dialog) && Session == session && session.IsTrusted)
        {
            await ImportAgainAsync(session);
        }
    }

    /// <summary>Download Temurin 21…: the JDK downloaded becomes the tooling JDK, and the folder is read again.</summary>
    public async Task DownloadToolingJvmAsync()
    {
        if (await DownloadJdk(ToolingJvms.RecoveryRequirement) is not { } downloaded)
        {
            return;
        }
        await UseToolingJvmAsync(downloaded);
    }

    /// <summary>Choose…: a JDK of the table new enough to run the host, or one added from disk.</summary>
    public async Task ChooseToolingJvmAsync()
    {
        MissingJdk tooling = new(null, new JdkRequirement(null, ToolingJvms.Minimum, null, JdkRequirement.Tooling));
        if (await PickJdk(tooling) is not { } chosen)
        {
            return;
        }
        await UseToolingJvmAsync(chosen.Origin == JdkOrigin.Detected ? JdkRegistry.Current.Add(chosen.Home) : chosen);
    }

    private async Task UseToolingJvmAsync(Jdk jdk)
    {
        JdkRegistry.Current.ToolingName = jdk.Name;
        ShowToolingJvmMissing(false);
        if (Session is { IsTrusted: true } session)
        {
            await ImportAgainAsync(session);
        }
    }
}

/// <summary>Choose JDK: the JDKs of the table that meet a missing requirement's feature, and Add JDK from Disk… for one
/// that is not there yet; closes with the JDK chosen, or null.</summary>
public sealed class JdkPickerDialog : IdeWindow
{
    private readonly MissingJdk missing;
    private readonly ListBox list = new() { Height = 220 };
    private readonly TextBlock problem = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };

    public JdkPickerDialog(MissingJdk missing)
    {
        this.missing = missing;
        Title = "Choose JDK";
        Width = 560;
        list.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(Row.Text));
        Button choose = DialogFooter.Default("Choose");
        choose.Click += (_, _) => Close(list.SelectedItem is Row row ? row.Jdk : null);
        list.DoubleTapped += (_, _) => Close(list.SelectedItem is Row row ? row.Jdk : null);
        Button add = new() { Content = "Add JDK from Disk…" };
        add.Click += (_, _) => UnawaitedWork.Observe(AddFromDiskAsync());
        Button download = new() { Content = "Download JDK…" };
        download.Click += (_, _) => UnawaitedWork.Observe(DownloadAsync());
        string heading = missing.Requirement.Source == JdkRequirement.Tooling
            ? $"Wayfarer's project host needs {missing.Requirement.Describe()}."
            : MissingJdks.Say([missing]).Replace(" No installed JDK matches.", string.Empty, StringComparison.Ordinal);
        List<Control> paragraphs = [DialogFooter.Paragraph(heading), list, problem];
        DialogFooter.Question(this, "Choose JDK", paragraphs, DialogFooter.Of(choose, DialogFooter.Cancel(() => Close(null))),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, download } });
        Fill(null);
    }

    private void Fill(Jdk? select)
    {
        Row[] rows = [.. MissingJdks.Candidates(missing.Requirement, JdkRegistry.Current.All()).Select(jdk => new Row(jdk))];
        list.ItemsSource = rows;
        list.SelectedItem = rows.FirstOrDefault(row => select is not null && PathNames.Same(row.Jdk.Home, select.Home)) ?? rows.FirstOrDefault();
        if (rows.Length == 0)
        {
            Say($"No JDK in the table meets {missing.Requirement.Describe()}. Add one from disk.");
        }
    }

    private async Task DownloadAsync()
    {
        if (await new JdkDownloadDialog(missing.Requirement).ShowDialog<Jdk?>(this) is { } downloaded)
        {
            Close(downloaded);
        }
    }

    private async Task AddFromDiskAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add JDK from Disk" });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } home)
        {
            return;
        }
        try
        {
            Jdk added = JdkRegistry.Current.Add(home);
            problem.IsVisible = false;
            Fill(added);
            if (missing.Requirement.Feature is int feature && added.Feature != feature)
            {
                Say($"{added.Name} is JDK {added.Feature}; this needs JDK {feature}. It was added to the JDK table.");
            }
        }
        catch (ArgumentException refused)
        {
            Say(refused.Message);
        }
    }

    private void Say(string text)
    {
        problem.Text = text;
        problem.IsVisible = true;
    }

    private sealed record Row(Jdk Jdk)
    {
        public string Text => $"{Jdk.Name}    {Jdk.Version}    {Jdk.Home}";
    }
}
