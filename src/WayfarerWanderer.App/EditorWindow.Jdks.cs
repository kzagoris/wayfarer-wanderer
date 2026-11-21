using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Passenger.Core;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>The missing-JDK banner, as IntelliJ shows a missing SDK: after every import whose model asks for a JDK no JDK
/// here meets, it names the first and how many more, with Choose… (pick from the JDK table, or add one from disk, then
/// import again) and Download…, which waits for JDK downloads.</summary>
public sealed partial class EditorWindow
{
    public Border JdkBanner { get; private set; } = null!;

    public TextBlock JdkBannerText { get; } = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    public Button ChooseJdkButton { get; } = new() { Content = "Choose…" };

    public Button DownloadJdkButton { get; } = new() { Content = "Download…", IsEnabled = false };

    /// <summary>What the banner's model asks for that no JDK meets, in the order the banner names them.</summary>
    public IReadOnlyList<MissingJdk> MissingJdkList { get; private set; } = [];

    /// <summary>Asks the person for a JDK for a missing requirement; the picker dialog unless a test sets another.</summary>
    public Func<MissingJdk, Task<Jdk?>> PickJdk { get; set; } = null!;

    private Border MissingJdkBanner()
    {
        PickJdk = missing => new JdkPickerDialog(missing).ShowDialog<Jdk?>(this);
        ChooseJdkButton.Click += (_, _) => UnawaitedWork.Observe(ChooseJdkAsync());
        ToolTip.SetTip(DownloadJdkButton, "Downloading JDKs is not available yet. Choose an installed JDK, or add one from disk.");
        ToolTip.SetShowOnDisabled(DownloadJdkButton, true);
        DockPanel.SetDock(ChooseJdkButton, Dock.Right);
        DockPanel.SetDock(DownloadJdkButton, Dock.Right);
        JdkBanner = new Border
        {
            IsVisible = false,
            Padding = new(8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { JdkBannerText, ChooseJdkButton, DownloadJdkButton },
            },
        };
        DockPanel.SetDock(JdkBanner, Dock.Top);
        return JdkBanner;
    }

    /// <summary>Shows the banner for the session's accepted model, or puts it away when nothing is missing.</summary>
    private void ShowMissingJdks()
    {
        MissingJdkList = Session is { IsTrusted: true } session ? MissingJdks.Of(session.Snapshot) : [];
        JdkBannerText.Text = MissingJdks.Say(MissingJdkList);
        JdkBanner.IsVisible = MissingJdkList.Count > 0;
    }

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
        MissingJdks.Choose(session.Root, missing, chosen, JdkRegistry.Current, ProjectJdkChoices.Current);
        try
        {
            await session.ImportAsync();
        }
        catch (OperationCanceledException)
        {
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
        List<Control> paragraphs = [DialogFooter.Paragraph(MissingJdks.Say([missing]).Replace(" No installed JDK matches.", string.Empty, StringComparison.Ordinal)), list, problem];
        DialogFooter.Question(this, "Choose JDK", paragraphs, DialogFooter.Of(choose, DialogFooter.Cancel(() => Close(null))), add);
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
