package wayfarer.host;

import java.nio.file.Files;
import java.nio.file.Path;

/** Finds which build tool owns a workspace and asks its evaluator for the model. */
public final class Workspaces {
    private final MavenEvaluator maven = new MavenEvaluator();
    private final GradleEvaluator gradle = new GradleEvaluator();

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
        throw new Problems.Problem("no build in " + folder + " that the project host evaluates");
    }
}
