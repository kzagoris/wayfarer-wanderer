// After Passenger 494009d7 src/Passenger.App/Templates/RepositoryFiles.cs, MIT (notices/Passenger/LICENSE): Wayfarer's own
// texts for Java and Kotlin in place of dotnet new's templates.
using Passenger.Core;
using Passenger.Text;

namespace WayfarerWanderer.App.Shell.Vcs;

/// <summary>A file a Git repository is given beside its code.</summary>
public enum RepositoryFile
{
    GitIgnore,
    GitAttributes,
    EditorConfig,
}

/// <summary>The files a Git repository is given, made one way wherever Wayfarer makes one (New Project, Create Git
/// Repository): a .gitignore of what Maven, Gradle, IntelliJ and the JVM leave behind; a .gitattributes keeping every text
/// file with LF line breaks, the Windows scripts with CRLF and the binaries out of diffs; an .editorconfig giving every file
/// LF line breaks, a final one and UTF-8. Each is written only where no file is there yet.</summary>
public static class RepositoryFiles
{
    /// <summary>The file's name, as it stands in the repository's top folder.</summary>
    public static string NameOf(RepositoryFile file) => file switch
    {
        RepositoryFile.GitIgnore => ".gitignore",
        RepositoryFile.GitAttributes => ".gitattributes",
        _ => ".editorconfig",
    };

    /// <summary>What the file does, in a few words, for the row that offers it.</summary>
    public static string DetailOf(RepositoryFile file) => file switch
    {
        RepositoryFile.GitIgnore => "Maven's, Gradle's and IntelliJ's output left out",
        RepositoryFile.GitAttributes => "LF line breaks on every system, binary files marked",
        _ => "LF line breaks, a final line break and UTF-8 in every file",
    };

    /// <summary>The file's text.</summary>
    public static string TextOf(RepositoryFile file) => file switch
    {
        RepositoryFile.GitIgnore => GitIgnore,
        RepositoryFile.GitAttributes => GitAttributes,
        _ => EditorConfig,
    };

    /// <summary>What Maven, Gradle, IntelliJ and the JVM leave behind, which no one commits.</summary>
    public const string GitIgnore = """
        # Build output
        target/
        build/
        out/
        bin/
        *.class

        # Gradle
        .gradle/
        !gradle/wrapper/gradle-wrapper.jar

        # Kotlin
        .kotlin/

        # IntelliJ IDEA
        .idea/
        *.iml
        *.iws

        # Logs and crash files
        *.log
        hs_err_pid*

        # Operating systems
        .DS_Store
        Thumbs.db

        """;

    /// <summary>Every text file kept with LF line breaks in the repository and in each checkout whatever the system, the
    /// wrappers' and Windows' scripts as their shells read them, Java's and Kotlin's diff drivers, so a hunk is headed by the
    /// method it is in, and the binary files a JVM project holds marked as such, never diffed or merged as text.</summary>
    public const string GitAttributes = """
        # Text files: LF line breaks in the repository and in every checkout, whatever the system
        * text=auto eol=lf

        # The wrappers' shell scripts, which sh reads by LF, and Windows' command scripts, which cmd.exe reads by CRLF
        gradlew text eol=lf
        mvnw text eol=lf
        *.cmd text eol=crlf
        *.bat text eol=crlf

        # Java's and Kotlin's diffs headed by the method a change is in
        *.java diff=java
        *.kt diff=kotlin
        *.kts diff=kotlin

        # Binary files, never diffed or merged as text
        *.jar binary
        *.war binary
        *.ear binary
        *.class binary
        *.png binary
        *.jpg binary
        *.jpeg binary
        *.gif binary
        *.bmp binary
        *.ico binary
        *.webp binary
        *.ttf binary
        *.otf binary
        *.woff binary
        *.woff2 binary
        *.pdf binary
        *.zip binary
        *.gz binary
        *.tar binary
        *.jks binary
        *.p12 binary

        """;

    /// <summary>Every file with LF line breaks, a final one and UTF-8, indented by four spaces, YAML and JSON by two.</summary>
    public const string EditorConfig = """
        root = true

        [*]
        charset = utf-8
        end_of_line = lf
        insert_final_newline = true
        indent_style = space
        indent_size = 4

        [*.{yml,yaml,json}]
        indent_size = 2

        [*.{cmd,bat}]
        end_of_line = crlf

        """;

    /// <summary>Writes each file into a folder, the .editorconfig first so the others take its line breaks, each only where
    /// no file is there yet: those it wrote, by name, else why one could not be. Off the window's thread.</summary>
    public static Task<(IReadOnlyList<string> Written, string? Why)> WriteAsync(string folder, IReadOnlyList<RepositoryFile> files) =>
        BackgroundWork.Run(() =>
        {
            List<string> written = [];
            foreach (RepositoryFile file in files.OrderBy(f => f == RepositoryFile.EditorConfig ? 0 : 1))
            {
                try
                {
                    if (FileText.SaveNewOnly(Path.Combine(folder, NameOf(file)), TextOf(file)) is not null)
                    {
                        written.Add(NameOf(file));
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return ((IReadOnlyList<string>)written, $"{NameOf(file)} could not be written: {error.Message}");
                }
            }
            return ((IReadOnlyList<string>)written, (string?)null);
        });
}
