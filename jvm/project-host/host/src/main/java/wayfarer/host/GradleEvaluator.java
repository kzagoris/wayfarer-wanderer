package wayfarer.host;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Properties;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import org.gradle.tooling.BuildActionExecuter;
import org.gradle.tooling.CancellationTokenSource;
import org.gradle.tooling.GradleConnectionException;
import org.gradle.tooling.GradleConnector;
import org.gradle.tooling.ProjectConnection;
import org.gradle.tooling.model.build.BuildEnvironment;
import org.gradle.util.GradleVersion;
import wayfarer.gradle.WorkspaceAction;

/**
 * Evaluates a Gradle workspace through the Tooling API, on the Gradle its wrapper selects: an init script applies
 * Wayfarer's model plugin to each build, whose builder returns exactly Wayfarer's model per project (never the
 * built-in Eclipse or IDEA models), and one build action collects every project of the build and of the builds it
 * includes. Connections stay open per workspace so a reimport finds the daemon warm.
 */
final class GradleEvaluator {
    static final GradleVersion MINIMUM = GradleVersion.version("8.0");

    private final Map<Path, ProjectConnection> connections = new ConcurrentHashMap<>();
    private volatile Path initScript;

    Protocol.WorkspaceResult evaluate(Path root, Protocol.ImportParams params, Host.Context context) {
        Protocol.Jvm daemonJvm = ImportJvms.forGradle(root, params);
        ProjectConnection connection = connections.computeIfAbsent(root, folder -> connect(folder, params));
        CancellationTokenSource cancel = GradleConnector.newCancellationTokenSource();
        context.cancellation().onCancel(cancel::cancel);
        try {
            BuildEnvironment environment = connection.model(BuildEnvironment.class)
                    .setJavaHome(daemonJvm == null ? null : Path.of(daemonJvm.home()).toFile())
                    .withCancellationToken(cancel.token())
                    .setStandardOutput(System.err)
                    .setStandardError(System.err)
                    .get();
            GradleVersion version = GradleVersion.version(environment.getGradle().getGradleVersion());
            if (version.getBaseVersion().compareTo(MINIMUM) < 0) {
                throw new Problems.Problem("Gradle " + version.getVersion() + " is older than " + MINIMUM.getVersion()
                        + ", which Wayfarer imports. Update the wrapper with 'gradle wrapper --gradle-version " + MINIMUM.getVersion() + "' or later.");
            }
            Protocol.Jvm importJvm = Jvms.at(environment.getJava().getJavaHome().toPath());
            List<String> arguments = new ArrayList<>(List.of("--init-script", initScript().toString()));
            // The IDE's JDKs, so a toolchain resolves to one Gradle's own detection would miss.
            String jdkPaths = GradleJdks.argument(root, params.jdks());
            if (jdkPaths != null) {
                arguments.add(jdkPaths);
            }
            BuildActionExecuter<List<String>> action = connection.action(new WorkspaceAction())
                    .withArguments(arguments)
                    .withCancellationToken(cancel.token())
                    .setStandardOutput(System.err)
                    .setStandardError(System.err);
            if (daemonJvm != null) {
                action.setJavaHome(Path.of(daemonJvm.home()).toFile());
            }
            List<String> models = action.run();
            List<Protocol.BuildTarget> targets = new ArrayList<>();
            Set<String> inputs = new LinkedHashSet<>(rootInputs(root));
            for (String json : models) {
                JsonObject model = Host.JSON.fromJson(json, JsonObject.class);
                JsonElement buildFiles = model.remove("buildFiles");
                if (buildFiles instanceof JsonArray files) {
                    files.forEach(file -> inputs.add(file.getAsString()));
                }
                Protocol.BuildTarget target = withImportHome(Host.JSON.fromJson(model, Protocol.BuildTarget.class), importJvm);
                target = MainScan.withDiscovered(target);
                targets.add(target);
                context.partial(new Protocol.TargetEvaluated(target));
            }
            String settings = Files.isRegularFile(root.resolve("settings.gradle.kts")) ? root.resolve("settings.gradle.kts").toString()
                    : Files.isRegularFile(root.resolve("settings.gradle")) ? root.resolve("settings.gradle").toString()
                    : Files.isRegularFile(root.resolve("build.gradle.kts")) ? root.resolve("build.gradle.kts").toString()
                    : root.resolve("build.gradle").toString();
            return new Protocol.WorkspaceResult(params.generation(), root.toString(), "gradle", settings, importJvm, targets,
                    BuildInputs.stamp(inputs), null);
        } catch (GradleConnectionException | IllegalStateException failure) {
            context.cancellation().throwIfCancelled();
            if (failure.getClass().getSimpleName().contains("Cancel")) {
                throw new Cancellation.Cancelled();
            }
            // A broken connection is not kept for the next import.
            connections.remove(root);
            closeQuietly(connection);
            throw new Problems.Problem(say(root, failure), failure);
        }
    }

