package wayfarer.host;

import com.google.gson.JsonObject;
import java.io.BufferedReader;
import java.io.File;
import java.io.IOException;
import java.io.InputStreamReader;
import java.net.MalformedURLException;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.TimeUnit;
import java.util.function.BooleanSupplier;
import java.util.function.Consumer;
import java.util.function.Function;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.stream.Stream;

/**
 * Evaluates a Maven workspace with the Maven its wrapper selects: the wrapper is run once ({@code mvnw -v}) so it
 * downloads its distribution and says where it is, then that distribution's {@code lib} folder is loaded beside
 * Wayfarer's {@code maven-model.jar} in a class loader of its own, one per Maven installation, whose model evaluates
 * the reactor in this process. A workspace without a wrapper uses the configured installation, else {@code mvn} on PATH.
 */
final class MavenEvaluator {
    private static final Pattern MAVEN_HOME = Pattern.compile("(?m)^Maven home:\\s*(.+?)\\s*$");
    private final Map<String, Function<Object[], String>> models = new ConcurrentHashMap<>();
    private final Map<String, String> homes = new ConcurrentHashMap<>();

    Protocol.WorkspaceResult evaluate(Path root, Protocol.ImportParams params, Host.Context context) {
        Protocol.Jvm jvm = ImportJvms.forMaven(params);
        String mavenHome = mavenHome(root, params, jvm, context.cancellation());
        Function<Object[], String> model = models.computeIfAbsent(mavenHome, MavenEvaluator::load);

        CommandLine command = CommandLine.read(root);
        JsonObject request = new JsonObject();
        request.addProperty("root", root.toString());
        request.addProperty("javaHome", jvm.home());
        request.addProperty("javaVersion", jvm.version());
        request.addProperty("localRepository", command.properties.getOrDefault("maven.repo.local", null));
        request.addProperty("userSettings", command.userSettings != null ? command.userSettings
                : Path.of(System.getProperty("user.home"), ".m2", "settings.xml").toString());
        request.addProperty("globalSettings", command.globalSettings != null ? command.globalSettings
                : Path.of(mavenHome, "conf", "settings.xml").toString());
        request.addProperty("offline", command.offline);
        request.add("userProperties", Host.JSON.toJsonTree(command.properties));
        request.add("environment", Host.JSON.toJsonTree(System.getenv()));
        List<String> active = new ArrayList<>(command.activeProfiles);
        List<String> inactive = new ArrayList<>(command.inactiveProfiles);
        if (params.mavenProfiles() != null) {
            for (String profile : params.mavenProfiles()) {
                if (profile.startsWith("-") || profile.startsWith("!")) {
                    inactive.add(profile.substring(1));
                } else {
                    active.add(profile);
                }
            }
        }
        request.add("activeProfiles", Host.JSON.toJsonTree(active));
        request.add("inactiveProfiles", Host.JSON.toJsonTree(inactive));

        List<Protocol.BuildTarget> targets = new ArrayList<>();
        Consumer<String> partial = json -> {
            Protocol.BuildTarget target = MainScan.withDiscovered(Host.JSON.fromJson(json, Protocol.BuildTarget.class));
            targets.add(target);
            context.partial(new Protocol.TargetEvaluated(target));
        };
        BooleanSupplier cancelled = context.cancellation()::isCancelled;
        String answer;
        try {
            answer = model.apply(new Object[] {Host.JSON.toJson(request), partial, cancelled});
        } catch (RuntimeException failure) {
            context.cancellation().throwIfCancelled();
            throw new Problems.Problem("Maven could not evaluate " + root + ": " + Problems.say(failure), failure);
        }
        JsonObject rest = Host.JSON.fromJson(answer, JsonObject.class);
        List<String> inputs = new ArrayList<>();
        rest.getAsJsonArray("buildInputs").forEach(element -> inputs.add(element.getAsString()));
        return new Protocol.WorkspaceResult(params.generation(), root.toString(), "maven", rest.get("buildFile").getAsString(),
                jvm, targets, BuildInputs.stamp(inputs), null);
    }

