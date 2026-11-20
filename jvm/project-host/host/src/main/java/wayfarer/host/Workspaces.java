package wayfarer.host;

import java.nio.file.Files;
import java.nio.file.Path;

/** Finds which build tool owns a workspace and asks its evaluator for the model. */
public final class Workspaces {
    private final MavenEvaluator maven = new MavenEvaluator();

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
        throw new Problems.Problem("no build in " + folder + " that the project host evaluates");
    }
}