    private ProjectConnection connect(Path root, Protocol.ImportParams params) {
        GradleConnector connector = GradleConnector.newConnector().forProjectDirectory(root.toFile());
        boolean wrapper = Files.isRegularFile(root.resolve("gradle").resolve("wrapper").resolve("gradle-wrapper.properties"));
        if (!wrapper) {
            if (params.buildTool() == null || params.buildTool().isBlank()) {
                throw new Problems.Problem(root + " has no Gradle wrapper (gradle/wrapper/gradle-wrapper.properties), and no Gradle "
                        + "installation is configured. Add the wrapper with 'gradle wrapper', or choose a Gradle installation in Settings.");
            }
            connector.useInstallation(Path.of(params.buildTool()).toFile());
        } else {
            connector.useBuildDistribution();
        }
        return connector.connect();
    }

    /** The JDK homes a project's toolchain names are its own; a module without one compiles on the import JDK. */
    private static Protocol.BuildTarget withImportHome(Protocol.BuildTarget target, Protocol.Jvm importJvm) {
        Protocol.JvmEnvironment compile = fill(target.jvmBuildTarget(), importJvm);
        Protocol.JvmEnvironment run = fill(target.runEnvironment(), importJvm);
        return new Protocol.BuildTarget(target.id(), target.displayName(), target.baseDirectory(), target.languageIds(), compile, run,
                target.sourceSets(), target.tasks(), target.mainClasses(), target.activeProfiles(), target.absentLibraries(), target.problem());
    }

    private static Protocol.JvmEnvironment fill(Protocol.JvmEnvironment environment, Protocol.Jvm importJvm) {
        if (environment == null) {
            return null;
        }
        Integer version = environment.javaVersion() != null ? environment.javaVersion() : importJvm.feature();
        if (environment.javaHome() == null && environment.required() != null) {
            // A toolchain Gradle did not find stays without a home: the host looks for it in the IDE's table next.
            return new Protocol.JvmEnvironment(null, version, environment.vendor(), environment.required());
        }
        String home = environment.javaHome() != null ? environment.javaHome() : importJvm.home();
        return new Protocol.JvmEnvironment(home, version, environment.vendor() != null ? environment.vendor() : Jvms.at(Path.of(home)).vendor(),
                environment.required());
    }

    private static List<String> rootInputs(Path root) {
        List<String> inputs = new ArrayList<>();
        for (String name : List.of("settings.gradle.kts", "settings.gradle", "gradle.properties", "gradle/libs.versions.toml",
                "gradle/wrapper/gradle-wrapper.properties", "gradle/gradle-daemon-jvm.properties")) {
            inputs.add(root.resolve(name).toString());
        }
        String userHome = System.getenv("GRADLE_USER_HOME");
        Path gradleHome = userHome != null && !userHome.isBlank() ? Path.of(userHome) : Path.of(System.getProperty("user.home"), ".gradle");
        inputs.add(gradleHome.resolve("gradle.properties").toString());
        return inputs;
    }

    /** The init script, written once per host: it puts the staged gradle-model.jar on the build's init classpath. */
    private Path initScript() {
        Path script = initScript;
        if (script != null && Files.isRegularFile(script)) {
            return script;
        }
        synchronized (this) {
            if (initScript != null && Files.isRegularFile(initScript)) {
                return initScript;
            }
            Path jar = Stage.folder().resolve("lib").resolve("gradle-model.jar");
            String path = jar.toAbsolutePath().toString().replace("\\", "/");
            String text = "initscript {\n"
                    + "    dependencies { classpath files('" + path.replace("'", "\\'") + "') }\n"
                    + "}\n"
                    + "// Wayfarer Wanderer: registers the builder of its project model in each project (the registry is per project).\n"
                    + "allprojects {\n"
                    + "    apply plugin: wayfarer.gradle.WayfarerModelPlugin\n"
                    + "}\n";
            try {
                Path written = Files.createTempFile("wayfarer-model-", ".gradle");
                Files.writeString(written, text, StandardCharsets.UTF_8);
                written.toFile().deleteOnExit();
                initScript = written;
                return written;
            } catch (IOException failure) {
                throw new Problems.Problem("the project host could not write its Gradle init script: " + failure.getMessage(), failure);
            }
        }
    }

    private static String say(Path root, Throwable failure) {
        // The Tooling API wraps the build's own failure; its deepest messages say what is wrong in the build.
        List<String> messages = new ArrayList<>();
        for (Throwable cause = failure; cause != null && cause != cause.getCause(); cause = cause.getCause()) {
            String message = cause.getMessage();
            if (message != null && !message.isBlank() && messages.stream().noneMatch(message::contains)) {
                messages.add(message.strip());
            }
        }
        String detail = messages.isEmpty() ? failure.getClass().getSimpleName() : String.join(" > ", messages.subList(Math.max(0, messages.size() - 3), messages.size()));
        return "Gradle could not evaluate " + root + ": " + detail;
    }

    private static void closeQuietly(ProjectConnection connection) {
        try {
            connection.close();
        } catch (RuntimeException ignored) {
            // Gone already.
        }
    }

    static Properties properties(Path file) {
        Properties properties = new Properties();
        if (Files.isRegularFile(file)) {
            try (InputStream in = Files.newInputStream(file)) {
                properties.load(in);
            } catch (IOException unreadable) {
                System.err.println("wayfarer gradle: could not read " + file + ": " + unreadable);
            }
        }
        return properties;
    }
}
