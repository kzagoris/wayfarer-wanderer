using Avalonia.Headless.XUnit;
using Passenger.Core;
using WayfarerWanderer.App;
using WayfarerWanderer.Jvm;
using Xunit;

namespace WayfarerWanderer.Tests;

/// <summary>Project Structure on an IntelliJ project of two modules: its pages, a library added to a module in test scope
/// (its .iml and .idea/libraries written), an unset path variable shown as a problem and set from there, the JDK table with
/// a missing JDK, and the folder imported again when the dialog closes having changed something.</summary>
[Collection(JvmTrustCollection.Name)]
public class ProjectStructureDialogTests
{
    [AvaloniaFact]
    public async Task AnIntelliJProjectsStructureIsShownChangedAndImportedAgain()
    {
        string root = WorkspaceImportTests.Copy("idea/multi");
        string variable = "WAYFARER_TEST_LIBS_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        string libs = Directory.CreateDirectory(Path.Combine(root, "shared")).FullName;
        File.WriteAllText(Path.Combine(libs, "shared.jar"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root, ".idea", "libraries"));
        File.WriteAllText(Path.Combine(root, ".idea", "libraries", "shared.xml"),
            $"<component name=\"libraryTable\"><library name=\"shared\"><CLASSES><root url=\"jar://${variable}$/shared.jar!/\" /></CLASSES></library></component>");
        IdeaProjectWriter.AddDependency(Path.Combine(root, "core", "core.iml"), "shared");
        string gone = Directory.CreateTempSubdirectory("wayfarer-gone-jdk-").FullName;
        Directory.CreateDirectory(Path.Combine(gone, "bin"));
        File.WriteAllText(Path.Combine(gone, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), string.Empty);
        File.WriteAllText(Path.Combine(gone, "release"), "JAVA_VERSION=\"11.0.99\"\nIMPLEMENTOR=\"Azul Systems, Inc.\"\n");
        string goneName = "gone-" + Guid.NewGuid().ToString("N")[..6];
        JdkRegistry.Current.Add(gone, goneName);
        Directory.Delete(gone, recursive: true);
        WorkspaceTrust.Trust(root);
        EditorWindow window = EditorApplication.CreateWindow(null);
        window.Show();
        try
        {
            await window.OpenWorkspaceAsync(root, TestContext.Current.CancellationToken);
            WorkspaceSnapshot before = window.Session!.Snapshot!;
            ProjectStructureDialog dialog = new(root, before);
            Assert.Equal([ProjectStructureDialog.ProjectPage, ProjectStructureDialog.ModulesPage, ProjectStructureDialog.LibrariesPage,
                ProjectStructureDialog.JdksPage, ProjectStructureDialog.ProblemsPage], dialog.Pages);
            Assert.Equal("17", dialog.ProjectJdkName());

            dialog.Page = ProjectStructureDialog.ProblemsPage;
            Assert.Contains(dialog.Problems(), said => said.Contains($"path variable {variable} is not set", StringComparison.Ordinal));
            Assert.Contains("Set Path Variable…", dialog.Shown);
            dialog.SetPathVariable(variable, libs);

            JvmModule app = before.Module("app")!;
            string tools = Path.Combine(root, "tools.jar");
            File.WriteAllText(tools, string.Empty);
            dialog.AddJarsLibrary(ProjectStructureDialog.ImlOf(app), "tools", [tools], "TEST");
            Assert.Contains(ProjectStructureDialog.OrderEntries(ProjectStructureDialog.ImlOf(app)), entry => entry == ("library", "tools", "TEST"));
            dialog.Page = ProjectStructureDialog.ModulesPage;
            Assert.Contains("Add from Maven…", dialog.Shown);
            Assert.DoesNotContain("Open Build File", dialog.Shown);

            dialog.Page = ProjectStructureDialog.JdksPage;
            Assert.Contains(dialog.JdkRows(), row => row.StartsWith($"{goneName}    missing", StringComparison.Ordinal));
            dialog.RemoveJdk(goneName, deleteFiles: false);
            Assert.DoesNotContain(dialog.JdkRows(), row => row.StartsWith(goneName, StringComparison.Ordinal));
            Assert.True(dialog.Changed);

            // The window imports again as the dialog closes having changed something.
            window.ShowStructure = shown => Task.FromResult(dialog.Changed);
            await window.ShowProjectStructureAsync();
            // The build-input watcher may have started its own import of the changed files, which then wins.
            for (int i = 0; i < 100 && (window.Session!.IsStale || window.Session.Snapshot!.Generation <= before.Generation); i++)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
            WorkspaceSnapshot after = window.Session!.Snapshot!;
            Assert.True(after.Generation > before.Generation);
            Assert.True(after.Module("core")!.Classpath.Compile.Any(path => PathNames.Same(path, Path.Combine(libs, "shared.jar"))),
                $"{after.Module("core")!.Problem} | {string.Join(",", after.Module("core")!.Classpath.Compile)} | {string.Join(",", after.Module("core")!.AbsentLibraries)}");
            Assert.Null(after.Module("core")!.Problem);
            // A test library: on no main classpath (app has no test folder, so no test classpath either).
            Assert.DoesNotContain(after.Module("app")!.Classpath.Compile, path => PathNames.Same(path, tools));
            Assert.DoesNotContain(after.Module("app")!.Classpath.Runtime, path => PathNames.Same(path, tools));
        }
        finally
        {
            window.Close();
            IdeaPlatform.Current.SetPathVariable(variable, null);
            JdkRegistry.Current.Remove(goneName);
            await ProjectHosts.ReleaseAsync(root);
            WorkspaceTrust.TakeBack(WorkspaceTrust.FolderToTrust(root) ?? root);
        }
    }
}
