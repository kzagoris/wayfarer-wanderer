package wayfarer.maven;

import java.util.LinkedHashSet;
import java.util.Set;
import org.apache.maven.model.building.ModelProblem;
import org.apache.maven.project.DependencyResolutionException;
import org.apache.maven.project.ProjectBuildingException;
import org.apache.maven.project.ProjectBuildingResult;

/** Maven's failures in the words the window shows: the problems themselves, without Maven's advice to rerun with -X. */
final class Problems {
    private Problems() {
    }

    static String of(ProjectBuildingException failure) {
        Set<String> said = new LinkedHashSet<>();
        if (failure.getResults() != null) {
            for (ProjectBuildingResult result : failure.getResults()) {
                for (ModelProblem problem : result.getProblems()) {
                    if (problem.getSeverity() != ModelProblem.Severity.WARNING) {
                        String where = problem.getSource() == null || problem.getSource().isEmpty() ? "" : " (" + problem.getSource()
                                + (problem.getLineNumber() > 0 ? ", line " + problem.getLineNumber() : "") + ")";
                        said.add(problem.getMessage() + where);
                    }
                }
            }
        }
        return said.isEmpty() ? deepest(failure) : String.join("; ", said);
    }

    static String of(DependencyResolutionException failure) {
        // Maven's own message names the project and each artifact that did not resolve.
        return failure.getMessage() == null ? deepest(failure) : failure.getMessage().strip();
    }

    private static String deepest(Throwable failure) {
        String said = failure.getMessage();
        for (Throwable cause = failure.getCause(); cause != null && cause != cause.getCause(); cause = cause.getCause()) {
            if (cause.getMessage() != null && !cause.getMessage().isBlank()) {
                said = cause.getMessage();
            }
        }
        return said == null ? failure.getClass().getSimpleName() : said.strip();
    }
}
