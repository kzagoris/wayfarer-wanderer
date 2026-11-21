using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using System.Globalization;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

public enum NewProjectKind
{
    Application,
    SpringBoot
}

public enum NewProjectLanguage
{
    Java,
    Kotlin
}

public enum NewProjectBuild
{
    Maven,
    Gradle,

    /// <summary>A plain IntelliJ module: an <c>.iml</c> beside <c>src</c>, compiled with the JDK's own javac.</summary>
    IntelliJ
}

public sealed record ProjectGenerationOptions
{
    public string ProjectName { get; init; } = "";
    public string Destination { get; init; } = "";
    public string GroupId { get; init; } = "";
    public string ArtifactId { get; init; } = "";
    public string PackageName { get; init; } = "";
    public string JdkHome { get; init; } = "";
    public NewProjectKind Kind { get; init; }
    public NewProjectLanguage Language { get; init; }
    public NewProjectBuild Build { get; init; }
    public string LaunchName { get; init; } = "Application";
    public Action<string>? BeforePublish { get; init; }
}

public sealed record ProjectGenerationResult(
    string Destination,
    string LaunchConfigurationPath,
    bool RequiresWorkspaceTrust);

public sealed class ProjectGenerationException : InvalidOperationException
{
    public ProjectGenerationException(string message)
        : base(message)
    {
    }