    /** The Maven home the wrapper selects, downloading it the first time; cached while the wrapper's properties stand. */
    private String mavenHome(Path root, Protocol.ImportParams params, Protocol.Jvm jvm, Cancellation cancellation) {
        Path wrapperProperties = root.resolve(".mvn").resolve("wrapper").resolve("maven-wrapper.properties");
        Path script = root.resolve(isWindows() ? "mvnw.cmd" : "mvnw");
        List<String> command;
        String key;
        if (Files.isRegularFile(script) && Files.isRegularFile(wrapperProperties)) {
            command = isWindows() ? List.of("cmd.exe", "/c", script.toString(), "-v") : List.of("sh", script.toString(), "-v");
            key = wrapperProperties + "|" + stamp(wrapperProperties);
        } else {
            String mvn = configuredMaven(params.buildTool());
            if (mvn == null) {
                throw new Problems.Problem(root + " has no Maven wrapper (mvnw and .mvn/wrapper), and no Maven installation is "
                        + "configured or on PATH. Add the wrapper with 'mvn wrapper:wrapper', or choose a Maven installation in Settings.");
            }
            command = isWindows() && mvn.toLowerCase().endsWith(".cmd") ? List.of("cmd.exe", "/c", mvn, "-v") : List.of(mvn, "-v");
            key = mvn;
        }
        String cached = homes.get(key);
        if (cached != null && Files.isDirectory(Path.of(cached, "lib"))) {
            return cached;
        }
        ProcessBuilder builder = new ProcessBuilder(command).directory(root.toFile()).redirectErrorStream(true);
        builder.environment().put("JAVA_HOME", jvm.home());
        // Only the distribution is wanted here; MAVEN_OPTS from the environment is read by the evaluation itself, and a
        // malformed one would stop the download.
        builder.environment().remove("MAVEN_OPTS");
        StringBuilder output = new StringBuilder();
        try {
            Process process = builder.start();
            cancellation.onCancel(process::destroyForcibly);
            try (BufferedReader lines = new BufferedReader(new InputStreamReader(process.getInputStream(), StandardCharsets.UTF_8))) {
                String line;
                while ((line = lines.readLine()) != null) {
                    output.append(line).append('\n');
                    System.err.println("wayfarer maven: " + line);
                }
            }
            if (!process.waitFor(10, TimeUnit.MINUTES)) {
                process.destroyForcibly();
            }
            cancellation.throwIfCancelled();
            Matcher home = MAVEN_HOME.matcher(output);
            if (process.exitValue() != 0 || !home.find()) {
                throw new Problems.Problem("The Maven wrapper in " + root + " did not start Maven (exit " + process.exitValue() + "): "
                        + lastLines(output.toString(), 5));
            }
            String found = Path.of(home.group(1)).toAbsolutePath().normalize().toString();
            homes.put(key, found);
            return found;
        } catch (IOException failure) {
            throw new Problems.Problem("The Maven wrapper in " + root + " could not run: " + failure.getMessage(), failure);
        } catch (InterruptedException interrupted) {
            Thread.currentThread().interrupt();
            throw new Cancellation.Cancelled();
        }
    }

    private static String configuredMaven(String buildTool) {
        String name = isWindows() ? "mvn.cmd" : "mvn";
        if (buildTool != null && !buildTool.isBlank()) {
            Path tool = Path.of(buildTool);
            Path inHome = tool.resolve("bin").resolve(name);
            return Files.isRegularFile(inHome) ? inHome.toString() : Files.isRegularFile(tool) ? tool.toString() : null;
        }
        String path = System.getenv("PATH");
        if (path == null) {
            return null;
        }
        for (String folder : path.split(File.pathSeparator)) {
            Path candidate = Path.of(folder.replace("\"", "")).resolve(name);
            if (Files.isRegularFile(candidate)) {
                return candidate.toString();
            }
        }
        return null;
    }

