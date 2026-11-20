using System.Globalization;
using System.Xml.Linq;

namespace WayfarerWanderer.Jvm;

/// <summary>One IntelliJ <c>JAVA_MODULE</c> folder: its <c>.iml</c>, <c>.idea/misc.xml</c>, and project libraries.</summary>
public static class IdeaProject
{
    public static WorkspaceSnapshot Read(string workspaceRoot, long generation)
    {
        string root = Path.GetFullPath(workspaceRoot);
        string iml = ModuleFile(root) ?? throw new InvalidDataException(
            $"'{root}' has no single IntelliJ module file. An IntelliJ module is a folder that contains its .iml file.");
        XDocument document = Load(iml);
        XElement module = document.Root ?? throw new InvalidDataException($"'{iml}' has no module element.");
        if (!string.Equals(module.Attribute("type")?.Value, "JAVA_MODULE", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{iml}' is not a Java module.");
        }
        XElement manager = module.Elements("component")
            .FirstOrDefault(element => element.Attribute("name")?.Value == "NewModuleRootManager")
            ?? throw new InvalidDataException($"'{iml}' has no module roots.");

        string name = Path.GetFileNameWithoutExtension(iml);
        XElement? project = ProjectRoot(root);
        bool inherit = !string.Equals(manager.Attribute("inherit-compiler-output")?.Value, "false", StringComparison.OrdinalIgnoreCase);
        string? projectOutput = Attribute(project, "output", "url") is { } outputUrl
            ? Expand(outputUrl, root, root)
            : null;
        string outputRoot = inherit
            ? projectOutput ?? Path.Combine(root, "out")
            : Attribute(manager, "output", "url") is { } moduleOutput
                ? Expand(moduleOutput, root, root)
                : projectOutput ?? Path.Combine(root, "out");
        string classes = inherit || manager.Element("output") is null
            ? Path.Combine(outputRoot, "production", name)
            : outputRoot;
        List<JvmSourceRoot> sources = [];
        foreach (XElement folder in manager.Descendants("sourceFolder"))
        {
            string? url = folder.Attribute("url")?.Value;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }
            bool tests = string.Equals(folder.Attribute("isTestSource")?.Value, "true", StringComparison.OrdinalIgnoreCase);
            sources.Add(new JvmSourceRoot(Expand(url, root, root), tests ? JvmSourceKind.TestJava : JvmSourceKind.MainJava));
        }

        List<string> present = [];
        List<string> absent = [];
        foreach (XElement entry in manager.Elements("orderEntry"))
        {
            if (entry.Attribute("type")?.Value != "library")
            {
                continue;
            }
            string library = entry.Attribute("name")?.Value ?? string.Empty;
            List<string> jars = LibraryJars(root, library);
            if (jars.Count == 0)
            {
                absent.Add(library.Length == 0 ? "(unnamed library)" : library);
                continue;
            }
            foreach (string jar in jars)
            {
                if (File.Exists(jar) || Directory.Exists(jar))
                {
                    present.Add(jar);
                }
                else
                {
                    absent.Add(jar);
                }
            }
        }

        string? jdkName = manager.Elements("orderEntry")
            .FirstOrDefault(entry => entry.Attribute("type")?.Value == "jdk")
            ?.Attribute("jdkName")?.Value
            ?? project?.Attribute("project-jdk-name")?.Value;
        int? level = LanguageLevel(project?.Attribute("languageLevel")?.Value);
        // A JDK saved in Wayfarer wins. Otherwise use the JDK IntelliJ named, then JAVA_HOME.
        JvmToolchain toolchain = new(ProjectJdk.Recorded(root) ?? IdeaJdks.Home(jdkName), level, jdkName);
        JvmModule imported = new(
            name,
            name,
            root,
            sources,
            new JvmClasspath(present, [], [classes, .. present]),
            toolchain,
            toolchain,
            toolchain,
            ["classes"],
            WorkspaceLayouts.MainClasses(sources))
        {
            AbsentLibraries = absent,
        };
        return new WorkspaceSnapshot(generation, root, JvmBuildSystem.Idea, iml, toolchain, [imported])
        {
            OutputRoot = outputRoot,
        };
    }

    public static string? ModuleFile(string workspaceRoot)
    {
        if (!Directory.Exists(workspaceRoot))
        {
            return null;
        }
        string[] files = [.. Directory.EnumerateFiles(workspaceRoot, "*.iml", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)];
        return files.Length == 1 ? files[0] : null;
    }

