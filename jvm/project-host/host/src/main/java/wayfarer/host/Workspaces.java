package wayfarer.host;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;

/**
 * Finds which build tool owns a workspace and asks its evaluator for the model: Maven where there is a pom.xml, else
 * Gradle where there is a settings or build script, else an IntelliJ module where the folder holds its .iml.
 */
public final class Workspaces {
    private final MavenEvaluator maven = new MavenEvaluator();
    private final GradleEvaluator gradle = new GradleEvaluator();
    private final IdeaEvaluator idea = new IdeaEvaluator();

    public Protocol.WorkspaceResult importWorkspace(Protocol.ImportParams params, Host.Context context) {
        if (params.folder() == null) {
            throw new Problems.Problem("import was sent no folder");
        }
        Path folder = Path.of(params.folder()).toAbsolutePath().normalize();
        if (!Files.isDirectory(folder)) {
            throw new Problems.Problem(folder + " is not a folder");
        }
        if (Files.isRegularFile(folder.resolve("pom.xml"))) {
            return maven.evaluate(folder, params, context);
        }
        for (String name : new String[] {"settings.gradle.kts", "settings.gradle", "build.gradle.kts", "build.gradle"}) {
            if (Files.isRegularFile(folder.resolve(name))) {
                return gradle.evaluate(folder, params, context);
            }
        }
        if (IdeaEvaluator.owns(folder)) {
            return idea.evaluate(folder, params);
        }
        List<String> modules = IdeaEvaluator.moduleFolders(folder);
        if (!modules.isEmpty()) {
            throw new Problems.Problem(folder + " is a folder of IntelliJ projects, and has no .iml of its own. Open one of its modules: "
                    + String.join(", ", modules) + ". An IntelliJ module is a folder that contains its .iml file.");
        }
        throw new Problems.Problem(folder + " has no Maven build (pom.xml), no Gradle build (settings.gradle or build.gradle) and no "
                + "IntelliJ module file (.iml).");
    }
}
