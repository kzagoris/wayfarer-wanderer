package wayfarer.host;

import com.google.gson.JsonElement;
import java.util.List;
import java.util.Map;

/**
 * The host's protocol, one UTF-8 JSON object per line on stdio, shaped like Passenger's project host. A request is
 * {@code {"id":N,"method":"…","params":{…}}}. A reply is {@code {"id":N,"partial":…}}, {@code {"id":N,"result":…}}
 * or {@code {"id":N,"error":"…"}}. The first line, id 0, says which protocol and JVM the host runs on, or why it cannot
 * start. Names in the model follow the Build Server Protocol: build targets, sources, dependency modules and JVM
 * environments. The golden examples under {@code protocol/} are read by the tests on both sides.
 */
public final class Protocol {
    public static final int VERSION = 1;

    public static final String IMPORT = "import";
    public static final String CANCEL = "cancel";
    public static final String PING = "ping";

    private Protocol() {
    }

    public record Request(int id, String method, JsonElement params) {
    }

    /** The first line's result. */
    public record Ready(int protocol, Jvm java, long pid) {
    }

    /** A JVM as the host knows it: its home, version string, feature release and vendor. */
    public record Jvm(String home, String version, Integer feature, String vendor) {
    }

    public record CancelParams(int request) {
    }

    /**
     * Import the workspace at {@code folder}. {@code jdk} is the JDK the workspace records for itself, if any,
     * {@code mavenProfiles} the profiles it asks for beyond Maven's own activation (a leading '-' turns one off), and
     * {@code buildTool} an installation to use where the workspace has no wrapper. {@code jdks} are the JDKs the IDE's
     * table holds, by name: the host chooses among these and never searches the disk for JDKs itself.
     */
    public record ImportParams(String folder, long generation, String jdk, List<String> mavenProfiles, String buildTool, List<Jdk> jdks) {
    }

    /** A JDK of the IDE's table: the name projects refer to it by, its home, version string, feature release and vendor. */
    public record Jdk(String name, String home, String version, Integer feature, String vendor) {
    }

    /** A partial answer to an import: one build target as soon as it is evaluated. */
    public record TargetEvaluated(BuildTarget target) {
    }

    /** The answer to an import: the whole model of one generation. */
    public record WorkspaceResult(
            long generation,
            String root,
            String buildSystem,
            String buildFile,
            Jvm importJvm,
            List<BuildTarget> targets,
            List<BuildInput> buildInputs,
            String outputRoot) {
    }

    /** A file the evaluation read, stamped as it was read: last write in milliseconds since the epoch, and length (-1 absent). */
    public record BuildInput(String path, long modified, long length) {
    }

    /**
     * One module. {@code jvmBuildTarget} is the JDK it compiles with and {@code runEnvironment} the JDK it runs on;
     * {@code problem} says why part of it could not be evaluated while the rest stands.
     */
    public record BuildTarget(
            String id,
            String displayName,
            String baseDirectory,
            List<String> languageIds,
            JvmEnvironment jvmBuildTarget,
            JvmEnvironment runEnvironment,
            List<SourceSet> sourceSets,
            List<String> tasks,
            List<MainClass> mainClasses,
            List<String> activeProfiles,
            List<String> absentLibraries,
            String problem) {
    }

    public record JvmEnvironment(String javaHome, Integer javaVersion, String vendor) {
    }

    public record SourceSet(
            String id,
            String name,
            boolean test,
            List<SourceItem> sources,
            List<String> outputs,
            Map<String, List<DependencyModule>> dependencyModules) {
    }

    /** A source or resource root; {@code kind} is java, kotlin or resources. */
    public record SourceItem(String uri, String kind, boolean generated) {
    }

    /**
     * One classpath entry, in order: its file, and either the library coordinates (group:artifact:version[:classifier])
     * or the source set whose outputs it is.
     */
    public record DependencyModule(String path, String coordinates, String moduleRef) {
    }

    /** {@code origin} is configured (the build names it) or discovered (a source scan found it). */
    public record MainClass(String className, String sourcePath, String origin) {
    }
}
