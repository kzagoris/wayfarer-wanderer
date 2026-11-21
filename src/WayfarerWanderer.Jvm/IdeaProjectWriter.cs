using System.Text;
using System.Xml;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

/// <summary>A project library of an IntelliJ project: its name, the file under <c>.idea/libraries</c> that holds it, its
/// class roots as IntelliJ writes them, and for a repository library its Maven coordinates.</summary>
public sealed record IdeaProjectLibrary(string Name, string File, IReadOnlyList<string> Classes, string? MavenId);

/// <summary>
/// Writes IntelliJ's own project files for a project without a build tool, so a project edited in Wayfarer opens the
/// same in IntelliJ: project libraries (<c>.idea/libraries/*.xml</c>, plain or repository), a module's library order
/// entries (<c>*.iml</c>) and the project JDK and language level (<c>.idea/misc.xml</c>). Each write is atomic and keeps
/// the elements, attributes and whitespace it does not change; the file is a build input, so the import runs again.
/// </summary>
public static class IdeaProjectWriter
{
    public static readonly IReadOnlyList<string> Scopes = ["COMPILE", "TEST", "RUNTIME", "PROVIDED"];

    /// <summary>The project's libraries, in file order.</summary>
    public static IReadOnlyList<IdeaProjectLibrary> Libraries(string root)
    {
        string folder = Path.Combine(root, ".idea", "libraries");
        List<IdeaProjectLibrary> found = [];
        if (!Directory.Exists(folder))
        {
            return found;
        }
        foreach (string file in Directory.GetFiles(folder, "*.xml").Order(StringComparer.Ordinal))
        {
            if (Load(file) is not { } document)
            {
                continue;
            }
            foreach (XmlElement library in document.GetElementsByTagName("library").OfType<XmlElement>())
            {
                List<string> classes = [.. Children(library, "CLASSES").SelectMany(c => Children(c, "root")).Select(r => r.GetAttribute("url")).Where(u => u.Length > 0)];
                string? mavenId = library.GetAttribute("type") == "repository" ? Children(library, "properties").FirstOrDefault()?.GetAttribute("maven-id") : null;
                found.Add(new IdeaProjectLibrary(library.GetAttribute("name"), file, classes, mavenId is { Length: > 0 } ? mavenId : null));
            }
        }
        return found;
    }

    /// <summary>Writes a repository library for <paramref name="coordinates"/> (<c>group:artifact:version</c>) whose jar
    /// sits in <c>$MAVEN_REPOSITORY$</c>; the import downloads it there when it is missing.</summary>
    public static IdeaProjectLibrary AddMavenLibrary(string root, string coordinates, bool transitive)
    {
        string[] parts = coordinates.Trim().Split(':');
        if (parts.Length < 3 || parts.Any(part => part.Length == 0))
        {
            throw new ArgumentException($"{coordinates} is not group:artifact:version.", nameof(coordinates));
        }
        (string group, string artifact, string version) = (parts[0], parts[1], parts[^1]);
        string jar = $"jar://$MAVEN_REPOSITORY$/{group.Replace('.', '/')}/{artifact}/{version}/{artifact}-{version}.jar!/";
        string name = coordinates.Trim();
        string xml = $"""
            <component name="libraryTable">
              <library name="{Escape(name)}" type="repository">
                <properties {(transitive ? string.Empty : "include-transitive-deps=\"false\" ")}maven-id="{Escape(name)}" />
                <CLASSES>
                  <root url="{Escape(jar)}" />
                </CLASSES>
                <JAVADOC />
                <SOURCES />
              </library>
            </component>
            """;
        return WriteLibrary(root, name, xml, [jar], name);
    }

