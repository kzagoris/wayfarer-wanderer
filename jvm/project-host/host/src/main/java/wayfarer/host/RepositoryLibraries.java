package wayfarer.host;

import java.io.File;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import org.apache.maven.repository.internal.MavenRepositorySystemUtils;
import org.eclipse.aether.DefaultRepositorySystemSession;
import org.eclipse.aether.RepositorySystem;
import org.eclipse.aether.artifact.DefaultArtifact;
import org.eclipse.aether.collection.CollectRequest;
import org.eclipse.aether.graph.Dependency;
import org.eclipse.aether.repository.LocalRepository;
import org.eclipse.aether.repository.RemoteRepository;
import org.eclipse.aether.resolution.ArtifactRequest;
import org.eclipse.aether.resolution.ArtifactResult;
import org.eclipse.aether.resolution.DependencyRequest;
import org.eclipse.aether.resolution.DependencyResult;
import org.eclipse.aether.supplier.RepositorySystemSupplier;
import org.eclipse.aether.util.artifact.JavaScopes;
import org.eclipse.aether.util.filter.DependencyFilterUtils;

/**
 * Resolves IntelliJ repository libraries ({@code <library type="repository">} with a {@code maven-id}) whose jars are
 * not on disk, as IntelliJ does on a machine with an empty {@code ~/.m2}: with Maven Resolver, into the local
 * repository, from Maven Central (or the repositories {@code WAYFARER_MAVEN_REPOSITORIES} lists, comma separated).
 * Results are kept per coordinates for the host's life.
 */
final class RepositoryLibraries {
    static final String CENTRAL = "https://repo.maven.apache.org/maven2/";
    private static final Pattern LOCAL_REPOSITORY = Pattern.compile("<localRepository>\\s*([^<]+?)\\s*</localRepository>");

    private final Path local;
    private final List<String> remotes;
    private final Map<String, List<String>> resolved = new ConcurrentHashMap<>();
    private volatile RepositorySystem system;

    RepositoryLibraries(Path local, List<String> remotes) {
        this.local = local;
        this.remotes = remotes;
    }

    /** The person's local repository and the remotes the environment names, else Maven Central. */
    static RepositoryLibraries forThisMachine(Path local) {
        String listed = System.getenv("WAYFARER_MAVEN_REPOSITORIES");
        List<String> remotes = new ArrayList<>();
        if (listed != null && !listed.isBlank()) {
            for (String url : listed.split(",")) {
                if (!url.isBlank()) {
                    remotes.add(url.strip());
                }
            }
        }
        if (remotes.isEmpty()) {
            remotes.add(CENTRAL);
        }
        return new RepositoryLibraries(local, remotes);
    }

    /** {@code ~/.m2/settings.xml}'s localRepository, else {@code ~/.m2/repository}: what $MAVEN_REPOSITORY$ means. */
    static Path defaultLocal() {
        Path m2 = Path.of(System.getProperty("user.home"), ".m2");
        Path settings = m2.resolve("settings.xml");
        if (Files.isRegularFile(settings)) {
            try {
                Matcher found = LOCAL_REPOSITORY.matcher(Files.readString(settings));
                if (found.find() && !found.group(1).contains("${")) {
                    return Path.of(found.group(1));
                }
            } catch (java.io.IOException unreadable) {
                // The default below.
            }
        }
        return m2.resolve("repository");
    }

    Path local() {
        return local;
    }

    /** The jars of {@code coordinates} (group:artifact[:extension[:classifier]]:version), with its runtime dependencies
     * when {@code transitive}; a {@link Problems.Problem} naming why when it cannot be resolved. */
    List<String> resolve(String coordinates, boolean transitive) {
        String key = coordinates + (transitive ? "+" : "");
        List<String> known = resolved.get(key);
        if (known != null) {
            return known;
        }
        RepositorySystem repositories = system();
        DefaultRepositorySystemSession session = MavenRepositorySystemUtils.newSession();
        session.setLocalRepositoryManager(repositories.newLocalRepositoryManager(session, new LocalRepository(local.toFile())));
        List<RemoteRepository> remote = new ArrayList<>();
        for (int i = 0; i < remotes.size(); i++) {
            remote.add(new RemoteRepository.Builder(i == 0 ? "central" : "remote" + i, "default", remotes.get(i)).build());
        }
        List<String> jars = new ArrayList<>();
        try {
            DefaultArtifact artifact = new DefaultArtifact(coordinates);
            if (transitive) {
                CollectRequest collect = new CollectRequest(new Dependency(artifact, JavaScopes.RUNTIME), remote);
                DependencyResult result = repositories.resolveDependencies(session,
                        new DependencyRequest(collect, DependencyFilterUtils.classpathFilter(JavaScopes.RUNTIME)));
                for (ArtifactResult each : result.getArtifactResults()) {
                    add(jars, each.getArtifact().getFile());
                }
            } else {
                add(jars, repositories.resolveArtifact(session, new ArtifactRequest(artifact, remote, null)).getArtifact().getFile());
            }
        } catch (Exception failure) {
            throw new Problems.Problem("the repository library " + coordinates + " could not be downloaded: " + Problems.say(failure), failure);
        }
        resolved.put(key, jars);
        return jars;
    }

    private static void add(List<String> jars, File file) {
        if (file != null) {
            jars.add(file.getAbsolutePath());
        }
    }

    private RepositorySystem system() {
        RepositorySystem made = system;
        if (made == null) {
            synchronized (this) {
                if (system == null) {
                    system = new RepositorySystemSupplier().get();
                }
                made = system;
            }
        }
        return made;
    }
}