    public ProjectGenerationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public static class ProjectGenerator
{
    public const int MinimumJdk = 21;
    public const string TemplateVersion = "2";
    public const string GradleVersion = "9.7.1";
    public const string MavenVersion = "3.9.11";
    public const string KotlinVersion = "2.4.21";
    public const string SpringBootVersion = "4.1.2";

    /// <summary>The newest JDK a Kotlin starter accepts: KotlinVersion's JvmTarget stops at JVM_26, and a starter
    /// targets its JDK's own release, so a newer JDK is refused rather than compiled down.</summary>
    public const int KotlinHighestJdk = 26;

    /// <summary>The newest JDK a starter in this language accepts, or null for no limit.</summary>
    public static int? HighestJdk(NewProjectLanguage language) =>
        language == NewProjectLanguage.Kotlin ? KotlinHighestJdk : null;

    /// <summary>Whether a starter exists for this kind, language and build. An IntelliJ module is compiled with javac
    /// alone, so it makes a plain Java application only.</summary>
    public static bool Supports(NewProjectKind kind, NewProjectLanguage language, NewProjectBuild build) =>
        build != NewProjectBuild.IntelliJ || (kind == NewProjectKind.Application && language == NewProjectLanguage.Java);

    public static async Task<ProjectGenerationResult> GenerateAsync(
        ProjectGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProjectGenerationValues values = ValidateValues(options);
        DirectoryInfo parent = Directory.CreateDirectory(values.Parent);
        string staging = Path.Combine(parent.FullName, $".{Path.GetFileName(values.Destination)}.wayfarer-{Guid.NewGuid():N}.staging");
        string reservation = staging + ".lock";
        bool ownsStaging = false;
        bool published = false;

        await Task.Yield();
        try
        {
            using FileStream publicationLock = new(
                reservation,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            Directory.CreateDirectory(staging);
            ownsStaging = true;

            // The project asks for the chosen JDK by its name in the person's table, so a clone resolves it from its own.
            Jdk registered = JdkRegistry.Current.Add(values.Jdk);
            values = values with { JdkName = registered.Name };
            WriteTemplateAsync(staging, values, cancellationToken);
            WriteTemplateProvenance(staging, values, cancellationToken);
            WriteNew(ProjectJdk.FileOf(staging), ProjectJdk.Serialize(new JdkRequirement(registered.Name, registered.Feature, JdkRegistry.Distribution(registered.Vendor))), cancellationToken);
            WriteLaunchConfiguration(staging, values, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            options.BeforePublish?.Invoke(staging);
            cancellationToken.ThrowIfCancellationRequested();

            Publish(staging, values.Destination, cancellationToken);
            ownsStaging = false;
            published = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new ProjectGenerationException(
                $"The project could not be published safely ({failure.Message}). Existing entries were preserved; correct the disk or connection problem and retry.",
                failure);
        }
        finally
        {
            if (ownsStaging && Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            if (!published && File.Exists(reservation))
            {
                TryDelete(reservation);
            }
        }

        return new ProjectGenerationResult(
            values.Destination,
            Path.Combine(values.Destination, ".wayfarer", "launches", "starter.json"),
            RequiresWorkspaceTrust(values.Destination));
    }

    public static void CheckWorkspaceTrust(string projectPath)
    {
        string full = Path.GetFullPath(projectPath);
        try
        {
            WorkspaceTrust.Check(full);
        }
        catch (UntrustedFolderException failure)
        {
            throw new ProjectGenerationException(failure.Message);
        }
    }

    public static ProjectGenerationOptions Validate(ProjectGenerationOptions options)
    {
        _ = ValidateValues(options);
        return options;
    }

    private static ProjectGenerationValues ValidateValues(ProjectGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Require(options.ProjectName, "Project name");
        Require(options.LaunchName, "Launch configuration name");
        Require(options.JdkHome, "JDK");
        Require(options.Destination, "Destination");
        if (!Enum.IsDefined(options.Kind) || !Enum.IsDefined(options.Language) || !Enum.IsDefined(options.Build))
        {
            throw new ProjectGenerationException("The project kind, language and build system must each name a supported choice.");
        }
        if (!Supports(options.Kind, options.Language, options.Build))
        {
            throw new ProjectGenerationException(
                "An IntelliJ module builds a plain Java application with javac. Choose Maven or Gradle for a Kotlin or Spring Boot project.");
        }

        string name = options.ProjectName.Trim();
        if (name.Any(IsLineOrControl))
        {
            throw new ProjectGenerationException("The project name must not contain control characters.");
        }

        string group = RequireCoordinate(options.GroupId, "Group");
        string artifact = RequireArtifact(options.ArtifactId);
        string package = RequirePackage(options.PackageName, options.Language);
        string jdk = ValidateJdk(options.JdkHome);
        if (HighestJdk(options.Language) is int highest && JdkVersion(jdk) is int selected && selected > highest)
        {
            throw new ProjectGenerationException(
                $"Kotlin {KotlinVersion} supports JDK {MinimumJdk} to {highest}; the selected JDK is version {selected}. Choose JDK {highest} or older for a Kotlin project.");
        }
        string destination;
        try
        {
            destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Destination.Trim()));
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectGenerationException($"The destination is not a valid local path: {failure.Message}");
        }
        if (Path.GetPathRoot(destination) == destination)
        {
            throw new ProjectGenerationException("The destination must name a project folder, not a file-system root.");
        }
        string parent = Path.GetDirectoryName(destination)
            ?? throw new ProjectGenerationException("The destination must have a parent folder.");
        if (File.Exists(destination))
        {
            throw new ProjectGenerationException("The destination already exists as a file.");
        }
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new ProjectGenerationException("The destination is not empty. Existing files and folders were not changed.");
        }

        string nearest = parent;
        while (!Directory.Exists(nearest))
        {
            string? above = Path.GetDirectoryName(nearest);
            if (above is null)
            {
                throw new ProjectGenerationException("The destination's drive or share does not exist.");
            }
            nearest = above;
        }
        if (File.Exists(parent))
        {
            throw new ProjectGenerationException("The destination's parent is a file.");
        }

        return new ProjectGenerationValues(name, destination, parent, group, artifact, package, jdk, JdkVersion(jdk), options);
    }

    private static void Publish(string staging, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(destination))
        {
            try
            {
                Directory.Move(staging, destination);
                return;
            }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new ProjectGenerationException(
                    "Another entry arrived at the destination while the project was being generated. Nothing existing was changed.");
            }
        }

        if (Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new ProjectGenerationException("Another entry arrived in the destination. It was preserved unchanged.");
        }

