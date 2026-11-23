// After Passenger 8a1df7e4 src/Passenger.App/Templates/NewProjectDialog.cs, MIT (notices/Passenger/LICENSE): its layout
// kept (families under a search at the left; names, where it goes and the family's choices at the right; the footer's
// problem, progress and buttons), dotnet new, packages, Godot and set-ups cut for Wayfarer's bundled JVM starters.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Passenger.Core;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>A kind of starter the New Project dialog lists at its left: its name, what it makes, its language when it has
/// one alone, and the build systems it is made with, the first offered first.</summary>
public sealed record StarterFamily(
    string Name,
    string Description,
    NewProjectKind Kind,
    IReadOnlyList<NewProjectLanguage> Languages,
    IReadOnlyList<NewProjectBuild> Builds)
{
    public static IReadOnlyList<StarterFamily> All { get; } =
    [
        new("Java", "A Java application with a main class.", NewProjectKind.Application,
            [NewProjectLanguage.Java], [NewProjectBuild.IntelliJ, NewProjectBuild.Maven, NewProjectBuild.Gradle]),
        new("Kotlin", "A Kotlin application with a main function and a JUnit test.", NewProjectKind.Application,
            [NewProjectLanguage.Kotlin], [NewProjectBuild.Gradle, NewProjectBuild.Maven]),
        // Spring Boot is not offered for now (docs/todo.md); ProjectGenerator still makes it (NewProjectKind.SpringBoot).
    ];

    public bool Matches(string[] words) =>
        words.All(word => Name.Contains(word, StringComparison.OrdinalIgnoreCase)
            || Description.Contains(word, StringComparison.OrdinalIgnoreCase)
            || Builds.Any(build => BuildName(build).Contains(word, StringComparison.OrdinalIgnoreCase)));

    public static string BuildName(NewProjectBuild build) => build switch
    {
        NewProjectBuild.IntelliJ => "IntelliJ",
        NewProjectBuild.Maven => "Maven",
        _ => "Gradle",
    };
}

/// <summary>A row of the New Project dialog's list: a heading (Recent, All templates), a family, or a recent starter (a
/// family made with a build system lately), which chooses its family and that build system.</summary>
public sealed record StarterRow(string? Heading, StarterFamily? Family = null, NewProjectBuild? Recent = null);

/// <summary>What a row of the New Project dialog's JDK drop-down is: a JDK, an action that finds one, or a heading.</summary>
public enum JdkEntryKind
{
    Jdk,
    Download,
    AddFromDisk,
    Header,
    Separator,
}

/// <summary>A row of the JDK drop-down: a JDK's home with its name, version and home, or an action, a heading or a rule.</summary>
public sealed record JdkEntry(JdkEntryKind Kind, string Text, string? Home = null, string? Detail = null)
{
    public bool IsJdk => Kind == JdkEntryKind.Jdk;
}

/// <summary>The New Project dialog, as IntelliJ's: the starter families down the left under a search; on the right the
/// project's name, where it goes, the family's language and build system, the JDK (a drop-down of the JDKs kept and
/// detected, with Download JDK… and Add JDK from Disk…), and under Advanced Settings the coordinates its build needs. Create refuses
/// what the generator would, saying why; else it writes the starter and closes with it (<see cref="Created"/>).</summary>
public sealed class NewProjectDialog : IdeWindow
{
    private const string DefaultName = "untitled";
    private const string DefaultGroup = "com.example";

