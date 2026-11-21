using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

/// <summary>
/// Hands Wayfarer's JDK table to Gradle and Maven, so a toolchain the build asks for resolves to a JDK the IDE shows
/// even where the build tool's own detection would not find it.
/// <list type="bullet">
/// <item>Gradle gets <c>-Porg.gradle.java.installations.paths</c>. A command-line <c>-P</c> replaces the value in the
/// person's <c>gradle.properties</c> and the project's, so theirs come first and the table's homes after. Auto-detection
/// and auto-download stay as the project sets them.</item>
/// <item>Maven gets <c>--global-toolchains</c>, a <c>toolchains.xml</c> written under Wayfarer's state: the Maven
/// installation's own <c>conf/toolchains.xml</c> entries (which that option replaces) first, then one JDK toolchain per
/// table JDK. The person's <c>~/.m2/toolchains.xml</c> still applies on top.</item>
/// </list>
/// The host merges Gradle's paths the same way for the Tooling API (<c>GradleJdks.java</c>).
/// </summary>
public static class BuildJdks
{
    public const string GradlePathsProperty = "org.gradle.java.installations.paths";

    /// <summary>The JDK homes Gradle is told about for <paramref name="root"/>: the person's
    /// <c>gradle.properties</c> paths, the project's, then the table's homes, each once; empty when the table is.</summary>
    public static IReadOnlyList<string> GradleInstallationPaths(string root, IEnumerable<Jdk> jdks, string? gradleUserHome = null)
    {
        ArgumentNullException.ThrowIfNull(jdks);
        List<string> homes = [.. jdks.Select(jdk => jdk.Home)];
        if (homes.Count == 0)
        {
            return [];
        }
        List<string> paths = [];
        void Offer(string path)
        {
            string trimmed = path.Trim();
            if (trimmed.Length > 0 && !paths.Any(known => PathNames.Same(known, trimmed)))
            {
                paths.Add(trimmed);
            }
        }
        foreach (string file in new[] { Path.Combine(gradleUserHome ?? GradleUserHome(), "gradle.properties"), Path.Combine(root, "gradle.properties") })
        {
            if (Property(file, GradlePathsProperty) is { } theirs)
            {
                foreach (string path in theirs.Split(','))
                {
                    Offer(path);
                }
            }
        }
        homes.ForEach(Offer);
        return paths;
    }

    /// <summary>The <c>-P</c> argument that tells Gradle about the table's JDKs; null when the table is empty.</summary>
    public static string? GradleArgument(string root, IEnumerable<Jdk> jdks, string? gradleUserHome = null) =>
        GradleInstallationPaths(root, jdks, gradleUserHome) is { Count: > 0 } paths
            ? $"-P{GradlePathsProperty}={string.Join(',', paths)}"
            : null;

    /// <summary>Gradle's user home: <c>GRADLE_USER_HOME</c>, else <c>~/.gradle</c>.</summary>
    public static string GradleUserHome() =>
        Environment.GetEnvironmentVariable("GRADLE_USER_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gradle");

