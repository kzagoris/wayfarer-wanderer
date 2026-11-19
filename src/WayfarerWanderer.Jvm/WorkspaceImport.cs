using System.Text.RegularExpressions;
using System.Xml.Linq;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

public interface IWorkspaceImporter
{
    Task<WorkspaceSnapshot> ImportAsync(string workspaceRoot, long generation, CancellationToken cancellationToken);
}

public interface IWorkspaceImportAdapter
{
    bool CanImport(string workspaceRoot);

    Task<WorkspaceSnapshot> ImportAsync(string workspaceRoot, long generation, CancellationToken cancellationToken);
}

public sealed class WorkspaceImporter : IWorkspaceImporter
{
    private readonly IReadOnlyList<IWorkspaceImportAdapter> adapters;

    public WorkspaceImporter()
        : this([new MavenWorkspaceImportAdapter(), new GradleWorkspaceImportAdapter()])
    {
    }

    public WorkspaceImporter(IReadOnlyList<IWorkspaceImportAdapter> adapters)
    {
        this.adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
    }

    public async Task<WorkspaceSnapshot> ImportAsync(string workspaceRoot, long generation, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(workspaceRoot);
        WorkspaceTrust.Check(root);
        IWorkspaceImportAdapter adapter = adapters.FirstOrDefault(candidate => candidate.CanImport(root))
            ?? throw new InvalidDataException($"No supported Maven pom or Gradle settings exists in '{root}'.");
        return await adapter.ImportAsync(root, generation, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class MavenWorkspaceImportAdapter : IWorkspaceImportAdapter
{
    public bool CanImport(string workspaceRoot) => File.Exists(Path.Combine(workspaceRoot, "pom.xml"));

    public Task<WorkspaceSnapshot> ImportAsync(string workspaceRoot, long generation, CancellationToken cancellationToken)
    {
        WorkspaceTrust.Check(workspaceRoot);
        cancellationToken.ThrowIfCancellationRequested();
        string pomPath = Path.Combine(workspaceRoot, "pom.xml");
        XDocument document = XDocument.Load(pomPath, LoadOptions.PreserveWhitespace);
        XElement project = document.Root ?? throw new InvalidDataException($"'{pomPath}' has no project element.");
        XNamespace ns = project.Name.Namespace;
        int? release = IntegerProperty(project, ns, "maven.compiler.release");
        string? jdk = ProjectJdk.HomeOf(workspaceRoot);
        List<JvmModule> modules = [];
        string parentName = Text(project, ns, "artifactId") ?? new DirectoryInfo(workspaceRoot).Name;
        List<string> names = [.. project.Element(ns + "modules")?.Elements(ns + "module").Select(element => element.Value.Trim()) ?? []];
        if (names.Count == 0)
        {
            modules.Add(ReadMavenModule(workspaceRoot, parentName, project, ns, release, jdk));
        }
        else
        {
            foreach (string name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string moduleRoot = Path.GetFullPath(Path.Combine(workspaceRoot, name));
                string modulePom = Path.Combine(moduleRoot, "pom.xml");
                if (!File.Exists(modulePom))
                {
                    throw new InvalidDataException($"Maven module '{name}' has no pom.xml.");
                }
                XDocument moduleDocument = XDocument.Load(modulePom, LoadOptions.PreserveWhitespace);
                XElement moduleProject = moduleDocument.Root ?? throw new InvalidDataException($"'{modulePom}' has no project element.");
                int? moduleRelease = IntegerProperty(moduleProject, moduleProject.Name.Namespace, "maven.compiler.release") ?? release;
                modules.Add(ReadMavenModule(moduleRoot, name, moduleProject, moduleProject.Name.Namespace, moduleRelease, jdk));
            }
        }
        JvmToolchain toolchain = new(jdk, release, null);
        WorkspaceSnapshot snapshot = new(generation, workspaceRoot, JvmBuildSystem.Maven, pomPath, toolchain, modules);
        return Task.FromResult(snapshot);
    }

    private static JvmModule ReadMavenModule(string root, string fallbackName, XElement project, XNamespace ns, int? release, string? jdk)
    {
        string name = Text(project, ns, "artifactId") ?? fallbackName;
        JvmToolchain toolchain = new(jdk, release, null);
        return new JvmModule(
            fallbackName,
            name,
            root,
            WorkspaceLayouts.SourceRoots(root),
            new JvmClasspath(string.Empty, string.Empty, string.Empty),
            toolchain,
            toolchain,
            toolchain,
            ["validate", "compile", "test", "package"],
            WorkspaceLayouts.MainClasses(root));
    }

    private static string? Text(XElement parent, XNamespace ns, string name) =>
        parent.Element(ns + name)?.Value.Trim();

    private static int? IntegerProperty(XElement project, XNamespace ns, string name)
    {
        XElement? properties = project.Element(ns + "properties");
        XElement? property = properties?.Element(ns + name);
        return int.TryParse(property?.Value.Trim(), out int value) ? value : null;
    }
}

public sealed class GradleWorkspaceImportAdapter : IWorkspaceImportAdapter
{
    private static readonly Regex IncludeCall = new(
        @"\binclude\s*(?:\((?<body>.*?)\)|(?<plain>[^\r\n;]+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex QuotedModule = new(
        @"[""'](?<module>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool CanImport(string workspaceRoot) =>
        File.Exists(Path.Combine(workspaceRoot, "settings.gradle")) ||
        File.Exists(Path.Combine(workspaceRoot, "settings.gradle.kts"));

    public async Task<WorkspaceSnapshot> ImportAsync(string workspaceRoot, long generation, CancellationToken cancellationToken)
    {
        WorkspaceTrust.Check(workspaceRoot);
        string? settings = FirstFile(workspaceRoot, "settings.gradle.kts", "settings.gradle");
        if (settings is null)
        {
            throw new InvalidDataException($"No Gradle settings exists in '{workspaceRoot}'.");
        }
        string content = await File.ReadAllTextAsync(settings, cancellationToken).ConfigureAwait(false);
        List<string> names = [];
        foreach (Match include in IncludeCall.Matches(content))
        {
            string body = include.Groups["body"].Success ? include.Groups["body"].Value : include.Groups["plain"].Value;
            foreach (Match match in QuotedModule.Matches(body))
            {
                string name = match.Groups["module"].Value.Trim(':').Replace(':', Path.DirectorySeparatorChar);
                if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                }
            }
        }
        string? jdk = ProjectJdk.HomeOf(workspaceRoot);
        List<JvmModule> modules = [];
        if (names.Count == 0)
        {
            names.Add(string.Empty);
        }
        foreach (string name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root = name.Length == 0 ? workspaceRoot : Path.GetFullPath(Path.Combine(workspaceRoot, name));
            if (!Directory.Exists(root))
            {
                throw new InvalidDataException($"Gradle module '{name}' does not exist.");
            }
            modules.Add(await ReadGradleModuleAsync(root, name, jdk, cancellationToken).ConfigureAwait(false));
        }
        JvmToolchain toolchain = new(jdk, null, null);
        return new WorkspaceSnapshot(generation, workspaceRoot, JvmBuildSystem.Gradle, settings, toolchain, modules);
    }

    private static async Task<JvmModule> ReadGradleModuleAsync(string root, string name, string? jdk, CancellationToken cancellationToken)
    {
        string? buildFile = FirstFile(root, "build.gradle.kts", "build.gradle");
        string script = buildFile is null ? string.Empty : await File.ReadAllTextAsync(buildFile, cancellationToken).ConfigureAwait(false);
        JvmToolchain toolchain = new(jdk, null, null);
        List<JvmMainClass> mains = [.. WorkspaceLayouts.MainClasses(root)];
        foreach (Match match in Regex.Matches(script, @"mainClass(?:\.set)?\s*(?:=|\()\s*[""']([^""']+)[""']"))
        {
            string main = match.Groups[1].Value;
            if (!mains.Any(existing => existing.ClassName == main))
            {
                mains.Add(new JvmMainClass(main, string.Empty));
            }
        }
        return new JvmModule(
            name.Replace(Path.DirectorySeparatorChar, ':'),
            name.Length == 0 ? new DirectoryInfo(root).Name : Path.GetFileName(root),
            root,
            WorkspaceLayouts.SourceRoots(root),
            new JvmClasspath(string.Empty, string.Empty, string.Empty),
            toolchain,
            toolchain,
            toolchain,
            ["classes", "test", "build", "run"],
            mains);
    }

    private static string? FirstFile(string root, params string[] names) =>
        names.Select(name => Path.Combine(root, name)).FirstOrDefault(File.Exists);
}

internal static class WorkspaceLayouts
{
    private static readonly (string Relative, JvmSourceKind Kind)[] Roots =
    [
        ("src/main/java", JvmSourceKind.MainJava),
        ("src/main/kotlin", JvmSourceKind.MainKotlin),
        ("src/test/java", JvmSourceKind.TestJava),
        ("src/test/kotlin", JvmSourceKind.TestKotlin),
        ("src/main/resources", JvmSourceKind.MainResources),
        ("src/test/resources", JvmSourceKind.TestResources),
        ("build/generated/sources", JvmSourceKind.Generated),
        ("target/generated-sources", JvmSourceKind.Generated),
    ];

    public static IReadOnlyList<JvmSourceRoot> SourceRoots(string root) =>
        [.. Roots.Select(item => (Path: Path.GetFullPath(Path.Combine(root, item.Relative)), item.Kind))
            .Where(item => Directory.Exists(item.Path))
            .Select(item => new JvmSourceRoot(item.Path, item.Kind))];

    public static IReadOnlyList<JvmMainClass> MainClasses(string root)
    {
        List<JvmMainClass> mains = [];
        IEnumerable<JvmSourceRoot> sourceRoots = SourceRoots(root).Where(item => item.Kind is JvmSourceKind.MainJava or JvmSourceKind.MainKotlin);
        foreach (JvmSourceRoot sourceRoot in sourceRoots)
        {
            foreach (string file in Directory.EnumerateFiles(sourceRoot.Path, "*.*", SearchOption.AllDirectories)
                         .Where(path => path.EndsWith(".java", StringComparison.OrdinalIgnoreCase) ||
                                        path.EndsWith(".kt", StringComparison.OrdinalIgnoreCase)))
            {
                string text = File.ReadAllText(file);
                string? className = MainClassOf(text, file);
                if (className is not null)
                {
                    mains.Add(new JvmMainClass(className, file));
                }
            }
        }
        return mains;
    }

    private static string? MainClassOf(string text, string file)
    {
        Match package = Regex.Match(text, @"(?m)^\s*package\s+([\w.]+)");
        string prefix = package.Success ? package.Groups[1].Value + "." : string.Empty;
        if (file.EndsWith(".java", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(text, @"\bpublic\s+static\s+void\s+main\s*\("))
        {
            Match type = Regex.Match(text, @"\b(?:class|record)\s+([A-Za-z_$][\w$]*)");
            return type.Success ? prefix + type.Groups[1].Value : null;
        }
        if (file.EndsWith(".kt", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(text, @"(?m)^\s*fun\s+main\s*\("))
        {
            return prefix + Path.GetFileNameWithoutExtension(file) + "Kt";
        }
        return null;
    }
}