        string reserve = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.wayfarer-{Guid.NewGuid():N}.empty");
        Directory.Move(destination, reserve);
        try
        {
            if (Directory.EnumerateFileSystemEntries(reserve).Any())
            {
                Directory.Move(reserve, destination);
                throw new ProjectGenerationException("Another entry arrived in the destination. It was preserved unchanged.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            if (Directory.EnumerateFileSystemEntries(reserve).Any())
            {
                Directory.Move(destination, staging);
                Directory.Move(reserve, destination);
                throw new ProjectGenerationException(
                    "Another entry arrived in the destination during publication. It was preserved unchanged.");
            }
        }
        catch
        {
            if (Directory.Exists(reserve) && !Directory.Exists(destination))
            {
                Directory.Move(reserve, destination);
            }
            else if (Directory.Exists(reserve) && !Directory.EnumerateFileSystemEntries(reserve).Any())
            {
                Directory.Delete(reserve);
            }
            throw;
        }
        Directory.Delete(reserve);
    }

    private static void WriteTemplateAsync(
        string staging,
        ProjectGenerationValues values,
        CancellationToken cancellationToken)
    {
        string root = TemplateRoot();
        VerifyTemplateBundle(root);
        // The template's own files first: a common file it has too (an IntelliJ module's README and .gitignore) is its own.
        CopyDirectory(
            Path.Combine(root, "templates",
                TemplateName(values.Options.Kind),
                values.Options.Language.ToString().ToLowerInvariant(),
                values.Options.Build.ToString().ToLowerInvariant()),
            staging,
            values,
            cancellationToken);
        if (values.Options.Build != NewProjectBuild.IntelliJ)
        {
            CopyDirectory(Path.Combine(root, "wrappers", values.Options.Build.ToString().ToLowerInvariant()),
                staging, values, cancellationToken);
        }
        CopyDirectory(Path.Combine(root, "common"), staging, values, cancellationToken, keepExisting: true);
    }

    private static void CopyDirectory(
        string source,
        string destination,
        ProjectGenerationValues values,
        CancellationToken cancellationToken,
        bool keepExisting = false)
    {
        if (!Directory.Exists(source))
        {
            throw new ProjectGenerationException($"The bundled JVM template is missing: {source}");
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, file)
                .Replace("__PACKAGE_PATH__", values.Package.Replace('.', Path.DirectorySeparatorChar), StringComparison.Ordinal)
                .Replace("__MODULE__", values.Artifact, StringComparison.Ordinal);
            string target = Path.Combine(destination, relative);
            if (keepExisting && File.Exists(target))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Path.GetExtension(file).Equals(".binary", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(file, target[..^".binary".Length], overwrite: false);
            }
            else if (Path.GetExtension(file).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(file, target, overwrite: false);
            }
            else
            {
                byte[] text = Encoding.UTF8.GetBytes(Render(File.ReadAllText(file), values));
                using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(text);
                output.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows() && (Path.GetFileName(target) is "gradlew" or "mvnw"))
            {
                File.SetUnixFileMode(target,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
    }

    private static string Render(string template, ProjectGenerationValues values) =>
        template
            .Replace("{{NAME}}", EscapeSourceString(values.Name, values.Options.Language), StringComparison.Ordinal)
            .Replace("{{NAME_XML}}", System.Security.SecurityElement.Escape(values.Name), StringComparison.Ordinal)
            .Replace("{{GROUP}}", values.Group, StringComparison.Ordinal)
            .Replace("{{ARTIFACT}}", values.Artifact, StringComparison.Ordinal)
            .Replace("{{MODULE}}", values.Artifact, StringComparison.Ordinal)
            .Replace("{{PACKAGE}}", values.Package, StringComparison.Ordinal)
            .Replace("{{PACKAGE_PATH}}", values.Package.Replace('.', Path.DirectorySeparatorChar), StringComparison.Ordinal)
            .Replace("{{JDK}}", values.Jdk.Replace('\\', '/'), StringComparison.Ordinal)
            .Replace("{{JDK_NAME_XML}}", System.Security.SecurityElement.Escape(values.JdkName), StringComparison.Ordinal)
            .Replace("{{JDK_VERSION}}", values.JdkVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{KOTLIN_VERSION}}", KotlinVersion, StringComparison.Ordinal)
            .Replace("{{SPRING_BOOT_VERSION}}", SpringBootVersion, StringComparison.Ordinal)
            .Replace("{{MAVEN_VERSION}}", MavenVersion, StringComparison.Ordinal)
            .Replace("{{GRADLE_VERSION}}", GradleVersion, StringComparison.Ordinal)
            .Replace("{{LAUNCH}}", values.Options.LaunchName, StringComparison.Ordinal);

    private static string TemplateName(NewProjectKind kind) =>
        kind == NewProjectKind.Application ? "application" : "boot";

    private static void WriteTemplateProvenance(
        string staging,
        ProjectGenerationValues values,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(staging, ".wayfarer", "template.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        cancellationToken.ThrowIfCancellationRequested();
        string json = JsonSerializer.Serialize(new
        {
            schema = 1,
            name = values.Name,
            templateVersion = TemplateVersion,
            kind = values.Options.Kind.ToString(),
            language = values.Options.Language.ToString(),
            build = values.Options.Build.ToString(),
            gradle = values.Options.Build == NewProjectBuild.Gradle ? GradleVersion : null,
            maven = values.Options.Build == NewProjectBuild.Maven ? MavenVersion : null,
            kotlin = values.Options.Language == NewProjectLanguage.Kotlin ? KotlinVersion : null,
            springBoot = values.Options.Kind == NewProjectKind.SpringBoot ? SpringBootVersion : null,
            junit = "5.12.2",
            wrappers = Checksums(staging, values.Options.Build),
            source = "Bundled with Wayfarer Wanderer; see THIRD_PARTY_NOTICES.md"
        }, JsonOptions);
        WriteNew(path, json, cancellationToken);
    }

    private static void WriteLaunchConfiguration(
        string staging,
        ProjectGenerationValues values,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(staging, ".wayfarer", "launches", "starter.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string main = values.Options.Kind == NewProjectKind.SpringBoot
            ? values.Package + ".Application"
            : values.Package + ".App";
        if (values.Options.Language == NewProjectLanguage.Kotlin)
        {
            main += "Kt";
        }
        JvmLaunchConfiguration launch = new()
        {
            Id = "starter",
            Name = values.Options.LaunchName,
            Module = values.Artifact,
            MainClass = main,
            WorkingDirectory = values.Destination,
            Profile = string.Empty,
            BuildTask = (values.Options.Kind, values.Options.Build) switch
            {
                (_, NewProjectBuild.IntelliJ) => "classes",
                (NewProjectKind.SpringBoot, NewProjectBuild.Maven) => "spring-boot:run",
                (NewProjectKind.SpringBoot, _) => "bootRun",
                (_, NewProjectBuild.Maven) => "exec:java",
                _ => "run"
            },
            Arguments = [],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
        };
        string json = JsonSerializer.Serialize(launch, JsonOptions);
        WriteNew(path, json, cancellationToken);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static void WriteNew(string path, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] bytes = Encoding.UTF8.GetBytes(text + '\n');
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static Dictionary<string, string> Checksums(string folder, NewProjectBuild build) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(_ => build != NewProjectBuild.IntelliJ)
            .Where(file => build == NewProjectBuild.Maven
                ? Path.GetFileName(file).StartsWith("mvnw", StringComparison.Ordinal) ||
                  Path.GetRelativePath(folder, file).StartsWith(".mvn" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                : Path.GetFileName(file).StartsWith("gradlew", StringComparison.Ordinal) ||
                  Path.GetRelativePath(folder, file).StartsWith("gradle" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(
                file => Path.GetRelativePath(folder, file).Replace('\\', '/'),
                file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))),
                StringComparer.Ordinal);

    private static string EscapeSourceString(string value, NewProjectLanguage language)
    {
        StringBuilder escaped = new(value.Length);
        foreach (char character in value)
        {
            if (IsLineOrControl(character))
            {
                throw new ProjectGenerationException("The project name must not contain control or line-separator characters.");
            }
            escaped.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '$' when language == NewProjectLanguage.Kotlin => "\\$",
                _ => character
            });
        }
        return escaped.ToString();
    }

    private static bool IsLineOrControl(char character) =>
        char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator;

    private static string TemplateRoot()
    {
        string[] starts = [Directory.GetCurrentDirectory(), AppContext.BaseDirectory];
        foreach (string start in starts)
        {
            DirectoryInfo? at = new(start);
            while (at is not null)
            {
                string candidate = Path.Combine(at.FullName, "templates", "jvm");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                at = at.Parent;
            }
        }
        throw new ProjectGenerationException(
            "The bundled JVM templates could not be found. Reinstall or restore the Wayfarer Wanderer template assets, then retry.");
    }

    private static void VerifyTemplateBundle(string root)
    {
        string manifestPath = Path.Combine(root, "manifest.json");
        try
        {
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement document = manifest.RootElement;
            if (document.GetProperty("templateVersion").GetString() != TemplateVersion)
            {
                throw new ProjectGenerationException(
                    "The bundled JVM template version is not supported. Restore the matching Wayfarer Wanderer assets and retry.");
            }
            foreach (JsonProperty wrapper in document.GetProperty("wrapperSha256").EnumerateObject())
            {
                string path = Path.GetFullPath(Path.Combine(root, "wrappers", wrapper.Name.Replace('/', Path.DirectorySeparatorChar)));
                if (!Path.GetRelativePath(Path.Combine(root, "wrappers"), path).Split(
                    Path.DirectorySeparatorChar).Contains("..") && File.Exists(path))
                {
                    string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
                    if (!actual.Equals(wrapper.Value.GetString(), StringComparison.Ordinal))
                    {
                        throw new ProjectGenerationException(
                            $"The bundled wrapper '{wrapper.Name}' failed its checksum. Restore the matching template assets and retry.");
                    }
                }
                else
                {
                    throw new ProjectGenerationException(
                        $"The bundled wrapper '{wrapper.Name}' is missing or its path is invalid. Restore the template assets and retry.");
                }
            }
        }
        catch (Exception failure) when (failure is IOException or JsonException or KeyNotFoundException or FormatException)
        {
            throw new ProjectGenerationException(
                "The bundled JVM template manifest could not be read safely. Restore the template assets and retry.",
                failure);
        }
    }

    private static string RequireCoordinate(string value, string label)
    {
        string coordinate = Require(value, label);
        if (!coordinate.All(IsCoordinateCharacter) || !char.IsAsciiLetterLower(coordinate[0]))
        {
            throw new ProjectGenerationException($"{label} must start with a lowercase letter and contain only lowercase letters, digits and dots.");
        }
        if (coordinate.Split('.').Any(segment => segment.Length == 0 || !char.IsAsciiLetterLower(segment[0])))
        {
            throw new ProjectGenerationException($"{label} segments must each start with a lowercase letter and must not be empty.");
        }
        return coordinate;
    }

    private static string RequireArtifact(string value)
    {
        string artifact = Require(value, "Artifact");
        if (!char.IsAsciiLetterLower(artifact[0]) ||
            !artifact.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.'))
        {
            throw new ProjectGenerationException("Artifact must start with a lowercase letter and contain only lowercase letters, digits, '-' and '.'.");
        }
        return artifact;
    }

    private static string RequirePackage(string value, NewProjectLanguage language)
    {
        string package = Require(value, "Package");
        string[] parts = package.Split('.');
        if (parts.Any(part => part.Length == 0 || !IsJavaIdentifier(part, language)))
        {
            throw new ProjectGenerationException("Package must contain valid, non-keyword Java identifiers separated by dots.");
        }
        return package;
    }

    private static bool IsJavaIdentifier(string identifier, NewProjectLanguage language)
    {
        Rune[] runes = identifier.EnumerateRunes().ToArray();
        if (runes.Length == 0 || !IsJavaStart(runes[0]) || runes.Any(r => !IsJavaPart(r)))
        {
            return false;
        }
        return JavaKeywords.All(keyword => !keyword.Equals(identifier, StringComparison.Ordinal)) &&
            (language != NewProjectLanguage.Kotlin ||
             KotlinKeywords.All(keyword => !keyword.Equals(identifier, StringComparison.Ordinal)));
    }

    private static bool IsJavaStart(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
            UnicodeCategory.LetterNumber || rune.Value == '_';
    }

    private static bool IsJavaPart(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return IsJavaStart(rune) || category is UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.ConnectorPunctuation or UnicodeCategory.Format;
    }

    private static readonly string[] JavaKeywords =
    [
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue",
        "default", "do", "double", "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if",
        "implements", "import", "instanceof", "int", "interface", "long", "module", "native", "new", "non-sealed",
        "null", "open", "opens", "package", "permits", "private", "protected", "provides", "public", "record",
        "requires", "return", "sealed", "short", "static", "strictfp", "super", "switch", "synchronized",
        "this", "throw", "throws", "to", "transient", "transitive", "true", "try", "uses", "var", "void",
        "volatile", "when", "while", "with", "yield", "_"
    ];

    private static readonly string[] KotlinKeywords =
    [
        "as", "break", "class", "continue", "do", "else", "false", "for", "fun", "if", "in", "interface", "is",
        "null", "object", "package", "return", "super", "this", "throw", "true", "try", "typealias", "typeof",
        "val", "var", "when", "while"
    ];

    private static bool IsCoordinateCharacter(char c) =>
        char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '.';

    private static string Require(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ProjectGenerationException($"{label} is required.");
        }
        return value.Trim();
    }

    private static string ValidateJdk(string value)
    {
        string home;
        try
        {
            home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectGenerationException($"The selected JDK folder is not a valid local path: {failure.Message}");
        }
        if (!Directory.Exists(home))
        {
            throw new ProjectGenerationException("The selected JDK folder does not exist. Choose a local JDK and retry.");
        }
        string java = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        string javac = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "javac.exe" : "javac");
        string release = Path.Combine(home, "release");
        if (!File.Exists(java) || !File.Exists(javac) || !File.Exists(release))
        {
            throw new ProjectGenerationException("The selected folder is not a complete JDK (bin/java, bin/javac and release are required). Choose a local JDK and retry.");
        }
        int version = JdkVersion(home);
        if (version < MinimumJdk)
        {
            throw new ProjectGenerationException($"JDK {MinimumJdk} or newer is required; the selected JDK is version {version}.");
        }
        return home;
    }

    private static int JdkVersion(string home)
    {
        string release = Path.Combine(home, "release");
        string? declared = File.ReadLines(release).FirstOrDefault(line =>
            line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal));
        if (declared is null ||
            !int.TryParse(declared[14..].Trim().Trim('"').Split('.')[0].Split('-', 2)[0],
                NumberStyles.None, CultureInfo.InvariantCulture, out int version))
        {
            throw new ProjectGenerationException("The selected JDK release file does not declare a valid Java version.");
        }
        return version;
    }

    private static bool RequiresWorkspaceTrust(string projectPath)
        => !WorkspaceTrust.Trusts(Path.GetFullPath(projectPath));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private sealed record ProjectGenerationValues(
        string Name,
        string Destination,
        string Parent,
        string Group,
        string Artifact,
        string Package,
        string Jdk,
        int JdkVersion,
        ProjectGenerationOptions Options)
    {
        /// <summary>The chosen JDK's name in the <see cref="JdkRegistry"/>, once it is registered.</summary>
        public string JdkName { get; init; } = "";
    }
}