    /// <summary>Writes a plain library of jars and folders: a jar as a jar root, a folder as a jar directory (its jars).
    /// Paths inside the project are written through <c>$PROJECT_DIR$</c>.</summary>
    public static IdeaProjectLibrary AddJarsLibrary(string root, string name, IReadOnlyList<string> paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        List<string> classes = [];
        StringBuilder roots = new();
        StringBuilder directories = new();
        foreach (string path in paths)
        {
            string url = Url(root, Path.GetFullPath(path));
            if (Directory.Exists(path))
            {
                classes.Add("file://" + url);
                roots.Append($"      <root url=\"file://{Escape(url)}\" />\n");
                directories.Append($"    <jarDirectory url=\"file://{Escape(url)}\" recursive=\"false\" />\n");
            }
            else
            {
                classes.Add($"jar://{url}!/");
                roots.Append($"      <root url=\"jar://{Escape(url)}!/\" />\n");
            }
        }
        string xml = "<component name=\"libraryTable\">\n"
            + $"  <library name=\"{Escape(name)}\">\n    <CLASSES>\n{roots}    </CLASSES>\n    <JAVADOC />\n    <SOURCES />\n{directories}  </library>\n"
            + "</component>\n";
        return WriteLibrary(root, name, xml, classes, null);
    }

    /// <summary>Removes a project library and every module's entry for it.</summary>
    public static void RemoveLibrary(string root, string name, IEnumerable<string> modules)
    {
        foreach (string iml in modules)
        {
            RemoveDependency(iml, name);
        }
        foreach (IdeaProjectLibrary library in Libraries(root).Where(l => l.Name == name))
        {
            File.Delete(library.File);
        }
    }

    /// <summary>Adds a project library to a module's order entries with a scope (COMPILE is written as IntelliJ does,
    /// without one); one already there keeps its place and takes the scope.</summary>
    public static void AddDependency(string iml, string library, string scope = "COMPILE")
    {
        if (!Scopes.Contains(scope))
        {
            throw new ArgumentException($"{scope} is not a scope.", nameof(scope));
        }
        Edit(iml, document =>
        {
            XmlElement manager = Manager(document, iml);
            XmlElement? entry = LibraryEntry(manager, library);
            if (entry is null)
            {
                entry = document.CreateElement("orderEntry");
                entry.SetAttribute("type", "library");
                entry.SetAttribute("name", library);
                entry.SetAttribute("level", "project");
                Append(manager, entry);
            }
            if (scope == "COMPILE")
            {
                entry.RemoveAttribute("scope");
            }
            else
            {
                // IntelliJ writes the scope ahead of the name.
                entry.RemoveAttribute("scope");
                XmlAttribute attribute = document.CreateAttribute("scope");
                attribute.Value = scope;
                entry.Attributes.InsertAfter(attribute, entry.Attributes["type"]);
            }
        });
    }

    /// <summary>Removes a library's entry from a module; nothing when it has none.</summary>
    public static void RemoveDependency(string iml, string library) =>
        Edit(iml, document =>
        {
            XmlElement manager = Manager(document, iml);
            if (LibraryEntry(manager, library) is { } entry)
            {
                if (entry.PreviousSibling is XmlWhitespace space)
                {
                    manager.RemoveChild(space);
                }
                manager.RemoveChild(entry);
            }
        });

    /// <summary>Sets the project JDK by its name in the table, and the language level (<c>JDK_17</c>) when given.</summary>
    public static void SetProjectJdk(string root, string jdkName, int? languageLevel = null) =>
        Edit(Path.Combine(root, ".idea", "misc.xml"), document =>
        {
            XmlElement manager = document.GetElementsByTagName("component").OfType<XmlElement>().FirstOrDefault(c => c.GetAttribute("name") == "ProjectRootManager")
                ?? throw new InvalidDataException(".idea/misc.xml has no ProjectRootManager.");
            manager.SetAttribute("project-jdk-name", jdkName);
            manager.SetAttribute("project-jdk-type", "JavaSDK");
            if (languageLevel is int level)
            {
                manager.SetAttribute("languageLevel", level <= 8 ? $"JDK_1_{level}" : $"JDK_{level}");
            }
        });

