using System.Text;
using System.Text.Json;
using System.Xml;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

/// <summary>An IntelliJ application-level library: its name, its class roots as IntelliJ writes them, and for a
/// repository library its Maven coordinates.</summary>
public sealed record IdeaLibrary(string Name, IReadOnlyList<string> Classes, string? MavenId = null, bool? Transitive = null);

/// <summary>
/// IntelliJ's platform-level settings an IntelliJ project refers to: path variables (<c>$NAME$</c> in project files,
/// IntelliJ's <c>path.macros.xml</c>) and global libraries (<c>applicationLibraries.xml</c>), read from the same
/// configuration folders <see cref="JdkRegistry.IntelliJTable"/> reads, newest product first, and sent with each
/// import as the JDK table is. Wayfarer's own path variables (<see cref="File"/>, in its state) come first.
/// </summary>
public sealed class IdeaPlatform
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string? intelliJConfig;
    private readonly Lock gate = new();

    /// <param name="file">Where Wayfarer's own path variables are kept; null keeps them in memory only.</param>
    /// <param name="intelliJConfig">The folder of JetBrains products' configuration folders; the person's when null.</param>
    public IdeaPlatform(string? file, string? intelliJConfig = null)
    {
        File = file;
        this.intelliJConfig = intelliJConfig;
    }

    /// <summary>The platform settings the app uses; it points this at its state folder at start.</summary>
    public static IdeaPlatform Current { get; set; } = new(null);

    public string? File { get; }

    /// <summary>Every path variable: Wayfarer's own, then IntelliJ's that Wayfarer does not set.</summary>
    public IReadOnlyDictionary<string, string> PathVariables()
    {
        Dictionary<string, string> all = new(Own(), StringComparer.Ordinal);
        foreach (string options in OptionsFolders())
        {
            foreach ((string name, string value) in ReadMacros(Path.Combine(options, "path.macros.xml")))
            {
                all.TryAdd(name, value);
            }
        }
        return all;
    }

    /// <summary>Wayfarer's own path variables.</summary>
    public IReadOnlyDictionary<string, string> Own()
    {
        lock (gate)
        {
            return File is not null && System.IO.File.Exists(File) ? ReadOwn(File) : memory;
        }
    }

    private Dictionary<string, string> memory = [];

    /// <summary>Sets one of Wayfarer's own path variables (null or empty removes it) and saves them.</summary>
    public void SetPathVariable(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (gate)
        {
            Dictionary<string, string> own = new(File is not null && System.IO.File.Exists(File) ? ReadOwn(File) : memory, StringComparer.Ordinal);
            if (string.IsNullOrEmpty(value))
            {
                own.Remove(name);
            }
            else
            {
                own[name] = value;
            }
            memory = own;
            if (File is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(File))!);
                AtomicFile.Write(File, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(own, JsonOptions)));
            }
        }
    }

    /// <summary>IntelliJ's global libraries, each name once, newest product first.</summary>
    public IReadOnlyList<IdeaLibrary> GlobalLibraries()
    {
        List<IdeaLibrary> all = [];
        foreach (string options in OptionsFolders())
        {
            foreach (IdeaLibrary library in ReadLibraries(Path.Combine(options, "applicationLibraries.xml")))
            {
                if (!all.Any(known => known.Name == library.Name))
                {
                    all.Add(library);
                }
            }
        }
        return all;
    }

    private static Dictionary<string, string> ReadOwn(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(file)) ?? [];
        }
        catch (Exception unreadable) when (unreadable is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private IEnumerable<string> OptionsFolders()
    {
        string root = intelliJConfig ?? JdkScanner.IntelliJConfig();
        try
        {
            return Directory.Exists(root)
                ? [.. Directory.GetDirectories(root).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Select(product => Path.Combine(product, "options"))]
                : [];
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<(string Name, string Value)> ReadMacros(string file)
    {
        if (Load(file) is not { } document)
        {
            yield break;
        }
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (XmlElement macro in document.GetElementsByTagName("macro").OfType<XmlElement>())
        {
            string name = macro.GetAttribute("name");
            string value = macro.GetAttribute("value");
            if (name.Length > 0 && value.Length > 0)
            {
                yield return (name, value.Replace("$USER_HOME$", user, StringComparison.Ordinal));
            }
        }
    }

    private static IEnumerable<IdeaLibrary> ReadLibraries(string file)
    {
        if (Load(file) is not { } document)
        {
            yield break;
        }
        foreach (XmlElement library in document.GetElementsByTagName("library").OfType<XmlElement>())
        {
            string name = library.GetAttribute("name");
            if (name.Length == 0)
            {
                continue;
            }
            List<string> classes = [];
            foreach (XmlElement roots in library.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "CLASSES"))
            {
                classes.AddRange(roots.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "root").Select(e => e.GetAttribute("url")).Where(url => url.Length > 0));
            }
            classes.AddRange(library.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "jarDirectory").Select(e => e.GetAttribute("url")).Where(url => url.Length > 0));
            XmlElement? properties = library.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.Name == "properties");
            string? mavenId = library.GetAttribute("type") == "repository" && properties?.GetAttribute("maven-id") is { Length: > 0 } id ? id : null;
            bool? transitive = mavenId is null ? null : properties!.GetAttribute("include-transitive-deps") != "false";
            yield return new IdeaLibrary(name, classes, mavenId, transitive);
        }
    }

    private static XmlDocument? Load(string file)
    {
        if (!System.IO.File.Exists(file))
        {
            return null;
        }
        XmlDocument document = new() { XmlResolver = null };
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
}
