using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Passenger.Core;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>
/// Project Structure, as IntelliJ lays it out: the project (its JDK, language level and, for an IntelliJ project, its
/// output folder), the modules (sources, paths, JDK and dependencies; editable for an IntelliJ project, read-only with
/// where each value came from for Maven or Gradle), the project libraries (IntelliJ projects), the JDK table (add,
/// download, rename, remove, locate a missing home, choose the tooling JDK) and the problems, each with its fix.
/// What it changes is written at once; <see cref="Changed"/> says the folder needs importing again when it closes.
/// </summary>
public sealed partial class ProjectStructureDialog : IdeWindow
{
    public const string ProjectPage = "Project";
    public const string ModulesPage = "Modules";
    public const string LibrariesPage = "Libraries";
    public const string JdksPage = "JDKs";
    public const string ProblemsPage = "Problems";

    private readonly string root;
    private readonly WorkspaceSnapshot? snapshot;
    private readonly ListBox pages = new() { Classes = { StyleClasses.Tree, StyleClasses.Pages } };
    private readonly TextBlock heading = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 16, 14) };
    private readonly StackPanel body = new() { Spacing = 10, Margin = new Thickness(0, 0, 16, 16) };
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false, Foreground = Brushes.IndianRed };
    private string? selectedModule;
    private string? selectedJdk;

    public ProjectStructureDialog(string root, WorkspaceSnapshot? snapshot, string? page = null)
    {
        this.root = Path.GetFullPath(root);
        this.snapshot = snapshot;
        Title = "Project Structure";
        Width = 920;
        Height = 640;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        List<string> names = [ProjectPage, ModulesPage];
        if (IsIdea)
        {
            names.Add(LibrariesPage);
        }
        names.AddRange([JdksPage, ProblemsPage]);
        pages.ItemsSource = names;
        pages.SelectionChanged += (_, _) => ShowPage();
        Border left = new() { Width = 200, BorderThickness = new Thickness(0, 0, 1, 0), Child = pages };
        left.Bind(Border.BorderBrushProperty, left.GetResourceObservable(TokenKeys.Divider));
        DockPanel right = new() { Margin = new Thickness(20, 16, 0, 0) };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(problem, Dock.Bottom);
        right.Children.Add(heading);
        right.Children.Add(problem);
        right.Children.Add(new ScrollViewer { Content = body });
        DockPanel.SetDock(left, Dock.Left);
        Button close = DialogFooter.Default("Close");
        close.Click += (_, _) => Close(Changed);
        Content = DialogFooter.Page(new DockPanel { Children = { left, right } }, DialogFooter.Bar(DialogFooter.Listed(close)));
        Page = page is not null && names.Contains(page) ? page : ProjectPage;
    }

    /// <summary>Whether something the import reads changed: the folder is imported again when the dialog closes.</summary>
    public bool Changed { get; private set; }

    /// <summary>Asks the person for a line of text (a name, coordinates, a path variable's value); a test sets another.</summary>
    public Func<string, string, string, Task<string?>> Ask { get; set; } = null!;

    /// <summary>Asks the person to agree before a removal; a test sets another.</summary>
    public Func<string, string, Control?, Task<bool>> Confirm { get; set; } = null!;

    /// <summary>Opens a file in the window (a build file); none when the dialog stands alone.</summary>
    public Action<string>? OpenFile { get; set; }

    public bool IsIdea => snapshot?.BuildSystem == JvmBuildSystem.Idea || (snapshot is null && Directory.Exists(Path.Combine(root, ".idea")));

    public IReadOnlyList<string> Pages => pages.ItemsSource as IReadOnlyList<string> ?? [];

    public string? Page
    {
        get => pages.SelectedItem as string;
        set => pages.SelectedItem = value;
    }

    /// <summary>The words the page shows now, for tests and the IDE's log.</summary>
    public IReadOnlyList<string> Shown => [.. Texts(body)];

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Ask ??= (title, label, value) => new AskDialog(title, label, value).ShowDialog<string?>(this);
        Confirm ??= (title, text, detail) => new ConfirmDialog(title, text, "Remove", detail: detail).ShowDialog<bool>(this);
    }

    private void ShowPage()
    {
        problem.IsVisible = false;
        heading.Text = Page;
        body.Children.Clear();
        switch (Page)
        {
            case ProjectPage:
                ShowProject();
                break;
            case ModulesPage:
                ShowModules();
                break;
            case LibrariesPage:
                ShowLibraries();
                break;
            case JdksPage:
                ShowJdks();
                break;
            case ProblemsPage:
                ShowProblems();
                break;
        }
    }

    // ---- Project ----

    private void ShowProject()
    {
        body.Children.Add(FormRows.Of("Name:", new TextBlock { Text = Path.GetFileName(root), VerticalAlignment = VerticalAlignment.Center }));
        body.Children.Add(FormRows.Of("Build:", new TextBlock { Text = snapshot?.BuildSystem.ToString() ?? "not imported", VerticalAlignment = VerticalAlignment.Center }));
        List<Jdk> jdks = [.. JdkRegistry.Current.All()];
        ComboBox jdk = new() { MinWidth = 280, ItemsSource = jdks.Select(j => j.Name).ToList(), SelectedItem = ProjectJdkName(), [Avalonia.Automation.AutomationProperties.NameProperty] = "Project JDK" };
        jdk.SelectionChanged += (_, _) =>
        {
            if (jdk.SelectedItem is string name && name != ProjectJdkName())
            {
                Try(() => ChooseProjectJdk(name));
            }
        };
        Button download = new() { Content = "Download JDK…" };
        download.Click += (_, _) => UnawaitedWork.Observe(DownloadJdkAsync(null));
        Button add = new() { Content = "Add JDK from Disk…" };
        add.Click += (_, _) => UnawaitedWork.Observe(AddJdkFromDiskAsync());
        body.Children.Add(FormRows.Of("JDK:", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { jdk, download, add } }));
        if (!IsIdea)
        {
            body.Children.Add(DialogFooter.Paragraph("The JDK chosen here is this machine's choice for the project. The project's own requirement (.wayfarer/project.json) and its build's toolchains are left as they are."));
        }
        int? level = snapshot?.Modules.FirstOrDefault()?.CompileToolchain.LanguageVersion;
        if (IsIdea)
        {
            TextBox levelBox = new() { Text = level?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, Width = 80 };
            levelBox.LostFocus += (_, _) =>
            {
                if (int.TryParse(levelBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value != level && ProjectJdkName() is { } name)
                {
                    Try(() => SetLanguageLevel(name, value));
                }
            };
            body.Children.Add(FormRows.Of("Language level:", levelBox));
            TextBox output = new() { Text = snapshot?.OutputRoot ?? Path.Combine(root, "out"), MinWidth = 360 };
            output.LostFocus += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(output.Text) && !PathNames.Same(Path.GetFullPath(output.Text), snapshot?.OutputRoot ?? Path.Combine(root, "out")))
                {
                    Try(() =>
                    {
                        IdeaProjectWriter.SetOutput(root, output.Text);
                        Changed = true;
                    });
                }
            };
            body.Children.Add(FormRows.Of("Output folder:", output));
        }
        else
        {
            body.Children.Add(FormRows.Of("Language level:", new TextBlock { Text = level?.ToString(CultureInfo.InvariantCulture) ?? "as the build says", VerticalAlignment = VerticalAlignment.Center }));
        }
    }

    /// <summary>The project's JDK by name: an IntelliJ project's <c>project-jdk-name</c>; for another, this machine's
    /// choice, else the requirement's name, else the name of the JDK it resolved to.</summary>
    public string? ProjectJdkName()
    {
        if (IsIdea)
        {
            return MiscAttribute("project-jdk-name");
        }
        return ProjectJdkChoices.Current.Chosen(root) ?? ProjectJdk.Requirement(root)?.Name ?? ProjectJdk.Resolve(root)?.Name;
    }

    /// <summary>Chooses the project's JDK: written into an IntelliJ project's misc.xml, else kept as this machine's
    /// choice for the project.</summary>
    public void ChooseProjectJdk(string name)
    {
        if (IsIdea)
        {
            IdeaProjectWriter.SetProjectJdk(root, name);
        }
        else
        {
            ProjectJdkChoices.Current.Choose(root, name);
        }
        Changed = true;
    }

    public void SetLanguageLevel(string jdkName, int level)
    {
        IdeaProjectWriter.SetProjectJdk(root, jdkName, level);
        Changed = true;
    }

    private string? MiscAttribute(string name)
    {
        string misc = Path.Combine(root, ".idea", "misc.xml");
        if (!File.Exists(misc))
        {
            return null;
        }
        try
        {
            XmlDocument document = new() { XmlResolver = null };
            using XmlReader reader = XmlReader.Create(misc, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document.Load(reader);
            return document.GetElementsByTagName("component").OfType<XmlElement>()
                .FirstOrDefault(c => c.GetAttribute("name") == "ProjectRootManager")?.GetAttribute(name) is { Length: > 0 } value ? value : null;
        }
        catch (Exception unreadable) when (unreadable is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- Modules ----

    private void ShowModules()
    {
        if (snapshot is null)
        {
            body.Children.Add(DialogFooter.Paragraph("The project has not been imported yet."));
            return;
        }
        ListBox modules = new() { ItemsSource = snapshot.Modules.Select(m => m.Id).ToList(), MaxHeight = 140, [Avalonia.Automation.AutomationProperties.NameProperty] = "Modules" };
        modules.SelectedItem = selectedModule is not null && snapshot.Module(selectedModule) is not null ? selectedModule : snapshot.Modules.FirstOrDefault()?.Id;
        modules.SelectionChanged += (_, _) =>
        {
            selectedModule = modules.SelectedItem as string;
            ShowPage();
        };
        body.Children.Add(modules);
        if (modules.SelectedItem is not string id || snapshot.Module(id) is not { } module)
        {
            return;
        }
        selectedModule = id;
        body.Children.Add(Section("Sources"));
        foreach (JvmSourceRoot source in module.SourceRoots)
        {
            body.Children.Add(Line($"{Relative(source.Path)}    {source.Kind}"));
        }
        body.Children.Add(Section("Paths"));
        foreach (JvmSourceSet set in module.SourceSets)
        {
            body.Children.Add(Line($"{set.Name}: {string.Join(", ", set.Outputs.Select(Relative))}"));
        }
        body.Children.Add(Section("JDK"));
        JvmToolchain jdk = module.CompileToolchain;
        string asked = jdk.Requirement is { } requirement ? $", asked for by {requirement.Source}: {requirement.Describe()}" : string.Empty;
        body.Children.Add(Line(jdk.Home is null ? $"missing{asked}" : $"{jdk.Home} (language level {jdk.LanguageVersion}){asked}"));
        body.Children.Add(Section("Dependencies"));
        if (IsIdea)
        {
            ShowIdeaDependencies(module);
            return;
        }
        body.Children.Add(DialogFooter.Paragraph($"From {Path.GetFileName(snapshot.BuildFile)}. Change dependencies in the build file; the project is imported again when it changes."));
        Button open = new() { Content = "Open Build File" };
        open.Click += (_, _) =>
        {
            OpenFile?.Invoke(snapshot.BuildFile);
            Close(Changed);
        };
        body.Children.Add(open);
        foreach (JvmClasspathEntry entry in module.SourceSet("main")?.CompileClasspath ?? [])
        {
            body.Children.Add(Line($"{entry.Coordinates ?? entry.ModuleRef ?? Path.GetFileName(entry.Path)}    compile"));
        }
        foreach (JvmClasspathEntry entry in (module.SourceSet("main")?.RuntimeClasspath ?? []).Where(e => module.SourceSet("main")!.CompileClasspath.All(c => c.Path != e.Path) && e.ModuleRef != module.Id + ":main"))
        {
            body.Children.Add(Line($"{entry.Coordinates ?? entry.ModuleRef ?? Path.GetFileName(entry.Path)}    runtime"));
        }
    }

    /// <summary>The module file an IntelliJ module is kept in.</summary>
    public static string ImlOf(JvmModule module) => Path.Combine(module.Root, module.Id + ".iml");

    private void ShowIdeaDependencies(JvmModule module)
    {
        string iml = ImlOf(module);
        List<(string Type, string Name, string Scope)> entries = OrderEntries(iml);
        ListBox list = new() { ItemsSource = entries.Select(e => $"{e.Name}    {e.Type}    {e.Scope}").ToList(), MaxHeight = 200, [Avalonia.Automation.AutomationProperties.NameProperty] = "Dependencies" };
        body.Children.Add(list);
        ComboBox scope = new() { ItemsSource = IdeaProjectWriter.Scopes, SelectedItem = "COMPILE", MinWidth = 120, [Avalonia.Automation.AutomationProperties.NameProperty] = "Scope" };
        Button maven = new() { Content = "Add from Maven…" };
        maven.Click += (_, _) => UnawaitedWork.Observe(AddMavenAsync(iml, scope.SelectedItem as string ?? "COMPILE"));
        Button jars = new() { Content = "Add Jars or Folder…" };
        jars.Click += (_, _) => UnawaitedWork.Observe(AddJarsAsync(iml, scope.SelectedItem as string ?? "COMPILE"));
        Button remove = new() { Content = "Remove" };
        remove.Click += (_, _) =>
        {
            if (list.SelectedIndex >= 0 && entries[list.SelectedIndex].Type == "library")
            {
                Try(() =>
                {
                    IdeaProjectWriter.RemoveDependency(iml, entries[list.SelectedIndex].Name);
                    Changed = true;
                    ShowPage();
                });
            }
        };
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Scope:", VerticalAlignment = VerticalAlignment.Center }, scope, maven, jars, remove } });
    }

    /// <summary>A module's order entries as its .iml lists them: library and module entries with their scopes.</summary>
    public static List<(string Type, string Name, string Scope)> OrderEntries(string iml)
    {
        List<(string, string, string)> found = [];
        try
        {
            XmlDocument document = new() { XmlResolver = null };
            using XmlReader reader = XmlReader.Create(iml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document.Load(reader);
            foreach (XmlElement entry in document.GetElementsByTagName("orderEntry").OfType<XmlElement>())
            {
                string type = entry.GetAttribute("type");
                string scope = entry.GetAttribute("scope") is { Length: > 0 } s ? s : "COMPILE";
                switch (type)
                {
                    case "library":
                        found.Add(("library", entry.GetAttribute("name"), scope));
                        break;
                    case "module":
                        found.Add(("module", entry.GetAttribute("module-name"), scope));
                        break;
                    case "module-library":
                        found.Add(("module library", entry.GetElementsByTagName("library").OfType<XmlElement>().FirstOrDefault()?.GetAttribute("name") is { Length: > 0 } n ? n : "(unnamed)", scope));
                        break;
                }
            }
        }
        catch (Exception unreadable) when (unreadable is XmlException or IOException or UnauthorizedAccessException)
        {
            // Nothing to list.
        }
        return found;
    }

    /// <summary>Adds a Maven library to the project and to a module's dependencies.</summary>
    public void AddMavenLibrary(string iml, string coordinates, bool transitive, string scope)
    {
        string name = IdeaProjectWriter.Libraries(root).FirstOrDefault(l => l.MavenId == coordinates.Trim())?.Name
            ?? IdeaProjectWriter.AddMavenLibrary(root, coordinates, transitive).Name;
        IdeaProjectWriter.AddDependency(iml, name, scope);
        Changed = true;
    }

    /// <summary>Adds a library of jars and folders to the project and to a module's dependencies.</summary>
    public void AddJarsLibrary(string iml, string name, IReadOnlyList<string> paths, string scope)
    {
        IdeaProjectWriter.AddJarsLibrary(root, name, paths);
        IdeaProjectWriter.AddDependency(iml, name, scope);
        Changed = true;
    }

    private async Task AddMavenAsync(string? iml, string scope)
    {
        if (await Ask("Add Library from Maven", "Coordinates (group:artifact:version):", string.Empty) is not { Length: > 0 } coordinates)
        {
            return;
        }
        Try(() =>
        {
            if (iml is null)
            {
                IdeaProjectWriter.AddMavenLibrary(root, coordinates, transitive: true);
                Changed = true;
            }
            else
            {
                AddMavenLibrary(iml, coordinates, transitive: true, scope);
            }
            ShowPage();
        });
    }

    private async Task AddJarsAsync(string? iml, string scope)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Jars",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Jars") { Patterns = ["*.jar"] }],
        });
        List<string> paths = [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
        if (paths.Count == 0)
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add Folder of Jars" });
            paths = [.. folders.Select(f => f.TryGetLocalPath()).OfType<string>()];
        }
        if (paths.Count == 0 || await Ask("Add Library", "Library name:", Path.GetFileNameWithoutExtension(paths[0])) is not { Length: > 0 } name)
        {
            return;
        }
        Try(() =>
        {
            if (iml is null)
            {
                IdeaProjectWriter.AddJarsLibrary(root, name, paths);
                Changed = true;
            }
            else
            {
                AddJarsLibrary(iml, name, paths, scope);
            }
            ShowPage();
        });
    }

    // ---- Libraries ----

    private void ShowLibraries()
    {
        IReadOnlyList<IdeaProjectLibrary> libraries = IdeaProjectWriter.Libraries(root);
        ListBox list = new() { ItemsSource = libraries.Select(l => l.MavenId is { } id ? $"{l.Name}    (Maven: {id})" : $"{l.Name}    {string.Join(", ", l.Classes)}").ToList(), MaxHeight = 300, [Avalonia.Automation.AutomationProperties.NameProperty] = "Libraries" };
        body.Children.Add(list);
        Button maven = new() { Content = "Add from Maven…" };
        maven.Click += (_, _) => UnawaitedWork.Observe(AddMavenAsync(null, "COMPILE"));
        Button jars = new() { Content = "Add Jars or Folder…" };
        jars.Click += (_, _) => UnawaitedWork.Observe(AddJarsAsync(null, "COMPILE"));
        Button remove = new() { Content = "Remove" };
        remove.Click += (_, _) =>
        {
            if (list.SelectedIndex >= 0)
            {
                UnawaitedWork.Observe(RemoveLibraryAsync(libraries[list.SelectedIndex].Name));
            }
        };
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { maven, jars, remove } });
        body.Children.Add(DialogFooter.Paragraph("A library added here is the project's; add it to a module on the Modules page. A Maven library's jars are downloaded into the local repository when the project is imported."));
    }

    private async Task RemoveLibraryAsync(string name)
    {
        if (!await Confirm("Remove Library", $"Remove the library {name} from the project and from every module that uses it?", null))
        {
            return;
        }
        Try(() =>
        {
            IdeaProjectWriter.RemoveLibrary(root, name, (snapshot?.Modules ?? []).Select(ImlOf).Where(File.Exists));
            Changed = true;
            ShowPage();
        });
    }

    // ---- JDKs ----

    /// <summary>The JDK table as the page lists it: each JDK's name, version, distribution, home and what it lacks.</summary>
    public IReadOnlyList<string> JdkRows() =>
        [
            .. JdkRegistry.Current.All().Select(jdk =>
                $"{jdk.Name}{(jdk.Name == JdkRegistry.Current.ToolingName ? " (Wayfarer's tools)" : string.Empty)}    {jdk.Version}    {JdkRegistry.Distribution(jdk.Vendor)}    {jdk.Home}    {jdk.Origin}{(jdk.Sources is null ? "    no sources" : string.Empty)}"),
            .. JdkRegistry.Current.MissingKept().Select(jdk => $"{jdk.Name}    missing    {jdk.Home}"),
        ];

    private void ShowJdks()
    {
        IReadOnlyList<Jdk> jdks = JdkRegistry.Current.All();
        IReadOnlyList<MissingKeptJdk> missing = JdkRegistry.Current.MissingKept();
        List<string> names = [.. jdks.Select(j => j.Name), .. missing.Select(m => m.Name)];
        IReadOnlyList<string> rows = JdkRows();
        ListBox list = new() { ItemsSource = rows, MaxHeight = 280, [Avalonia.Automation.AutomationProperties.NameProperty] = "JDKs" };
        list.SelectedIndex = selectedJdk is not null ? names.IndexOf(selectedJdk) : -1;
        TextBlock users = new() { TextWrapping = TextWrapping.Wrap };
        list.SelectionChanged += (_, _) =>
        {
            selectedJdk = list.SelectedIndex >= 0 ? names[list.SelectedIndex] : null;
            users.Text = selectedJdk is null ? string.Empty : UsersText(selectedJdk);
        };
        if (selectedJdk is not null)
        {
            users.Text = UsersText(selectedJdk);
        }
        body.Children.Add(list);
        Button add = new() { Content = "Add from Disk…" };
        add.Click += (_, _) => UnawaitedWork.Observe(AddJdkFromDiskAsync());
        Button download = new() { Content = "Download…" };
        download.Click += (_, _) => UnawaitedWork.Observe(DownloadJdkAsync(null));
        Button rename = new() { Content = "Rename…" };
        rename.Click += (_, _) => UnawaitedWork.Observe(RenameAsync());
        Button remove = new() { Content = "Remove…" };
        remove.Click += (_, _) => UnawaitedWork.Observe(RemoveJdkAsync());
        Button locate = new() { Content = "Locate…" };
        locate.Click += (_, _) => UnawaitedWork.Observe(LocateAsync());
        Button tooling = new() { Content = "Use for Wayfarer's Tools" };
        tooling.Click += (_, _) =>
        {
            if (selectedJdk is { } name && jdks.FirstOrDefault(j => j.Name == name) is { } jdk)
            {
                Try(() => UseForTools(jdk));
            }
        };
        body.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { add, download, rename, remove, locate, tooling }, ItemSpacing = 8, LineSpacing = 8 });
        body.Children.Add(users);
        body.Children.Add(DialogFooter.Paragraph("A missing JDK is one Wayfarer kept whose folder is gone; Locate… points it at its new folder. Renaming changes this machine's choices and Wayfarer's tools; a project's own files keep the name they hold."));
    }

    private static string UsersText(string name) =>
        JdkRegistry.Current.Users(name, ProjectJdkChoices.Current) is { Count: > 0 } users ? $"Used by: {string.Join(", ", users)}" : "Used by no project on this machine.";

    public void UseForTools(Jdk jdk)
    {
        if (jdk.Feature < ToolingJvms.Minimum)
        {
            throw new ArgumentException($"Wayfarer's tools need Java {ToolingJvms.Minimum} or later; {jdk.Name} is Java {jdk.Feature}.");
        }
        JdkRegistry.Current.ToolingName = (jdk.Origin == JdkOrigin.Detected ? JdkRegistry.Current.Add(jdk.Home) : jdk).Name;
        ShowPage();
    }

    public void RenameJdk(string name, string newName)
    {
        JdkRegistry.Current.Rename(name, newName, ProjectJdkChoices.Current);
        selectedJdk = newName;
        Changed = true;
        ShowPage();
    }

    /// <summary>Removes a JDK from the table, and its folder when <paramref name="deleteFiles"/> and Wayfarer downloaded
    /// it under <c>~/.jdks</c>.</summary>
    public void RemoveJdk(string name, bool deleteFiles)
    {
        Jdk? jdk = JdkRegistry.Current.Named(name);
        if (!JdkRegistry.Current.Remove(name, ProjectJdkChoices.Current))
        {
            throw new ArgumentException($"{name} was found where JDKs are installed and is not kept; there is nothing to remove from the table.");
        }
        if (deleteFiles && jdk is not null && JdkRegistry.DeletableFolder(jdk) is { } folder)
        {
            Directory.Delete(folder, recursive: true);
        }
        selectedJdk = null;
        Changed = true;
        ShowPage();
    }

    public void LocateJdk(string name, string home)
    {
        JdkRegistry.Current.Locate(name, home);
        Changed = true;
        ShowPage();
    }

    private async Task RenameAsync()
    {
        if (selectedJdk is { } name && await Ask("Rename JDK", "Name:", name) is { Length: > 0 } newName && newName != name)
        {
            Try(() => RenameJdk(name, newName));
        }
    }

    private async Task RemoveJdkAsync()
    {
        if (selectedJdk is not { } name)
        {
            return;
        }
        Jdk? jdk = JdkRegistry.Current.Named(name);
        CheckBox delete = new() { Content = "Also delete its files", IsVisible = jdk is not null && JdkRegistry.DeletableFolder(jdk) is not null };
        IReadOnlyList<string> users = JdkRegistry.Current.Users(name, ProjectJdkChoices.Current);
        string text = users.Count == 0 ? $"Remove {name} from the JDK table?"
            : $"Remove {name} from the JDK table? These fall back to their own requirement: {string.Join(", ", users)}.";
        if (await Confirm("Remove JDK", text, delete))
        {
            Try(() => RemoveJdk(name, delete.IsChecked == true));
        }
    }

    private async Task LocateAsync()
    {
        if (selectedJdk is not { } name)
        {
            return;
        }
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Locate {name}" });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } home)
        {
            Try(() => LocateJdk(name, home));
        }
    }

    private async Task AddJdkFromDiskAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add JDK from Disk" });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } home)
        {
            Try(() =>
            {
                selectedJdk = JdkRegistry.Current.Add(home).Name;
                Changed = true;
                ShowPage();
            });
        }
    }

    private async Task DownloadJdkAsync(JdkRequirement? wanted)
    {
        if (await new JdkDownloadDialog(wanted).ShowDialog<Jdk?>(this) is { } jdk)
        {
            selectedJdk = jdk.Name;
            Changed = true;
            ShowPage();
        }
    }

    // ---- Problems ----

    /// <summary>What stands in the project's way: missing JDKs, absent libraries and module problems.</summary>
    public IReadOnlyList<string> Problems()
    {
        List<string> found = [.. MissingJdks.Of(snapshot).Select(m => MissingJdks.Say([m]).Replace(" No installed JDK matches.", " (none installed)", StringComparison.Ordinal))];
        foreach (JvmModule module in snapshot?.Modules ?? [])
        {
            found.AddRange(module.AbsentLibraries.Select(library => $"Module '{module.Name}': library {library} is missing"));
            if (module.Problem is { Length: > 0 } said)
            {
                found.AddRange(said.Split("; ").Select(part => $"Module '{module.Name}': {part}"));
            }
        }
        return found;
    }

    private void ShowProblems()
    {
        IReadOnlyList<string> problems = Problems();
        if (problems.Count == 0)
        {
            body.Children.Add(DialogFooter.Paragraph("No problems."));
            return;
        }
        foreach (string said in problems)
        {
            StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = said, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, VerticalAlignment = VerticalAlignment.Center } } };
            if (UnsetVariable().Match(said) is { Success: true } unset)
            {
                string variable = unset.Groups[1].Value;
                Button set = new() { Content = "Set Path Variable…" };
                set.Click += (_, _) => UnawaitedWork.Observe(SetPathVariableAsync(variable));
                row.Children.Add(set);
            }
            else if (said.Contains("needs JDK", StringComparison.Ordinal) || said.Contains("needs the JDK", StringComparison.Ordinal))
            {
                Button download = new() { Content = "Download JDK…" };
                MissingJdk? missing = MissingJdks.Of(snapshot).FirstOrDefault(m => said.StartsWith(MissingJdks.Say([m])[..Math.Min(20, MissingJdks.Say([m]).Length)], StringComparison.Ordinal));
                download.Click += (_, _) => UnawaitedWork.Observe(DownloadJdkAsync(missing?.Requirement));
                row.Children.Add(download);
            }
            body.Children.Add(row);
        }
    }

    public void SetPathVariable(string name, string value)
    {
        IdeaPlatform.Current.SetPathVariable(name, value);
        Changed = true;
        ShowPage();
    }

    private async Task SetPathVariableAsync(string name)
    {
        if (await Ask("Set Path Variable", $"{name}:", IdeaPlatform.Current.PathVariables().GetValueOrDefault(name) ?? string.Empty) is { Length: > 0 } value)
        {
            Try(() => SetPathVariable(name, value));
        }
    }

    [GeneratedRegex(@"path variable ([A-Za-z0-9_.-]+) is not set")]
    private static partial Regex UnsetVariable();

    // ---- helpers ----

    private void Try(Action change)
    {
        try
        {
            change();
            problem.IsVisible = false;
        }
        catch (Exception refused) when (refused is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            problem.Text = refused.Message;
            problem.IsVisible = true;
        }
    }

    private string Relative(string path) => PathNames.Within(path, root) ? Path.GetRelativePath(root, path).Replace('\\', '/') : path;

    private static TextBlock Section(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) };

    private static TextBlock Line(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static IEnumerable<string> Texts(Control control) => control switch
    {
        TextBlock { Text: { } text } => [text],
        Button { Content: string content } => [content],
        ListBox { ItemsSource: IEnumerable<string> items } => items,
        ComboBox { SelectedItem: string selected } => [selected],
        TextBox { Text: { } text } => [text],
        Panel panel => panel.Children.SelectMany(Texts),
        ContentControl { Content: Control child } => Texts(child),
        Decorator { Child: { } child } => Texts(child),
        _ => [],
    };
}

/// <summary>Asks for one line of text, closing with it, or null.</summary>
public sealed class AskDialog : IdeWindow
{
    public AskDialog(string title, string label, string value)
    {
        Title = title;
        Width = 480;
        TextBox box = new() { Text = value };
        Button ok = DialogFooter.Default();
        ok.Click += (_, _) => Close(box.Text?.Trim());
        DialogFooter.Question(this, title, [FormRows.Of(label, box)], DialogFooter.Of(ok, DialogFooter.Cancel(() => Close(null))));
        Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
    }
}