    /** Maven's own lib folder, Wayfarer's maven-model.jar and the host's JSON library, over the platform's classes alone. */
    @SuppressWarnings("unchecked")
    private static Function<Object[], String> load(String mavenHome) {
        List<URL> urls = new ArrayList<>();
        try {
            Path stage = Stage.folder();
            urls.add(stage.resolve("maven-model.jar").toUri().toURL());
            try (Stream<Path> lib = Files.list(stage.resolve("lib"))) {
                for (Path jar : lib.filter(p -> p.getFileName().toString().startsWith("gson-")).toList()) {
                    urls.add(jar.toUri().toURL());
                }
            }
            for (String folder : List.of("lib", "lib/ext", "boot")) {
                Path dir = Path.of(mavenHome, folder);
                if (!Files.isDirectory(dir)) {
                    continue;
                }
                try (Stream<Path> jars = Files.list(dir)) {
                    for (Path jar : jars.filter(p -> p.getFileName().toString().endsWith(".jar")).sorted().toList()) {
                        urls.add(jar.toUri().toURL());
                    }
                }
            }
            URLClassLoader loader = new URLClassLoader("maven " + mavenHome, urls.toArray(URL[]::new), ClassLoader.getPlatformClassLoader());
            return (Function<Object[], String>) loader.loadClass("wayfarer.maven.MavenModel").getDeclaredConstructor().newInstance();
        } catch (MalformedURLException failure) {
            throw new Problems.Problem("The Maven installation at " + mavenHome + " could not be loaded: " + failure.getMessage(), failure);
        } catch (IOException | ReflectiveOperationException | LinkageError failure) {
            throw new Problems.Problem("The Maven installation at " + mavenHome + " could not be loaded: " + Problems.say(failure), failure);
        }
    }

    private static String stamp(Path file) {
        try {
            return Files.getLastModifiedTime(file).toMillis() + ":" + Files.size(file);
        } catch (IOException missing) {
            return "absent";
        }
    }

    private static String lastLines(String text, int count) {
        List<String> lines = text.strip().lines().toList();
        return String.join(" | ", lines.subList(Math.max(0, lines.size() - count), lines.size()));
    }

    static boolean isWindows() {
        return System.getProperty("os.name", "").toLowerCase().startsWith("windows");
    }

    /**
     * What Maven's command line would have taken from {@code .mvn/maven.config} and {@code MAVEN_OPTS}: system
     * properties, profiles, settings files and offline mode.
     */
    static final class CommandLine {
        final Map<String, String> properties = new LinkedHashMap<>();
        final List<String> activeProfiles = new ArrayList<>();
        final List<String> inactiveProfiles = new ArrayList<>();
        String userSettings;
        String globalSettings;
        boolean offline;

        static CommandLine read(Path root) {
            CommandLine line = new CommandLine();
            String opts = System.getenv("MAVEN_OPTS");
            if (opts != null) {
                line.accept(Words.split(opts), root);
            }
            Path config = root.resolve(".mvn").resolve("maven.config");
            if (Files.isRegularFile(config)) {
                try {
                    line.accept(Words.split(Files.readString(config)), root);
                } catch (IOException unreadable) {
                    System.err.println("wayfarer maven: could not read " + config + ": " + unreadable);
                }
            }
            return line;
        }

        private void accept(List<String> words, Path root) {
            for (int i = 0; i < words.size(); i++) {
                String word = words.get(i);
                String next = i + 1 < words.size() ? words.get(i + 1) : null;
                if (word.startsWith("-D") && word.length() > 2) {
                    String property = word.substring(2);
                    int equals = property.indexOf('=');
                    properties.put(equals < 0 ? property : property.substring(0, equals), equals < 0 ? "true" : property.substring(equals + 1));
                } else if ((word.equals("-P") || word.equals("--activate-profiles")) && next != null) {
                    profiles(next);
                    i++;
                } else if (word.startsWith("-P") && word.length() > 2) {
                    profiles(word.substring(2));
                } else if ((word.equals("-s") || word.equals("--settings")) && next != null) {
                    userSettings = root.resolve(next).toString();
                    i++;
                } else if ((word.equals("-gs") || word.equals("--global-settings")) && next != null) {
                    globalSettings = root.resolve(next).toString();
                    i++;
                } else if (word.equals("-o") || word.equals("--offline")) {
                    offline = true;
                }
            }
        }

        private void profiles(String list) {
            for (String profile : list.split(",")) {
                String name = profile.strip();
                if (name.startsWith("!") || name.startsWith("-")) {
                    inactiveProfiles.add(name.substring(1));
                } else if (!name.isEmpty()) {
                    activeProfiles.add(name.startsWith("+") ? name.substring(1) : name);
                }
            }
        }
    }
}
