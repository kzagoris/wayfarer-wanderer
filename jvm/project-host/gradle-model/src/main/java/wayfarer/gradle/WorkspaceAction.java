package wayfarer.gradle;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import org.gradle.tooling.BuildAction;
import org.gradle.tooling.BuildController;
import org.gradle.tooling.model.gradle.BasicGradleProject;
import org.gradle.tooling.model.gradle.GradleBuild;

/**
 * Runs in the daemon: asks Wayfarer's builder for every project of the build and of the builds it includes, and
 * returns their models as JSON, in the build's own order.
 */
public final class WorkspaceAction implements BuildAction<List<String>> {
    private static final long serialVersionUID = 1L;

    @Override
    public List<String> execute(BuildController controller) {
        List<String> models = new ArrayList<>();
        visit(controller.getBuildModel(), controller, models, new HashSet<>());
        return models;
    }

    private static void visit(GradleBuild build, BuildController controller, List<String> models, Set<String> seen) {
        if (!seen.add(build.getBuildIdentifier().getRootDir().getAbsolutePath())) {
            return;
        }
        for (BasicGradleProject project : build.getProjects()) {
            WayfarerModel model = controller.findModel(project, WayfarerModel.class);
            if (model != null) {
                models.add(model.getJson());
            }
        }
        for (GradleBuild included : build.getIncludedBuilds()) {
            visit(included, controller, models, seen);
        }
    }
}