    private readonly TextBox search = SearchBox.Of("Search templates");
    private readonly ListBox list;
    private readonly TextBlock nothing = new() { Classes = { StyleClasses.Muted }, Text = "Nothing matches", Margin = new Thickness(Gaps.Wide), IsVisible = false };
    private readonly TextBlock heading = new() { Classes = { StyleClasses.PageTitle } };
    private readonly TextBlock description = new() { Classes = { StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, Gaps.Tight, 0, 0) };
    private readonly TextBox projectName = new();
    private readonly TextBox directory = new();
    private readonly BrowseRow folderRow;
    private readonly TextBlock where = FormRows.Note();
    private readonly ComboBox language = new FormDropDown();
    private readonly Control languageRow;
    private readonly ComboBox build = new FormDropDown();
    private readonly TextBlock buildNote = FormRows.Note();
    private readonly ComboBox jdk = new FormDropDown { MaxDropDownHeight = 420 };
    private readonly Button advancedToggle = new()
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0, 2),
    };
    private readonly RowIcon advancedChevron = RowIcon.Chevron();
    private readonly StackPanel advanced = new() { Spacing = FormRows.Between, IsVisible = false };
    private readonly JdkRegistry registry;
    private List<JdkEntry> jdkEntries = [];
    private JdkEntry? jdkChosen;

    /// <summary>A home given the table lacks (JAVA_HOME's, or one set), listed at the top of the table's JDKs.</summary>
    private string? jdkOther;
    private readonly TextBox group = new();
    private readonly Control groupRow;
    private readonly TextBox artifact = new();
    private readonly TextBlock artifactLabel;
    private readonly TextBox package = new();
    private readonly TextBlock writes = FormRows.Note();
    private readonly ProgressBar bar = new() { IsIndeterminate = true, Width = 160 };
    private readonly TextBlock progress = new() { Classes = { StyleClasses.Muted }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel working;
    private readonly TextBlock problem = new() { Classes = { StyleClasses.Error }, TextWrapping = TextWrapping.Wrap, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button create = DialogFooter.Default("Create");

    /// <summary>The build system chosen last in each family, which choosing the family again opens on.</summary>
    private readonly Dictionary<string, NewProjectBuild> buildOf = new(StringComparer.Ordinal);

    private IReadOnlyList<StarterFamily> listed = StarterFamily.All;
    private IReadOnlyList<StarterRow> rows = [];
    private StarterFamily? family;
    private CancellationTokenSource? creating;

    /// <summary>The values the dialog gave last: one that differs is the person's, which a later change keeps.</summary>
    private string nameGiven = string.Empty;
    private string artifactGiven = string.Empty;
    private string packageGiven = string.Empty;

    /// <summary>Set while the dialog fills its lists, so their choice is not taken for the person's.</summary>
    private bool setting;

    /// <param name="folder">The directory the project's folder is made in.</param>
    /// <param name="jdkHome">The JDK chosen at first; JAVA_HOME's when null, else the table's first JDK new enough.</param>
    /// <param name="jdks">The JDK table the drop-down lists; <see cref="JdkRegistry.Current"/> when null.</param>
    /// <param name="recent">The starters made lately, newest first, by <see cref="KnownBy"/>: listed first, under Recent.</param>
    public NewProjectDialog(string folder, string? jdkHome = null, JdkRegistry? jdks = null, IReadOnlyList<string>? recent = null)
    {
        registry = jdks ?? JdkRegistry.Current;
        Recent = recent ?? [];
        FormRows.Scope(this);
        Title = "New Project";
        Width = DialogWidths.Workspace;
        Height = 600;
        MinWidth = 680;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        list = new ListBox
        {
            Classes = { StyleClasses.Tree },
            // The chosen family's band as wide as the search field above, its name in line with the field's magnifier.
            Padding = new Thickness(Gaps.Normal, 0),
        };
        ReusedRows.Use(list, Item);
        ListRow.Headings(list, item => item is StarterRow { Heading: not null });
        list.SelectionChanged += (_, _) =>
        {
            if (!setting && list.SelectedItem is StarterRow { Family: { } chosen } row)
            {
                ChooseFamily(chosen, row.Recent);
            }
        };
        search.TextChanged += (_, _) => Filter();
        ListKeys.Search(search, list, choosable: i => i < rows.Count && rows[i].Heading is null);
        search.Margin = new Thickness(Gaps.Normal, Gaps.Normal, Gaps.Normal, Gaps.Small);
        DockPanel leftPanel = new();
        DockPanel.SetDock(search, Dock.Top);
        leftPanel.Children.Add(search);
        leftPanel.Children.Add(new Panel { Children = { list, nothing } });
        Border left = new() { Width = 220, BorderThickness = Hairlines.After, Child = leftPanel };
        left.Bind(Border.BorderBrushProperty, left.GetResourceObservable(TokenKeys.Divider));

        directory.Text = folder;
        folderRow = BrowseRow.Of(directory, BrowseAsync);
        ReusedRows.Use(jdk, JdkRow);
        // Headings and the rule are shown, never chosen.
        jdk.ContainerPrepared += (_, e) =>
            e.Container.IsEnabled = e.Index >= jdkEntries.Count || jdkEntries[e.Index].Kind is not (JdkEntryKind.Header or JdkEntryKind.Separator);
        jdk.SelectionChanged += (_, _) => JdkSelected();
        FillJdks(jdkHome ?? Environment.GetEnvironmentVariable("JAVA_HOME"));
        group.Text = DefaultGroup;
        advancedToggle.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Gaps.Small,
            Children = { advancedChevron, new TextBlock { Text = "Advanced Settings", VerticalAlignment = VerticalAlignment.Center } },
        };
        advancedToggle.Click += (_, _) => AdvancedShown = !AdvancedShown;
        projectName.TextChanged += (_, _) => NameChanged();
        directory.TextChanged += (_, _) => ShowWhere();
        group.TextChanged += (_, _) => Derive();
        artifact.TextChanged += (_, _) => Derive();
        package.TextChanged += (_, _) => ShowWrites();
        language.SelectionChanged += (_, _) =>
        {
            if (!setting)
            {
                ShowChoice();
            }
        };
        build.SelectionChanged += (_, _) =>
        {
            if (!setting && family is not null && Build is { } chosen)
            {
                buildOf[family.Name] = chosen;
                ShowChoice();
            }
        };

        languageRow = Row("Language", language);
        groupRow = Row("Group", group);
        Grid artifactRow = FormRows.Of("Artifact", artifact, out artifactLabel);
        advanced.Children.Add(groupRow);
        advanced.Children.Add(artifactRow);
        advanced.Children.Add(Row("Package:", package));
        Control advancedRule = Rule();
        advancedRule.VerticalAlignment = VerticalAlignment.Center;
        advancedRule.Margin = new Thickness(Gaps.Normal, 0, 0, 0);
        Grid advancedHeader = new() { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) } };
        advancedHeader.Children.Add(advancedToggle);
        Grid.SetColumn(advancedRule, 1);
        advancedHeader.Children.Add(advancedRule);
        StackPanel right = new()
        {
            [DialogFooter.InsetProperty] = true,
            Spacing = FormRows.Between,
            Children =
            {
                new StackPanel { Children = { heading, description }, Margin = new Thickness(0, 0, 0, Gaps.Normal) },
                Row("Name", projectName),
                Row("Location", folderRow, [where]),
                Rule(),
                languageRow,
                Row("Build system", build, [buildNote]),
                Row("JDK", jdk, [writes]),
                advancedHeader,
                advanced,
            },
        };
        working = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gaps.Normal, IsVisible = false, Children = { bar, progress } };
        create.Click += (_, _) => IdeErrors.Observe(CreateAsync());
        Closing += (_, _) => creating?.Cancel();
        DockPanel.SetDock(left, Dock.Left);
        Content = DialogFooter.Page(
            new DockPanel { Children = { left, new ScrollViewer { Content = right } } },
            DialogFooter.OkCancel(create, Cancel, new Panel { Children = { problem, working } }));

        // A box's TextChanged comes a dispatcher pass after its text: what follows from the defaults is shown here.
        Set(projectName, ref nameGiven, Unique(DefaultName, name => !Exists(Path.Combine(Folder, name))));
        Filter();
        NameChanged();
        // The name has the keys as the dialog opens, its default chosen to be typed over; again once laid out.
        void FocusName()
        {
            projectName.Focus();
            projectName.SelectAll();
        }
        Opened += (_, _) =>
        {
            FocusName();
            Dispatcher.UIThread.Post(FocusName, DispatcherPriority.Loaded);
        };
    }

    /// <summary>The families the search finds.</summary>
    public IReadOnlyList<StarterFamily> Listed => listed;

    /// <summary>The family chosen.</summary>
    public StarterFamily? Family => family;

    /// <summary>The starters made lately, newest first, by <see cref="KnownBy"/>.</summary>
    public IReadOnlyList<string> Recent { get; }

    /// <summary>The list's rows as shown: Recent's heading and starters where any match, All templates' heading, the families.</summary>
    public IReadOnlyList<StarterRow> Rows => rows;

    /// <summary>The row chosen in the list.</summary>
    public StarterRow? ChosenRow => list.SelectedItem as StarterRow;

    /// <summary>What Create made from, by <see cref="KnownBy"/>, to keep among the recent ones; null before a family and
    /// build system are chosen.</summary>
    public string? Chosen => family is not null && Build is { } made ? KnownBy(family, made) : null;

    /// <summary>The name a starter is kept by among the recent ones: its family's and its build system's.</summary>
    public static string KnownBy(StarterFamily family, NewProjectBuild build) => $"{family.Name} {StarterFamily.BuildName(build)}".ToLowerInvariant();

    /// <summary>The search over the families.</summary>
    public string Query
    {
        get => search.Text ?? string.Empty;
        set
        {
            search.Text = value;
            Filter();
        }
    }

    public string ProjectName
    {
        get => projectName.Text?.Trim() ?? string.Empty;
        set
        {
            projectName.Text = value;
            NameChanged();
        }
    }

    /// <summary>The directory the project's folder is made in.</summary>
    public string Folder
    {
        get => directory.Text?.Trim() ?? string.Empty;
        set
        {
            directory.Text = value;
            ShowWhere();
        }
    }

    /// <summary>The home of the JDK chosen in the drop-down; empty while none is. A home set that the table lacks is
    /// listed at the top of its JDKs.</summary>
    public string JdkHome
    {
        get => jdkChosen?.Home ?? string.Empty;
        set => FillJdks(value);
    }

    /// <summary>The JDK drop-down's rows, as listed.</summary>
    public IReadOnlyList<JdkEntry> JdkEntries => jdkEntries;

    /// <summary>Whether Advanced Settings (the module name or group and artifact, and the package) is open.</summary>
    public bool AdvancedShown
    {
        get => advanced.IsVisible;
        set
        {
            advanced.IsVisible = value;
            advancedChevron.Turned = value;
        }
    }

    public string Group
    {
        get => group.Text?.Trim() ?? string.Empty;
        set
        {
            group.Text = value;
            Derive();
        }
    }

    public string Artifact
    {
        get => artifact.Text?.Trim() ?? string.Empty;
        set
        {
            artifact.Text = value;
            Derive();
        }
    }

    public string Package
    {
        get => package.Text?.Trim() ?? string.Empty;
        set
        {
            package.Text = value;
            ShowWrites();
        }
    }

    /// <summary>The languages the family chosen is made in.</summary>
    public IReadOnlyList<NewProjectLanguage> Languages => family?.Languages ?? [];

    public NewProjectLanguage? Language
    {
        get => family is null ? null : family.Languages.Count == 1 ? family.Languages[0]
            : language.SelectedIndex >= 0 ? family.Languages[language.SelectedIndex] : null;
        set => language.SelectedIndex = value is { } chosen && family is not null ? family.Languages.ToList().IndexOf(chosen) : -1;
    }

    /// <summary>The build systems the family chosen offers, in the order the dialog lists them.</summary>
    public IReadOnlyList<NewProjectBuild> Builds => family?.Builds ?? [];

    public NewProjectBuild? Build
    {
        get => family is not null && build.SelectedIndex >= 0 && build.SelectedIndex < family.Builds.Count ? family.Builds[build.SelectedIndex] : null;
        set => build.SelectedIndex = value is { } chosen && family is not null ? family.Builds.ToList().IndexOf(chosen) : -1;
    }

    /// <summary>Whether the Group and Artifact a Maven or Gradle build names are asked; an IntelliJ module asks its name.</summary>
    public bool AsksCoordinates => groupRow.IsVisible;

    /// <summary>What the dialog says under the build system chosen.</summary>
    public string BuildNote => buildNote.Text ?? string.Empty;

    /// <summary>Where Create puts the project, as the dialog says it.</summary>
    public string Where => where.Text ?? string.Empty;

    /// <summary>What Create writes, as the dialog says it.</summary>
    public string Writes => writes.Text ?? string.Empty;

    /// <summary>Why the last Create was refused or failed; null when none.</summary>
    public string? Problem => problem.IsVisible ? problem.Text : null;

    /// <summary>The Create button, for a click on it.</summary>
    public Button CreateButton => create;

    /// <summary>What Create made, once it closed the dialog; null until then, and when given up.</summary>
    public ProjectGenerationResult? Created { get; private set; }

    /// <summary>Chooses a family by its name, as a click on its row does; false when it is not listed.</summary>
    public bool Choose(string name)
    {
        int at = rows.ToList().FindIndex(r => r.Recent is null && r.Family?.Name == name);
        if (at < 0)
        {
            return false;
        }
        list.SelectedIndex = at;
        return true;
    }

    /// <summary>Chooses a recent starter by <see cref="KnownBy"/>, as a click on its row under Recent does; false when it is
    /// not listed.</summary>
    public bool ChooseRecent(string key)
    {
        int at = rows.ToList().FindIndex(r => r is { Family: { } f, Recent: { } b } && KnownBy(f, b) == key);
        if (at < 0)
        {
            return false;
        }
        list.SelectedIndex = at;
        return true;
    }

    /// <summary>The options Create hands the generator, as the dialog stands now.</summary>
    public ProjectGenerationOptions Options() => new()
    {
        ProjectName = ProjectName,
        Destination = Path.IsPathFullyQualified(Folder) && ProjectName.Length > 0 ? Path.Combine(Folder, ProjectName) : string.Empty,
        GroupId = Group,
        ArtifactId = Artifact,
        PackageName = Package,
        JdkHome = JdkHome,
        Kind = family?.Kind ?? NewProjectKind.Application,
        Language = Language ?? NewProjectLanguage.Java,
        Build = Build ?? NewProjectBuild.Maven,
        LaunchName = $"Run {ProjectName}",
    };

    /// <summary>Create, as its button does: the starter written, or why not, the dialog closing once it is made.</summary>
    public async Task CreateAsync()
    {
        if (creating is not null || family is null)
        {
            return;
        }
        if (WhyNot() is { } refused)
        {
            Fail(refused);
            return;
        }
        ProjectGenerationOptions options;
        try
        {
            options = ProjectGenerator.Validate(Options());
        }
        catch (ProjectGenerationException refusal)
        {
            Fail(refusal.Message);
            return;
        }
        using CancellationTokenSource cancel = new();
        creating = cancel;
        Fail(null);
        progress.Text = $"Creating {ProjectName} from the {family.Name} starter…";
        Busy(true);
        try
        {
            Created = await Task.Run(() => ProjectGenerator.GenerateAsync(options, cancel.Token), cancel.Token);
            creating = null;
            Close(Created);
        }
        catch (OperationCanceledException)
        {
            // Stopped: the generator removed its staging, and the destination is as it was.
        }
        catch (ProjectGenerationException failure)
        {
            Fail(failure.Message);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Fail($"{ProjectName} could not be created: {failure.Message}");
        }
        finally
        {
            creating = null;
            Busy(false);
        }
    }

    /// <summary>Cancel: stops a Create under way and closes the dialog.</summary>
    public void Cancel()
    {
        creating?.Cancel();
        Close(null);
    }

    /// <summary>Why Create would make nothing before the generator is asked: the name, then where it goes.</summary>
    private string? WhyNot()
    {
        if (ProjectName.Length == 0)
        {
            return "A project name is needed";
        }
        if (ProjectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ProjectName is "." or "..")
        {
            return $"{ProjectName} cannot name a folder";
        }
        if (Folder.Length == 0)
        {
            return "A location is needed";
        }
        if (!Path.IsPathFullyQualified(Folder))
        {
            return $"{Folder} is not a full path";
        }
        string root = Path.Combine(Folder, ProjectName);
        if (File.Exists(root))
        {
            return $"Project {ProjectName} cannot be created: {root} is a file";
        }
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            return $"Project {ProjectName} already exists in this location: {root} is not empty";
        }
        return null;
    }

    private const string RecentHeading = "Recent";

    private const string AllHeading = "All templates";

    /// <summary>Lists the starters made lately that the search finds under Recent, then the families it finds, keeping the
    /// row chosen where it still is, else taking the first of Recent at first, the first family after a search.</summary>
    private void Filter()
    {
        string[] words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        listed = [.. StarterFamily.All.Where(f => f.Matches(words))];
        List<StarterRow> shown = [];
        List<StarterRow> recent = [.. Recent.Select(RecentRow).OfType<StarterRow>().Where(r => r.Family!.Matches(words))];
        if (recent.Count > 0)
        {
            shown.Add(new StarterRow(RecentHeading));
            shown.AddRange(recent);
            shown.Add(new StarterRow(AllHeading));
        }
        int first = shown.Count;
        shown.AddRange(listed.Select(f => new StarterRow(null, f)));
        StarterRow? was = list.SelectedItem as StarterRow;
        int at = was is null ? -1 : shown.FindIndex(r => r.Heading is null && r.Family == was.Family && r.Recent == was.Recent);
        if (at < 0 && family is not null && Query.Length > 0)
        {
            at = shown.FindIndex(first, r => r.Family == family);
        }
        if (at < 0)
        {
            at = Query.Length == 0 && recent.Count > 0 ? 1 : listed.Count > 0 ? first : -1;
        }
        setting = true;
        rows = shown;
        list.ItemsSource = shown;
        list.SelectedIndex = at;
        setting = false;
        nothing.IsVisible = listed.Count == 0 && recent.Count == 0;
        ChooseFamily(at < 0 ? null : shown[at].Family, at < 0 ? null : shown[at].Recent);
    }

    /// <summary>A recent starter's row, by <see cref="KnownBy"/>; null for one no family offers now.</summary>
    private static StarterRow? RecentRow(string key) =>
        StarterFamily.All.SelectMany(f => f.Builds.Where(b => KnownBy(f, b) == key).Select(b => new StarterRow(null, f, b))).FirstOrDefault();

    /// <summary>A family chosen: its languages (the one chosen kept where it has it) and build systems (<paramref
    /// name="made"/> where a recent starter's row gives one, else the one chosen in it last, else its first).</summary>
    private void ChooseFamily(StarterFamily? chosen, NewProjectBuild? made = null)
    {
        if (ReferenceEquals(chosen, family))
        {
            if (made is { } again && Build != again)
            {
                Build = again;
            }
            return;
        }
        NewProjectLanguage? languageWas = Language;
        family = chosen;
        create.IsEnabled = chosen is not null && creating is null;
        if (chosen is null)
        {
            heading.Text = "New Project";
            description.Text = string.Empty;
            return;
        }
        setting = true;
        language.ItemsSource = chosen.Languages.Select(l => l.ToString()).ToList();
        language.SelectedIndex = languageWas is { } was && chosen.Languages.Contains(was) ? chosen.Languages.ToList().IndexOf(was) : 0;
        build.ItemsSource = chosen.Builds.Select(StarterFamily.BuildName).ToList();
        build.SelectedIndex = made is { } given ? chosen.Builds.ToList().IndexOf(given)
            : buildOf.TryGetValue(chosen.Name, out NewProjectBuild last) ? chosen.Builds.ToList().IndexOf(last) : 0;
        setting = false;
        heading.Text = chosen.Name;
        description.Text = chosen.Description;
        languageRow.IsVisible = chosen.Languages.Count > 1;
        ShowChoice();
    }

    /// <summary>The rows the language and build system chosen ask for, and what the dialog says of them.</summary>
    private void ShowChoice()
    {
        if (family is null)
        {
            return;
        }
        bool module = Build == NewProjectBuild.IntelliJ;
        groupRow.IsVisible = !module;
        artifactLabel.Text = module ? "Module name" : "Artifact";
        int? highest = Language is { } chosen ? ProjectGenerator.HighestJdk(chosen) : null;
        string jdks = highest is int top ? $"JDK {ProjectGenerator.MinimumJdk} to {top}" : $"JDK {ProjectGenerator.MinimumJdk} or newer";
        buildNote.Text = Build switch
        {
            NewProjectBuild.IntelliJ => $"A plain IntelliJ module: Wayfarer compiles src with the JDK's javac into out, with no build tool and nothing downloaded. Needs {jdks}.",
            NewProjectBuild.Maven => $"Maven {ProjectGenerator.MavenVersion} through its wrapper, which downloads Maven and the dependencies on the first build. Needs {jdks}.",
            NewProjectBuild.Gradle => $"Gradle {ProjectGenerator.GradleVersion} through its wrapper, which downloads Gradle and the dependencies on the first build. Needs {jdks}.",
            _ => string.Empty,
        };
        ShowWrites();
    }

    /// <summary>A name typed: the artifact, and the package from it, follow until the person types their own.</summary>
    private void NameChanged()
    {
        Derive();
        ShowWhere();
    }

    /// <summary>The artifact from the name and the package from the group and artifact, each while it is the dialog's.</summary>
    private void Derive()
    {
        if (Artifact.Length == 0 || Artifact == artifactGiven)
        {
            Set(artifact, ref artifactGiven, ArtifactOf(ProjectName));
        }
        if (Package.Length == 0 || Package == packageGiven)
        {
            Set(package, ref packageGiven, PackageOf(Group.Length > 0 ? Group : DefaultGroup, Artifact));
        }
        ShowWrites();
    }

    /// <summary>Where Create puts the project, said under the location.</summary>
    private void ShowWhere()
    {
        where.Text = Folder.Length > 0 && Path.IsPathFullyQualified(Folder) && ProjectName.Length > 0
            ? $"The project will be created in {Path.Combine(Folder, ProjectName)}"
            : string.Empty;
    }

    /// <summary>What Create writes for the starter chosen, said at the form's foot.</summary>
    private void ShowWrites()
    {
        if (family is null || Language is not { } chosen || Build is not { } made)
        {
            writes.Text = string.Empty;
            return;
        }
        string main = family.Kind == NewProjectKind.SpringBoot ? "Application" : "App";
        string source = $"{main}.{(chosen == NewProjectLanguage.Kotlin ? "kt" : "java")}";
        string packageName = Package.Length > 0 ? Package : "its package";
        writes.Text = made switch
        {
            NewProjectBuild.IntelliJ => $"Writes {source} in {packageName} under src, {Artifact}.iml and .idea, and a Run {ProjectName} configuration.",
            NewProjectBuild.Maven => $"Writes {source} and its test in {packageName}, pom.xml and the Maven wrapper, and a Run {ProjectName} configuration.",
            _ => $"Writes {source} and its test in {packageName}, build.gradle.kts and the Gradle wrapper, and a Run {ProjectName} configuration.",
        };
    }

    /// <summary>An artifact made from a name: lowercase letters, digits, '-' and '.', starting with a letter.</summary>
    internal static string ArtifactOf(string name)
    {
        System.Text.StringBuilder made = new();
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || (c is '.' && made.Length > 0))
            {
                made.Append(c);
            }
            else if (made.Length > 0 && made[^1] != '-')
            {
                made.Append('-');
            }
        }
        string artifact = made.ToString().Trim('-', '.');
        int start = 0;
        while (start < artifact.Length && !char.IsAsciiLetterLower(artifact[start]))
        {
            start++;
        }
        artifact = artifact[start..];
        return artifact.Length == 0 ? "project" : artifact;
    }

    /// <summary>A package made from a group and an artifact: the group, then the artifact's letters and digits.</summary>
    internal static string PackageOf(string group, string artifact)
    {
        string last = new([.. artifact.ToLowerInvariant().Where(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))]);
        if (last.Length == 0 || !char.IsAsciiLetterLower(last[0]))
        {
            last = "app" + last;
        }
        return $"{group}.{last}";
    }

    /// <summary>Puts the dialog's own value in a box; one with the keys keeps it chosen, so what is typed replaces it.</summary>
    private static void Set(TextBox box, ref string given, string text)
    {
        given = text;
        if (box.Text != text)
        {
            box.Text = text;
        }
        if (box.IsFocused)
        {
            box.SelectAll();
        }
    }

    /// <summary>A default name, or with the first number that makes it one nothing has (untitled, untitled1, untitled2).</summary>
    private static string Unique(string name, Func<string, bool> free)
    {
        if (free(name))
        {
            return name;
        }
        for (int n = 1; ; n++)
        {
            if (free($"{name}{n}"))
            {
                return $"{name}{n}";
            }
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            return Directory.Exists(path) || File.Exists(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private void Fail(string? why)
    {
        problem.Text = why;
        problem.IsVisible = why is not null;
    }

    private void Busy(bool busy)
    {
        working.IsVisible = busy;
        problem.IsVisible = !busy && problem.Text is not null;
        create.IsEnabled = !busy && family is not null;
        list.IsEnabled = !busy;
        foreach (Control field in (Control[])[search, projectName, directory, folderRow.Browse, language, build, jdk, advancedToggle, group, artifact, package])
        {
            field.IsEnabled = !busy;
        }
    }

    /// <summary>A row for whichever row the list hands its container: a heading's words; a family's name, its build systems
    /// on hover; a recent starter's family and build system.</summary>
    private static Control Item()
    {
        TextBlock name = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Border item = new() { Padding = new Thickness(Gaps.Tight, Gaps.Small), Child = name };
        TextBlock heading = ListRow.Heading();
        Panel shown = new() { Children = { heading, item } };
        return shown.Showing<StarterRow>(each =>
        {
            heading.IsVisible = each.Heading is not null;
            item.IsVisible = each.Family is not null;
            heading.Text = each.Heading;
            if (each is { Family: { } row, Recent: { } made })
            {
                name.Text = $"{row.Name} ({StarterFamily.BuildName(made)})";
                ToolTip.SetTip(item, $"{row.Name}: {StarterFamily.BuildName(made)}");
            }
            else if (each.Family is { } family)
            {
                name.Text = family.Name;
                ToolTip.SetTip(item, $"{family.Name}: {string.Join(", ", family.Builds.Select(StarterFamily.BuildName))}");
            }
        });
    }

    private Control Rule()
    {
        Border rule = new() { Height = 1, Margin = new Thickness(0, Gaps.Small) };
        rule.Bind(Border.BackgroundProperty, rule.GetResourceObservable(TokenKeys.Separator));
        return rule;
    }

    private static Control Row(string? label, Control editor, IReadOnlyList<TextBlock>? notes = null) => FormRows.Of(label, editor, notes ?? []);

    private async Task BrowseAsync()
    {
        if (await PickAsync("Select Project Location", Folder) is { } path)
        {
            Folder = path;
        }
    }

    /// <summary>Lists the JDK drop-down as IntelliJ's: Download JDK… and Add JDK from Disk…, then the JDKs the table
    /// keeps (with a home given that it lacks), then those it detected. Chooses the one at <paramref name="home"/>, else
    /// the first of JDK <see cref="ProjectGenerator.MinimumJdk"/> or newer, else the first.</summary>
    private void FillJdks(string? home)
    {
        IReadOnlyList<Jdk> all;
        try
        {
            all = registry.All();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            all = [];
        }
        home = home is { Length: > 0 } ? home.Trim() : null;
        // A home through a link (scoop's current) is the JDK the table lists at the link's target.
        if (home is not null && all.FirstOrDefault(j => PathNames.Same(j.Home, JdkRegistry.Resolved(home))) is { } linked)
        {
            home = linked.Home;
        }
        if (home is not null && !all.Any(j => PathNames.Same(j.Home, home)))
        {
            jdkOther = home;
        }
        List<JdkEntry> entries =
        [
            new(JdkEntryKind.Download, "Download JDK…"),
            new(JdkEntryKind.AddFromDisk, "Add JDK from Disk…"),
            new(JdkEntryKind.Separator, string.Empty),
        ];
        List<JdkEntry> kept = [.. all.Where(j => j.Origin != JdkOrigin.Detected).Select(Entry)];
        if (jdkOther is not null && !all.Any(j => PathNames.Same(j.Home, jdkOther)))
        {
            kept.Insert(0, new JdkEntry(JdkEntryKind.Jdk, jdkOther, jdkOther));
        }
        if (kept.Count > 0)
        {
            entries.Add(new(JdkEntryKind.Header, "JDKs"));
            entries.AddRange(kept);
        }
        List<JdkEntry> detected = [.. all.Where(j => j.Origin == JdkOrigin.Detected).Select(Entry)];
        if (detected.Count > 0)
        {
            entries.Add(new(JdkEntryKind.Header, "Detected JDKs"));
            entries.AddRange(detected);
        }
        JdkEntry? chosen = home is not null ? entries.FirstOrDefault(e => e.IsJdk && PathNames.Same(e.Home!, home)) : null;
        if (chosen is null && all.FirstOrDefault(j => j.Feature >= ProjectGenerator.MinimumJdk) is { } fit)
        {
            chosen = entries.First(e => e.IsJdk && PathNames.Same(e.Home!, fit.Home));
        }
        chosen ??= entries.FirstOrDefault(e => e.IsJdk);
        setting = true;
        jdkEntries = entries;
        jdk.ItemsSource = entries;
        jdk.SelectedItem = chosen;
        jdkChosen = chosen;
        setting = false;

        static JdkEntry Entry(Jdk j) => new(JdkEntryKind.Jdk, j.Name, j.Home, $"{j.Version}  {j.Home}");
    }

    /// <summary>A row chosen: a JDK is kept; an action puts the JDK chosen back and runs, choosing the JDK it finds.</summary>
    private void JdkSelected()
    {
        if (setting)
        {
            return;
        }
        JdkEntry? picked = jdk.SelectedItem as JdkEntry;
        if (picked is { IsJdk: true })
        {
            jdkChosen = picked;
            return;
        }
        setting = true;
        jdk.SelectedItem = jdkChosen;
        setting = false;
        if (picked?.Kind == JdkEntryKind.Download)
        {
            Dispatcher.UIThread.Post(() => IdeErrors.Observe(DownloadJdkAsync()));
        }
        else if (picked?.Kind == JdkEntryKind.AddFromDisk)
        {
            Dispatcher.UIThread.Post(() => IdeErrors.Observe(AddJdkFromDiskAsync()));
        }
    }

    /// <summary>Download JDK…: the Download JDK dialog (for the highest JDK the language takes, where it has one); the
    /// JDK it downloads is chosen.</summary>
    private async Task DownloadJdkAsync()
    {
        JdkRequirement? wanted = Language is { } chosen && ProjectGenerator.HighestJdk(chosen) is int highest
            ? new JdkRequirement(null, highest, null, JdkRequirement.Project)
            : null;
        if (await new JdkDownloadDialog(wanted).ShowDialog<Jdk?>(this) is { } downloaded)
        {
            FillJdks(downloaded.Home);
        }
    }

    /// <summary>Add JDK from Disk…: a folder kept in the JDK table, and chosen.</summary>
    private async Task AddJdkFromDiskAsync()
    {
        if (await PickAsync("Add JDK from Disk", JdkHome) is not { } home)
        {
            return;
        }
        try
        {
            FillJdks(registry.Add(home).Home);
            Fail(null);
        }
        catch (ArgumentException refused)
        {
            Fail(refused.Message);
        }
    }

    /// <summary>A row of the JDK drop-down, and the box's choice shown closed: a JDK's name with its version and home
    /// muted after it, an action, a muted heading, or a rule.</summary>
    private static Control JdkRow()
    {
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock detail = new()
        {
            Classes = { StyleClasses.Muted },
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Border rule = new() { Height = 1, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        rule.Bind(Border.BackgroundProperty, rule.GetResourceObservable(TokenKeys.Separator));
        DockPanel row = new();
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);
        row.Children.Add(detail);
        Panel panel = new() { Children = { row, rule } };
        return panel.Showing<JdkEntry>(entry =>
        {
            rule.IsVisible = entry.Kind == JdkEntryKind.Separator;
            row.IsVisible = !rule.IsVisible;
            name.Text = entry.Text;
            name.Classes.Set(StyleClasses.Muted, entry.Kind == JdkEntryKind.Header);
            detail.Text = entry.Detail ?? string.Empty;
            detail.IsVisible = entry.Detail is not null;
        });
    }

    private async Task<string?> PickAsync(string title, string from)
    {
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            SuggestedStartLocation = Directory.Exists(from) ? await StorageProvider.TryGetFolderFromPathAsync(from) : null,
        });
        return picked.Count == 1 ? picked[0].TryGetLocalPath() : null;
    }
}
