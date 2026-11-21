using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.Tests;

/// <summary>IntelliJ's path variables and global libraries, read from its configuration folders newest product first,
/// with Wayfarer's own path variables first.</summary>
public sealed class IdeaPlatformTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("wayfarer-idea-platform-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void PathVariablesAndGlobalLibrariesComeFromTheNewestProductAndWayfarersOwnFirst()
    {
        string config = Path.Combine(root, "JetBrains");
        Options(Path.Combine(config, "IntelliJIdea2024.3"), "<macro name=\"LIBS\" value=\"/old/libs\" /><macro name=\"TOOLS\" value=\"$USER_HOME$/tools\" />",
            "<library name=\"Shared\"><CLASSES><root url=\"jar://$LIBS$/old.jar!/\" /></CLASSES></library>");
        Options(Path.Combine(config, "IntelliJIdea2025.2"), "<macro name=\"LIBS\" value=\"/new/libs\" />",
            "<library name=\"Shared\"><CLASSES><root url=\"jar://$LIBS$/shared.jar!/\" /></CLASSES></library>"
            + "<library name=\"junit\" type=\"repository\"><properties maven-id=\"junit:junit:4.13.2\" include-transitive-deps=\"false\" /><CLASSES /></library>");
        IdeaPlatform platform = new(Path.Combine(root, "state", "path-variables.json"), config);

        IReadOnlyDictionary<string, string> variables = platform.PathVariables();
        Assert.Equal("/new/libs", variables["LIBS"]);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) + "/tools", variables["TOOLS"]);
        IReadOnlyList<IdeaLibrary> libraries = platform.GlobalLibraries();
        Assert.Equal(["Shared", "junit"], libraries.Select(library => library.Name));
        Assert.Equal(["jar://$LIBS$/shared.jar!/"], libraries[0].Classes);
        Assert.Equal(("junit:junit:4.13.2", (bool?)false), (libraries[1].MavenId, libraries[1].Transitive));

        platform.SetPathVariable("LIBS", "/mine");
        Assert.Equal("/mine", new IdeaPlatform(Path.Combine(root, "state", "path-variables.json"), config).PathVariables()["LIBS"]);
        platform.SetPathVariable("LIBS", null);
        Assert.Equal("/new/libs", platform.PathVariables()["LIBS"]);
    }

    private static void Options(string product, string macros, string libraries)
    {
        string options = Directory.CreateDirectory(Path.Combine(product, "options")).FullName;
        File.WriteAllText(Path.Combine(options, "path.macros.xml"), $"<application><component name=\"PathMacrosImpl\">{macros}</component></application>");
        File.WriteAllText(Path.Combine(options, "applicationLibraries.xml"), $"<application><component name=\"libraryTable\">{libraries}</component></application>");
    }
}