    /// <summary>Sets the project's compiler output folder (<c>$PROJECT_DIR$/out</c> for a folder inside the project).</summary>
    public static void SetOutput(string root, string folder) =>
        Edit(Path.Combine(root, ".idea", "misc.xml"), document =>
        {
            XmlElement manager = document.GetElementsByTagName("component").OfType<XmlElement>().FirstOrDefault(c => c.GetAttribute("name") == "ProjectRootManager")
                ?? throw new InvalidDataException(".idea/misc.xml has no ProjectRootManager.");
            XmlElement output = Children(manager, "output").FirstOrDefault() ?? (XmlElement)manager.AppendChild(document.CreateElement("output"))!;
            output.SetAttribute("url", "file://" + Url(root, Path.GetFullPath(folder)));
        });

    /// <summary>A path as IntelliJ writes it in a project file: through <c>$PROJECT_DIR$</c> inside the project, with
    /// forward slashes.</summary>
    public static string Url(string root, string path)
    {
        string full = Path.GetFullPath(path);
        string written = PathNames.Within(full, Path.GetFullPath(root))
            ? "$PROJECT_DIR$/" + Path.GetRelativePath(root, full)
            : full;
        return written.Replace('\\', '/').TrimEnd('/');
    }

    private static IdeaProjectLibrary WriteLibrary(string root, string name, string xml, IReadOnlyList<string> classes, string? mavenId)
    {
        if (Libraries(root).Any(library => library.Name == name))
        {
            throw new ArgumentException($"The project already has a library named {name}.", nameof(name));
        }
        string folder = Directory.CreateDirectory(Path.Combine(root, ".idea", "libraries")).FullName;
        string stem = string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));
        string file = Path.Combine(folder, stem + ".xml");
        for (int n = 2; File.Exists(file); n++)
        {
            file = Path.Combine(folder, $"{stem}_{n}.xml");
        }
        AtomicFile.Write(file, Encoding.UTF8.GetBytes(xml.Replace("\r\n", "\n", StringComparison.Ordinal)));
        return new IdeaProjectLibrary(name, file, classes, mavenId);
    }

    private static XmlElement Manager(XmlDocument document, string iml) =>
        document.GetElementsByTagName("component").OfType<XmlElement>().FirstOrDefault(c => c.GetAttribute("name") == "NewModuleRootManager")
            ?? throw new InvalidDataException($"{iml} has no module roots.");

    private static XmlElement? LibraryEntry(XmlElement manager, string library) =>
        Children(manager, "orderEntry").FirstOrDefault(e => e.GetAttribute("type") == "library" && e.GetAttribute("name") == library);

    /// <summary>Appends an element after the parent's last child element, in that element's indentation.</summary>
    private static void Append(XmlElement parent, XmlElement element)
    {
        XmlNode? last = parent.ChildNodes.OfType<XmlElement>().LastOrDefault();
        string indent = last?.PreviousSibling is XmlWhitespace space ? space.Value ?? "\n    " : "\n    ";
        XmlNode after = last ?? (XmlNode)parent;
        if (last is null)
        {
            parent.AppendChild(parent.OwnerDocument.CreateWhitespace(indent));
            parent.AppendChild(element);
            return;
        }
        parent.InsertAfter(element, after);
        parent.InsertAfter(parent.OwnerDocument.CreateWhitespace(indent), after);
    }

    private static void Edit(string file, Action<XmlDocument> change)
    {
        XmlDocument document = Load(file) ?? throw new InvalidDataException($"{file} is not an IntelliJ project file.");
        change(document);
        bool declared = document.FirstChild is XmlDeclaration;
        using MemoryStream bytes = new();
        using (XmlWriter writer = XmlWriter.Create(bytes, new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = !declared, Indent = false, NewLineHandling = NewLineHandling.None }))
        {
            document.Save(writer);
        }
        AtomicFile.Write(file, bytes.ToArray());
    }

    private static XmlDocument? Load(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }
        XmlDocument document = new() { XmlResolver = null, PreserveWhitespace = true };
        try
        {
            using XmlReader reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document.Load(reader);
            return document;
        }
        catch (Exception unreadable) when (unreadable is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<XmlElement> Children(XmlElement parent, string name) =>
        parent.ChildNodes.OfType<XmlElement>().Where(e => e.Name == name);

    private static string Escape(string text) => System.Security.SecurityElement.Escape(text) ?? text;
}