    /// <summary>Writes the Maven toolchains file for <paramref name="root"/> into <paramref name="folder"/> and returns
    /// its path; null when the table is empty. The installation's own toolchains come first.</summary>
    public static string? MavenToolchainsFile(string root, IEnumerable<Jdk> jdks, string folder, string? installation = null)
    {
        ArgumentNullException.ThrowIfNull(jdks);
        string text = MavenToolchains(jdks, installation ?? MavenInstallationToolchains(root));
        if (text.Length == 0)
        {
            return null;
        }
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "toolchains.xml");
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            if (File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes))
            {
                return file;
            }
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            // Written anew below.
        }
        AtomicFile.Write(file, bytes);
        return file;
    }

    /// <summary>The text of a toolchains file: the toolchains of <paramref name="installation"/> (a
    /// <c>toolchains.xml</c> path, or null), then for each table JDK one toolchain whose <c>version</c> is its full
    /// version and <c>id</c> its name, and for the newest JDK of each feature one whose <c>version</c> is the feature
    /// (<c>17</c>, <c>1.8</c>), since Maven matches a plain version exactly. Empty when the table is.</summary>
    public static string MavenToolchains(IEnumerable<Jdk> jdks, string? installation)
    {
        List<Jdk> table = [.. jdks];
        if (table.Count == 0)
        {
            return string.Empty;
        }
        StringBuilder xml = new();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        xml.Append("<!-- Written by Wayfarer Wanderer from its JDK table. Changes are lost on the next build. -->\n");
        xml.Append("<toolchains xmlns=\"http://maven.apache.org/TOOLCHAINS/1.1.0\">\n");
        foreach (string kept in InstallationToolchains(installation))
        {
            xml.Append("  ").Append(kept).Append('\n');
        }
        foreach (Jdk jdk in table)
        {
            Toolchain(xml, jdk.Version, jdk, jdk.Name);
        }
        foreach (Jdk newest in table.GroupBy(jdk => jdk.Feature).Select(group => group.OrderByDescending(jdk => jdk.Version, Comparer<string>.Create(JdkResolution.CompareVersions)).First()))
        {
            string feature = newest.Feature <= 8 ? $"1.{newest.Feature}" : newest.Feature.ToString(CultureInfo.InvariantCulture);
            if (feature != newest.Version)
            {
                Toolchain(xml, feature, newest, null);
            }
        }
        xml.Append("</toolchains>\n");
        return xml.ToString();
    }

    private static void Toolchain(StringBuilder xml, string version, Jdk jdk, string? id)
    {
        xml.Append("  <toolchain>\n    <type>jdk</type>\n    <provides>\n");
        xml.Append("      <version>").Append(SecurityElement.Escape(version)).Append("</version>\n");
        xml.Append("      <vendor>").Append(SecurityElement.Escape(JdkRegistry.Distribution(jdk.Vendor))).Append("</vendor>\n");
        if (id is not null)
        {
            xml.Append("      <id>").Append(SecurityElement.Escape(id)).Append("</id>\n");
        }
        xml.Append("    </provides>\n    <configuration>\n");
        xml.Append("      <jdkHome>").Append(SecurityElement.Escape(jdk.Home)).Append("</jdkHome>\n");
        xml.Append("    </configuration>\n  </toolchain>\n");
    }

    /// <summary>Each <c>toolchain</c> element of an installation's toolchains file, as XML text with the namespace
    /// left off; none when it is absent or does not read.</summary>
    private static IEnumerable<string> InstallationToolchains(string? file)
    {
        if (file is null || !File.Exists(file))
        {
            return [];
        }
        XmlDocument document = new() { XmlResolver = null };
        try
        {
            using XmlReader reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document.Load(reader);
        }
        catch (Exception unreadable) when (unreadable is XmlException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return [.. document.DocumentElement?.ChildNodes.OfType<XmlElement>()
            .Where(element => element.LocalName == "toolchain")
            .Select(element => Plain(element).OuterXml) ?? []];
    }

    /// <summary>A copy of an element whose names carry no namespace, so it takes the generated file's.</summary>
    private static XmlElement Plain(XmlElement element)
    {
        XmlDocument document = new();
        XmlElement copy = document.CreateElement(element.LocalName);
        foreach (XmlAttribute attribute in element.Attributes)
        {
            if (attribute.Prefix != "xmlns" && attribute.Name != "xmlns")
            {
                copy.SetAttribute(attribute.LocalName, attribute.Value);
            }
        }
        foreach (XmlNode child in element.ChildNodes)
        {
            copy.AppendChild(child is XmlElement inner ? document.ImportNode(Plain(inner), true) : document.ImportNode(child, true));
        }
        return copy;
    }

    /// <summary>The <c>conf/toolchains.xml</c> of the Maven installation <paramref name="root"/> builds with: the one
    /// its wrapper downloaded (<c>~/.m2/wrapper/dists</c>), else <c>MAVEN_HOME</c>'s; null for none.</summary>
    public static string? MavenInstallationToolchains(string root)
    {
        string properties = Path.Combine(root, ".mvn", "wrapper", "maven-wrapper.properties");
        if (Property(properties, "distributionUrl") is { } url)
        {
            string name = Path.GetFileNameWithoutExtension(url.Split('/').Last());
            if (name.EndsWith("-bin", StringComparison.Ordinal))
            {
                string dists = Path.Combine(Environment.GetEnvironmentVariable("MAVEN_USER_HOME") is { Length: > 0 } mavenUser
                    ? mavenUser
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".m2"), "wrapper", "dists", name);
                foreach (string hash in JdkScanner.Children(dists))
                {
                    foreach (string home in JdkScanner.Children(hash))
                    {
                        string file = Path.Combine(home, "conf", "toolchains.xml");
                        if (File.Exists(file))
                        {
                            return file;
                        }
                    }
                }
            }
        }
        return Environment.GetEnvironmentVariable("MAVEN_HOME") is { Length: > 0 } mavenHome && File.Exists(Path.Combine(mavenHome, "conf", "toolchains.xml"))
            ? Path.Combine(mavenHome, "conf", "toolchains.xml")
            : null;
    }

    /// <summary>The folder generated build files go in: beside the JDK table, else a temporary folder.</summary>
    public static string StateFolder(JdkRegistry registry) =>
        registry.File is { } file
            ? Path.GetDirectoryName(Path.GetFullPath(file))!
            : Path.Combine(Path.GetTempPath(), "wayfarer-wanderer");

    /// <summary>A key's value in a Java properties file (<c>key=value</c> or <c>key: value</c>, <c>\:</c> and
    /// <c>\\</c> unescaped); null when absent.</summary>
    internal static string? Property(string file, string key)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }
            foreach (string raw in File.ReadLines(file))
            {
                string line = raw.TrimStart();
                if (line.Length == 0 || line[0] is '#' or '!')
                {
                    continue;
                }
                int split = line.IndexOfAny(['=', ':']);
                if (split > 0 && line[..split].Trim() == key)
                {
                    return line[(split + 1)..].Trim().Replace("\\:", ":", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
                }
            }
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return null;
    }
}
