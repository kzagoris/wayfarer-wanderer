package wayfarer.host;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Properties;

/** The JDK a build tool runs on during import, recorded as the snapshot's import toolchain. */
final class ImportJvms {
    private ImportJvms() {
    }

    /** Maven runs on the JDK the workspace records, else JAVA_HOME, else the host's own JVM. */
    static Protocol.Jvm forMaven(Protocol.ImportParams params) {
        Protocol.Jvm recorded = usable(params.jdk());
        if (recorded != null) {
            return recorded;
        }
        Protocol.Jvm environment = usable(System.getenv("JAVA_HOME"));
        return environment != null ? environment : Jvms.current();
    }

    /**
     * The JVM the Gradle daemon runs on: the version gradle/gradle-daemon-jvm.properties asks for; else none, where
     * gradle.properties names org.gradle.java.home (Gradle honours it itself); else the JDK the workspace records, else
     * JAVA_HOME, as the wrapper on the command line would take it; else the host's own JVM.
     */
    static Protocol.Jvm forGradle(Path root, Protocol.ImportParams params) {
        Properties criteria = GradleEvaluator.properties(root.resolve("gradle").resolve("gradle-daemon-jvm.properties"));
        String wanted = criteria.getProperty("toolchainVersion");
        if (wanted != null && !wanted.isBlank()) {
            int feature = Jvms.feature(wanted.strip());
            for (Protocol.Jvm candidate : candidates(params)) {
                if (candidate.feature() != null && candidate.feature() == feature) {
                    return candidate;
                }
            }
            throw new Problems.Problem("gradle/gradle-daemon-jvm.properties asks for Java " + feature
                    + " to run Gradle, and no JDK " + feature + " was found. Install one, or record it as the project's JDK.");
        }
        if (GradleEvaluator.properties(root.resolve("gradle.properties")).getProperty("org.gradle.java.home") != null) {
            return null;
        }
        Protocol.Jvm recorded = usable(params.jdk());
        if (recorded != null) {
            return recorded;
        }
        Protocol.Jvm environment = usable(System.getenv("JAVA_HOME"));
        return environment != null ? environment : Jvms.current();
    }

    /** A JDK home that has a java to run and a release file that says its version; null otherwise. */
    static Protocol.Jvm usable(String home) {
        if (home == null || home.isBlank()) {
            return null;
        }
        Path path = Path.of(home).toAbsolutePath().normalize();
        String java = MavenEvaluator.isWindows() ? "java.exe" : "java";
        if (!Files.isRegularFile(path.resolve("bin").resolve(java))) {
            return null;
        }
        Protocol.Jvm jvm = Jvms.at(path);
        return jvm.version() == null ? null : jvm;
    }

    /**
     * The JDKs the workspace and environment name, the host's own, then those the IDE's table holds. The host never
     * searches the disk for JDKs: the IDE sends its table with each import.
     */
    static List<Protocol.Jvm> candidates(Protocol.ImportParams params) {
        List<Protocol.Jvm> found = new ArrayList<>();
        add(found, usable(params.jdk()));
        add(found, usable(System.getenv("JAVA_HOME")));
        add(found, usable(System.getProperty("java.home")));
        if (params.jdks() != null) {
            for (Protocol.Jdk jdk : params.jdks()) {
                add(found, usable(jdk.home()));
            }
        }
        return found;
    }

    /** The home of the JDK the IDE's table holds under {@code name} (IntelliJ's names included), while it is usable. */
    static String named(String name, List<Protocol.Jdk> jdks) {
        if (name == null || name.isBlank() || jdks == null) {
            return null;
        }
        for (Protocol.Jdk jdk : jdks) {
            if (name.equalsIgnoreCase(jdk.name()) && usable(jdk.home()) != null) {
                return usable(jdk.home()).home();
            }
        }
        return null;
    }

    private static void add(List<Protocol.Jvm> found, Protocol.Jvm jvm) {
        if (jvm != null && found.stream().noneMatch(known -> known.home().equalsIgnoreCase(jvm.home()))) {
            found.add(jvm);
        }
    }
}
