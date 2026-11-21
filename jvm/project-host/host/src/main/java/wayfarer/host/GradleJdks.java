package wayfarer.host;

import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/**
 * The JDK homes Gradle is told about ({@code -Porg.gradle.java.installations.paths}), so a toolchain resolves to a JDK of
 * the IDE's table even where Gradle's own detection would not find it. A command-line {@code -P} replaces the value in
 * the person's and the project's gradle.properties, so theirs come first, then the table's homes. Auto-detection and
 * auto-download stay as the project sets them. The app merges the same way for the wrapper (BuildJdks.cs).
 */
final class GradleJdks {
    static final String PROPERTY = "org.gradle.java.installations.paths";

    private GradleJdks() {
    }

    /** The {@code -P} argument for {@code root}; null when the table holds no usable JDK. */
    static String argument(Path root, List<Protocol.Jdk> jdks, Path gradleUserHome) {
        List<String> paths = paths(root, jdks, gradleUserHome);
        return paths.isEmpty() ? null : "-P" + PROPERTY + "=" + String.join(",", paths);
    }

    static String argument(Path root, List<Protocol.Jdk> jdks) {
        return argument(root, jdks, gradleUserHome());
    }

    /** The person's paths, the project's, then the table's homes, each once; empty when the table is. */
    static List<String> paths(Path root, List<Protocol.Jdk> jdks, Path gradleUserHome) {
        List<String> homes = new ArrayList<>();
        if (jdks != null) {
            for (Protocol.Jdk jdk : jdks) {
                if (jdk.home() != null && !jdk.home().isBlank()) {
                    homes.add(jdk.home());
                }
            }
        }
        List<String> paths = new ArrayList<>();
        if (homes.isEmpty()) {
            return paths;
        }
        for (Path file : List.of(gradleUserHome.resolve("gradle.properties"), root.resolve("gradle.properties"))) {
            String theirs = GradleEvaluator.properties(file).getProperty(PROPERTY);
            if (theirs != null) {
                for (String path : theirs.split(",")) {
                    offer(paths, path);
                }
            }
        }
        homes.forEach(home -> offer(paths, home));
        return paths;
    }

    static Path gradleUserHome() {
        String home = System.getenv("GRADLE_USER_HOME");
        return home != null && !home.isBlank() ? Path.of(home) : Path.of(System.getProperty("user.home"), ".gradle");
    }

    private static void offer(List<String> paths, String path) {
        String trimmed = path.strip();
        if (trimmed.isEmpty()) {
            return;
        }
        boolean windows = MavenEvaluator.isWindows();
        String key = key(trimmed, windows);
        if (paths.stream().noneMatch(known -> key(known, windows).equals(key))) {
            paths.add(trimmed);
        }
    }

    private static String key(String path, boolean windows) {
        String normal = path.replace('\\', '/');
        while (normal.length() > 1 && normal.endsWith("/")) {
            normal = normal.substring(0, normal.length() - 1);
        }
        return windows ? normal.toLowerCase(Locale.ROOT) : normal;
    }
}