    internal static string Expand(string url, string moduleDir, string projectDir)
    {
        string path = url.Trim();
        if (path.StartsWith("jar://", StringComparison.OrdinalIgnoreCase))
        {
            path = path["jar://".Length..];
        }
        else if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            path = path["file://".Length..];
        }
        int bang = path.IndexOf("!/", StringComparison.Ordinal);
        if (bang >= 0)
        {
            path = path[..bang];
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        path = path.Replace("$MODULE_DIR$", moduleDir, StringComparison.Ordinal)
            .Replace("$PROJECT_DIR$", projectDir, StringComparison.Ordinal)
            .Replace("$USER_HOME$", home, StringComparison.Ordinal);
        path = Uri.UnescapeDataString(path);
        if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && path[2] == ':')
        {
            path = path[1..];
        }
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(projectDir, path);
        }
        return Path.GetFullPath(path);
    }

    private static XElement? ProjectRoot(string root)
    {
        string misc = Path.Combine(root, ".idea", "misc.xml");
        if (!File.Exists(misc))
        {
            return null;
        }
        return Load(misc).Descendants("component")
            .FirstOrDefault(element => element.Attribute("name")?.Value == "ProjectRootManager");
    }

    private static string? Attribute(XElement? parent, string child, string name) =>
        parent?.Element(child)?.Attribute(name)?.Value;

    private static List<string> LibraryJars(string root, string name)
    {
        string folder = Path.Combine(root, ".idea", "libraries");
        if (name.Length == 0 || !Directory.Exists(folder))
        {
            return [];
        }
        foreach (string file in Directory.EnumerateFiles(folder, "*.xml"))
        {
            foreach (XElement library in Load(file).Descendants("library"))
            {
                if (!string.Equals(library.Attribute("name")?.Value, name, StringComparison.Ordinal))
                {
                    continue;
                }
                XElement? classes = library.Element("CLASSES");
                if (classes is null)
                {
                    return [];
                }
                return [.. classes.Elements("root")
                    .Select(entry => entry.Attribute("url")?.Value)
                    .OfType<string>()
                    .Select(url => Expand(url, root, root))];
            }
        }
        return [];
    }

    private static int? LanguageLevel(string? value)
    {
        const string prefix = "JDK_";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }
        string rest = value[prefix.Length..];
        if (rest.StartsWith("1_", StringComparison.Ordinal) &&
            int.TryParse(rest["1_".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int legacy))
        {
            return legacy;
        }
        return int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int modern) ? modern : null;
    }

    private static XDocument Load(string path)
    {
        try
        {
            return XDocument.Load(path, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException error)
        {
            throw new InvalidDataException($"'{path}' is not an IntelliJ project file.", error);
        }
    }
}

/// <summary>The JDK an IntelliJ project names, from the newest JetBrains <c>jdk.table.xml</c> and then <c>JAVA_HOME</c>.</summary>
public static class IdeaJdks
{
    public static string? Home(string? jdkName, string? configDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(jdkName))
        {
            foreach (string table in Tables(configDirectory))
            {
                string? home = ReadHome(table, jdkName);
                if (home is not null && Directory.Exists(home))
                {
                    return home;
                }
            }
        }
        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        return string.IsNullOrWhiteSpace(javaHome) ? null : Path.GetFullPath(javaHome);
    }

    public static string Missing(string? jdkName)
    {
        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        string named = string.IsNullOrWhiteSpace(jdkName) ? "(none)" : jdkName;
        string fallback = string.IsNullOrWhiteSpace(javaHome) ? "(not set)" : javaHome;
        return $"The IntelliJ JDK '{named}' was not found. JAVA_HOME is '{fallback}'.";
    }

    private static IEnumerable<string> Tables(string? configDirectory)
    {
        string root = configDirectory ?? DefaultConfig();
        if (!Directory.Exists(root))
        {
            yield break;
        }
        foreach (string product in Directory.EnumerateDirectories(root).OrderDescending(StringComparer.Ordinal))
        {
            string table = Path.Combine(product, "options", "jdk.table.xml");
            if (File.Exists(table))
            {
                yield return table;
            }
        }
    }

    private static string DefaultConfig()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JetBrains");
        }
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "JetBrains");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "JetBrains");
    }

    private static string? ReadHome(string table, string jdkName)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(table, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
        foreach (XElement jdk in document.Descendants("jdk"))
        {
            if (jdk.Element("name")?.Attribute("value")?.Value != jdkName)
            {
                continue;
            }
            string? home = jdk.Element("homePath")?.Attribute("value")?.Value;
            if (!string.IsNullOrWhiteSpace(home))
            {
                return IdeaProject.Expand(home, string.Empty, string.Empty);
            }
        }
        return null;
    }
}

/// <summary>JDT LS settings for an IntelliJ module that has no Maven or Gradle build.</summary>
public static class IdeaLanguageSettings
{
    public static object Document(WorkspaceSnapshot snapshot) => new
    {
        java = new
        {
            project = new
            {
                sourcePaths = SourcePaths(snapshot),
                outputPath = OutputPath(snapshot),
                referencedLibraries = Libraries(snapshot),
            },
            configuration = new { runtimes = Runtimes(snapshot) },
        },
    };

    public static object? Value(WorkspaceSnapshot snapshot, string? section) => section switch
    {
        "java.project.sourcePaths" => SourcePaths(snapshot),
        "java.project.outputPath" => OutputPath(snapshot),
        "java.project.referencedLibraries" => Libraries(snapshot),
        "java.configuration.runtimes" => Runtimes(snapshot),
        _ => null,
    };

    public static string[] SourcePaths(WorkspaceSnapshot snapshot) =>
        [.. snapshot.Modules.SelectMany(module => module.SourceRoots)
            .Select(root => Relative(snapshot, root.Path))
            .Distinct(StringComparer.Ordinal)];

    public static string OutputPath(WorkspaceSnapshot snapshot)
    {
        string? classes = snapshot.Modules.Select(module => module.Classpath.Runtime.FirstOrDefault()).FirstOrDefault(path => path is not null);
        return classes is null ? "bin" : Relative(snapshot, classes);
    }

    public static string[] Libraries(WorkspaceSnapshot snapshot) =>
        [.. snapshot.Modules.SelectMany(module => module.Classpath.Compile).Distinct(StringComparer.Ordinal)];

    public static object[] Runtimes(WorkspaceSnapshot snapshot)
    {
        JvmToolchain toolchain = snapshot.ToolingToolchain;
        if (string.IsNullOrWhiteSpace(toolchain.Home))
        {
            return [];
        }
        string name = toolchain.LanguageVersion is int level
            ? "JavaSE-" + level.ToString(CultureInfo.InvariantCulture)
            : "JavaSE";
        return [new { name, path = toolchain.Home, @default = true }];
    }

    private static string Relative(WorkspaceSnapshot snapshot, string path) =>
        Path.GetRelativePath(snapshot.Root, path).Replace('\\', '/');
}
