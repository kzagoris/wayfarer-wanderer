package wayfarer.host;

import java.nio.file.Files;
import java.nio.file.Path;

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
}
