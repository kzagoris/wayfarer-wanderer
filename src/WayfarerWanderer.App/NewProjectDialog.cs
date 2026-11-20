// After Passenger 2044e49b src/Passenger.App/Templates/NewProjectDialog.cs, MIT (notices/Passenger/LICENSE): its layout
// kept (families under a search at the left; names, where it goes and the family's choices at the right; the footer's
// problem, progress and buttons), dotnet new, packages, Godot and set-ups cut for Wayfarer's bundled JVM starters.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
        new("Spring Boot", $"A Spring Boot {ProjectGenerator.SpringBootVersion} web service that answers on its root path, with a test.",
            NewProjectKind.SpringBoot, [NewProjectLanguage.Java, NewProjectLanguage.Kotlin], [NewProjectBuild.Maven, NewProjectBuild.Gradle]),
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

/// <summary>The New Project dialog: the starter families down the left under a search; on the right the project's name,
/// where it goes, the family's language and build system, the JDK, and the coordinates its build needs. Create refuses
/// what the generator would, saying why; else it writes the starter and closes with it (<see cref="Created"/>).</summary>
public sealed class NewProjectDialog : IdeWindow
{
    private const string DefaultName = "untitled";
    private const string DefaultGroup = "com.example";

    private readonly TextBox search = new() { Classes = { StyleClasses.Search }, PlaceholderText = "Search", Margin = new Thickness(8, 8, 8, 6) };
    private readonly ListBox list;
    private readonly TextBlock nothing = new() { Classes = { StyleClasses.Muted }, Text = "Nothing matches", Margin = new Thickness(12), IsVisible = false };
    private readonly TextBlock heading = new() { FontSize = 16, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock description = new() { Classes = { StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox projectName = new() { Classes = { StyleClasses.Search } };
    private readonly TextBox directory = new() { Classes = { StyleClasses.Search } };
    private readonly Button browse = new() { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock where = new() { Classes = { StyleClasses.Muted }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox language = new() { Classes = { StyleClasses.Form }, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Control languageRow;
    private readonly ComboBox build = new() { Classes = { StyleClasses.Form }, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock buildNote = new() { Classes = { StyleClasses.Muted }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox jdk = new() { Classes = { StyleClasses.Search } };
    private readonly Button browseJdk = new() { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBox group = new() { Classes = { StyleClasses.Search } };
    private readonly Control groupRow;
    private readonly TextBox artifact = new() { Classes = { StyleClasses.Search } };
    private readonly TextBlock artifactLabel;
    private readonly TextBox package = new() { Classes = { StyleClasses.Search } };
    private readonly TextBlock writes = new() { Classes = { StyleClasses.Muted }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar bar = new() { IsIndeterminate = true, Height = 4, MinHeight = 4, Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock progress = new() { Classes = { StyleClasses.Muted }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel working;
    private readonly TextBlock problem = new() { Classes = { StyleClasses.Error }, TextWrapping = TextWrapping.Wrap, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button create = DialogFooter.Default("Create");

    /// <summary>The build system chosen last in each family, which choosing the family again opens on.</summary>
    private readonly Dictionary<string, NewProjectBuild> buildOf = new(StringComparer.Ordinal);

    private IReadOnlyList<StarterFamily> listed = StarterFamily.All;
    private StarterFamily? family;
    private CancellationTokenSource? creating;

    /// <summary>The values the dialog gave last: one that differs is the person's, which a later change keeps.</summary>
    private string nameGiven = string.Empty;
    private string artifactGiven = string.Empty;
    private string packageGiven = string.Empty;

    /// <summary>Set while the dialog fills its lists, so their choice is not taken for the person's.</summary>
    private bool setting;

    /// <param name="folder">The directory the project's folder is made in.</param>
    /// <param name="jdkHome">The JDK offered; JAVA_HOME's when null.</param>
    public NewProjectDialog(string folder, string? jdkHome = null)
    {
        FormRows.Scope(this);
        Title = "New Project";
        Width = 860;
        Height = 600;
        MinWidth = 680;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        list = new ListBox
        {
            Classes = { StyleClasses.Tree },
            // The chosen family's band as wide as the search field above, its name where the field's text starts.
            Padding = new Thickness(8, 0),
        };
        ReusedRows.Use(list, Item);
        list.SelectionChanged += (_, _) =>
        {
            if (!setting && list.SelectedItem is StarterFamily chosen)
            {
                ChooseFamily(chosen);
            }
        };
        search.TextChanged += (_, _) => Filter();
        ListKeys.Search(search, list);
        DockPanel leftPanel = new();
        DockPanel.SetDock(search, Dock.Top);
        leftPanel.Children.Add(search);
        leftPanel.Children.Add(new Panel { Children = { list, nothing } });
        Border left = new() { Width = 220, BorderThickness = new Thickness(0, 0, 1, 0), Child = leftPanel };
        left.Bind(Border.BorderBrushProperty, left.GetResourceObservable(TokenKeys.Divider));

        directory.Text = folder;
        jdk.Text = jdkHome ?? Environment.GetEnvironmentVariable("JAVA_HOME") ?? string.Empty;
        group.Text = DefaultGroup;
        browse.Click += (_, _) => IdeErrors.Observe(BrowseAsync());
        browseJdk.Click += (_, _) => IdeErrors.Observe(BrowseJdkAsync());
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

        languageRow = Row("Language:", language);
        groupRow = Row("Group:", group);
        Grid artifactRow = FormRows.Of("Artifact:", artifact, out artifactLabel);
        StackPanel right = new()
        {
            Margin = new Thickness(24, 18, 24, 18),
            Spacing = 10,
            Children =
            {
                new StackPanel { Children = { heading, description }, Margin = new Thickness(0, 0, 0, 8) },
                Row("Name:", projectName),
                Row("Location:", BrowseRow.Of(directory, browse)),
                Row(null, where),
                Rule(),
                languageRow,
                Row("Build system:", build),
                Row(null, buildNote),
                Row("JDK:", BrowseRow.Of(jdk, browseJdk)),
                Rule(),
                groupRow,
                artifactRow,
                Row("Package:", package),
                Row(null, writes),
            },
        };
        working = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, IsVisible = false, Children = { bar, progress } };
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

    public string JdkHome
    {
        get => jdk.Text?.Trim() ?? string.Empty;
        set => jdk.Text = value;
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
        int at = listed.ToList().FindIndex(f => f.Name == name);
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

    /// <summary>Lists the families the search finds, keeping the one chosen where it still is, else taking the first.</summary>
    private void Filter()
    {
        string[] words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        listed = [.. StarterFamily.All.Where(f => f.Matches(words))];
        int at = family is null ? -1 : listed.ToList().IndexOf(family);
        if (at < 0 && listed.Count > 0)
        {
            at = 0;
        }
        setting = true;
        list.ItemsSource = listed;
        list.SelectedIndex = at;
        setting = false;
        nothing.IsVisible = listed.Count == 0;
        ChooseFamily(at < 0 ? null : listed[at]);
    }

    /// <summary>A family chosen: its languages (the one chosen kept where it has it) and build systems (the one chosen in
    /// it last, else its first).</summary>
    private void ChooseFamily(StarterFamily? chosen)
    {
        if (ReferenceEquals(chosen, family))
        {
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
        build.SelectedIndex = buildOf.TryGetValue(chosen.Name, out NewProjectBuild last) ? chosen.Builds.ToList().IndexOf(last) : 0;
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
        artifactLabel.Text = module ? "Module name:" : "Artifact:";
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
        foreach (Control field in (Control[])[search, projectName, directory, browse, language, build, jdk, browseJdk, group, artifact, package])
        {
            field.IsEnabled = !busy;
        }
    }

    /// <summary>A row for whichever family the list hands its container: its name, and its build systems on hover.</summary>
    private static Control Item()
    {
        TextBlock name = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Border item = new() { Padding = new Thickness(5, 6), Child = name };
        return item.Showing<StarterFamily>(row =>
        {
            name.Text = row.Name;
            ToolTip.SetTip(item, $"{row.Name}: {string.Join(", ", row.Builds.Select(StarterFamily.BuildName))}");
        });
    }

    private Control Rule()
    {
        Border rule = new() { Height = 1, Margin = new Thickness(0, 6) };
        rule.Bind(Border.BackgroundProperty, rule.GetResourceObservable(TokenKeys.Separator));
        return rule;
    }

    private static Control Row(string? label, Control editor) => FormRows.Of(label, editor);

    private async Task BrowseAsync()
    {
        if (await PickAsync("Select Project Location", Folder) is { } path)
        {
            Folder = path;
        }
    }

    private async Task BrowseJdkAsync()
    {
        if (await PickAsync("Select JDK", JdkHome) is { } path)
        {
            JdkHome = path;
        }
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
