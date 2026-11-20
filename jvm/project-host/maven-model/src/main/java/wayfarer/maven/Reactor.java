package wayfarer.maven;

import java.io.File;
import java.util.ArrayList;
import java.util.List;
import org.apache.maven.project.MavenProject;
import org.eclipse.aether.artifact.Artifact;
import org.eclipse.aether.repository.WorkspaceReader;
import org.eclipse.aether.repository.WorkspaceRepository;

/**
 * Resolves the reactor's own modules to their folders, as Maven's ReactorReader does in a build: a module's main
 * artifact to its output folder, its {@code tests} classifier to its test output folder, and its POM to its file.
 */
final class Reactor implements WorkspaceReader {
    private final WorkspaceRepository repository = new WorkspaceRepository("reactor", new Object());
    private final List<MavenProject> projects;

    Reactor(List<MavenProject> projects) {
        this.projects = projects;
    }

    @Override
    public WorkspaceRepository getRepository() {
        return repository;
    }

    @Override
    public File findArtifact(Artifact artifact) {
        MavenProject project = find(artifact.getGroupId(), artifact.getArtifactId(), artifact.getBaseVersion());
        if (project == null) {
            return null;
        }
        if ("pom".equals(artifact.getExtension())) {
            return project.getFile();
        }
        if ("tests".equals(artifact.getClassifier()) || "test-jar".equals(artifact.getProperty("type", ""))) {
            return new File(project.getBuild().getTestOutputDirectory());
        }
        return artifact.getClassifier().isEmpty() ? new File(project.getBuild().getOutputDirectory()) : null;
    }

    @Override
    public List<String> findVersions(Artifact artifact) {
        List<String> versions = new ArrayList<>();
        for (MavenProject project : projects) {
            if (project.getGroupId().equals(artifact.getGroupId()) && project.getArtifactId().equals(artifact.getArtifactId())) {
                versions.add(project.getVersion());
            }
        }
        return versions;
    }

    MavenProject find(String groupId, String artifactId, String version) {
        for (MavenProject project : projects) {
            if (project.getGroupId().equals(groupId) && project.getArtifactId().equals(artifactId)
                    && (version == null || project.getVersion().equals(version))) {
                return project;
            }
        }
        return null;
    }
}
