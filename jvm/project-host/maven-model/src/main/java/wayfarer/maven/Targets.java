package wayfarer.maven;

import java.io.File;
import java.io.IOException;
import java.nio.file.DirectoryStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;
import org.apache.maven.model.Plugin;
import org.apache.maven.model.PluginExecution;
import org.apache.maven.model.Profile;
import org.apache.maven.model.Resource;
import org.apache.maven.project.DependencyResolutionResult;
import org.apache.maven.project.MavenProject;
import org.codehaus.plexus.util.xml.Xpp3Dom;
import org.eclipse.aether.artifact.Artifact;
import org.eclipse.aether.graph.Dependency;

/**
 * One Maven module as a build target: the roots its effective model and its well-known source plugins name
 * (build-helper's add-source, the Kotlin plugin's source folders, existing generated-sources folders), its main and
 * test source sets with their classpaths ordered as Maven orders them, its compiler release, configured mains and
 * active profiles. Field names follow the host's protocol.
 */
final class Targets {
    private static final String BUILD_HELPER = "org.codehaus.mojo:build-helper-maven-plugin";
    private static final String KOTLIN = "org.jetbrains.kotlin:kotlin-maven-plugin";
    private static final String COMPILER = "org.apache.maven.plugins:maven-compiler-plugin";
    private static final String TOOLCHAINS = "org.apache.maven.plugins:maven-toolchains-plugin";
    private static final String BOOT = "org.springframework.boot:spring-boot-maven-plugin";
    private static final String EXEC = "org.codehaus.mojo:exec-maven-plugin";

    private final Path root;
    private final List<MavenProject> projects;
    private final MavenModel.Request request;

    Targets(Path root, List<MavenProject> projects, MavenModel.Request request) {
        this.root = root;
        this.projects = projects;
        this.request = request;
    }

    Target of(MavenProject project, DependencyResolutionResult resolved, String problem) {
        Target target = new Target();
        Path base = project.getBasedir().toPath();
        target.id = MavenModel.relative(root, base);
        target.displayName = project.getArtifactId();
        target.baseDirectory = base.toString();
        boolean kotlin = plugin(project, KOTLIN) != null;
        target.languageIds = kotlin ? List.of("java", "kotlin") : List.of("java");
        Integer release = release(project);
        Requirement required = toolchain(project);
        // A release is a language level the import JDK compiles for; only a toolchains requirement asks for another JDK,
        // which the host then looks for in the IDE's table.
        Environment jvm = new Environment(required == null ? request.javaHome : null,
                release != null ? release : required != null ? required.feature() : feature(request.javaVersion), null, required);
        target.jvmBuildTarget = jvm;
        target.runEnvironment = jvm;
        target.problem = problem;
        target.activeProfiles = new ArrayList<>();
        for (Profile profile : project.getActiveProfiles()) {
            if ("pom".equals(profile.getSource()) || profile.getSource() == null || profile.getSource().contains(":")) {
                target.activeProfiles.add(profile.getId());
            }
        }
        target.tasks = new ArrayList<>(List.of("compile", "package", "test", "verify"));
        if (plugin(project, BOOT) != null) {
            target.tasks.add("spring-boot:run");
        }
        target.mainClasses = configuredMains(project);
        target.sourceSets = new ArrayList<>();
        if (!"pom".equals(project.getPackaging())) {
            List<Dependency> dependencies = resolved == null ? List.of() : resolved.getDependencies();
            target.sourceSets.add(main(project, kotlin, dependencies));
            target.sourceSets.add(test(project, kotlin, dependencies));
        }
        return target;
    }

    private SourceSet main(MavenProject project, boolean kotlin, List<Dependency> dependencies) {
        Set<String> java = new LinkedHashSet<>(project.getCompileSourceRoots());
        java.addAll(helperRoots(project, "add-source"));
        List<SourceItem> sources = new ArrayList<>();
        java.forEach(path -> sources.add(new SourceItem(path, "java", false)));
        if (kotlin) {
            kotlinRoots(project, false).forEach(path -> sources.add(new SourceItem(path, "kotlin", false)));
        }
        generated(project, "generated-sources").forEach(path -> sources.add(new SourceItem(path, "java", true)));
        for (Resource resource : project.getResources()) {
            sources.add(new SourceItem(absolute(project, resource.getDirectory()), "resources", false));
        }
        helperRoots(project, "add-resource").forEach(path -> sources.add(new SourceItem(path, "resources", false)));
        String id = MavenModel.relative(root, project.getBasedir().toPath());
        SourceSet set = new SourceSet(id + ":main", "main", false, dedupe(sources),
                List.of(project.getBuild().getOutputDirectory()));
        set.dependencyModules.put("compile", entries(dependencies, Set.of("compile", "provided", "system"), null));
        set.dependencyModules.put("runtime", entries(dependencies, Set.of("compile", "runtime"), self(project, id + ":main", false)));
        return set;
    }

    private SourceSet test(MavenProject project, boolean kotlin, List<Dependency> dependencies) {
        Set<String> java = new LinkedHashSet<>(project.getTestCompileSourceRoots());
        java.addAll(helperRoots(project, "add-test-source"));
        List<SourceItem> sources = new ArrayList<>();
        java.forEach(path -> sources.add(new SourceItem(path, "java", false)));
        if (kotlin) {
            kotlinRoots(project, true).forEach(path -> sources.add(new SourceItem(path, "kotlin", false)));
        }
        generated(project, "generated-test-sources").forEach(path -> sources.add(new SourceItem(path, "java", true)));
        for (Resource resource : project.getTestResources()) {
            sources.add(new SourceItem(absolute(project, resource.getDirectory()), "resources", false));
        }
        helperRoots(project, "add-test-resource").forEach(path -> sources.add(new SourceItem(path, "resources", false)));
        String id = MavenModel.relative(root, project.getBasedir().toPath());
        SourceSet set = new SourceSet(id + ":test", "test", true, dedupe(sources),
                List.of(project.getBuild().getTestOutputDirectory()));
        Set<String> all = Set.of("compile", "provided", "system", "runtime", "test");
        List<DependencyModule> compile = new ArrayList<>();
        compile.add(self(project, id + ":main", false));
        compile.addAll(entries(dependencies, all, null));
        set.dependencyModules.put("compile", compile);
        List<DependencyModule> runtime = new ArrayList<>();
        runtime.add(self(project, id + ":test", true));
        runtime.add(self(project, id + ":main", false));
        runtime.addAll(entries(dependencies, all, null));
        set.dependencyModules.put("runtime", runtime);
        return set;
    }

    private static DependencyModule self(MavenProject project, String ref, boolean test) {
        return new DependencyModule(test ? project.getBuild().getTestOutputDirectory() : project.getBuild().getOutputDirectory(), null, ref);
    }

    private List<DependencyModule> entries(List<Dependency> dependencies, Set<String> scopes, DependencyModule first) {
        List<DependencyModule> entries = new ArrayList<>();
        if (first != null) {
            entries.add(first);
        }
        for (Dependency dependency : dependencies) {
            if (!scopes.contains(dependency.getScope()) || dependency.getArtifact().getFile() == null) {
                continue;
            }
            Artifact artifact = dependency.getArtifact();
            String path = artifact.getFile().getPath();
            MavenProject sibling = projects.stream()
                    .filter(p -> p.getGroupId().equals(artifact.getGroupId()) && p.getArtifactId().equals(artifact.getArtifactId())
                            && p.getVersion().equals(artifact.getBaseVersion()))
                    .findFirst().orElse(null);
            if (sibling != null) {
                boolean tests = "tests".equals(artifact.getClassifier());
                entries.add(new DependencyModule(path, null,
                        MavenModel.relative(root, sibling.getBasedir().toPath()) + (tests ? ":test" : ":main")));
            } else {
                String coordinates = artifact.getGroupId() + ":" + artifact.getArtifactId() + ":" + artifact.getBaseVersion()
                        + (artifact.getClassifier().isEmpty() ? "" : ":" + artifact.getClassifier());
                entries.add(new DependencyModule(path, coordinates, null));
            }
        }
        return entries;
    }

    private static List<String> helperRoots(MavenProject project, String goal) {
        Plugin helper = plugin(project, BUILD_HELPER);
        List<String> roots = new ArrayList<>();
        if (helper == null) {
            return roots;
        }
        String element = goal.endsWith("resource") ? "resources" : "sources";
        for (PluginExecution execution : helper.getExecutions()) {
            if (!execution.getGoals().contains(goal) || !(execution.getConfiguration() instanceof Xpp3Dom configuration)) {
                continue;
            }
            Xpp3Dom list = configuration.getChild(element);
            if (list == null) {
                continue;
            }
            for (Xpp3Dom item : list.getChildren()) {
                String path = element.equals("resources") ? text(item.getChild("directory")) : item.getValue();
                if (path != null && !path.isBlank()) {
                    roots.add(absolute(project, path.strip()));
                }
            }
        }
        return roots;
    }

    private static List<String> kotlinRoots(MavenProject project, boolean test) {
        Plugin plugin = plugin(project, KOTLIN);
        List<String> roots = new ArrayList<>();
        for (PluginExecution execution : plugin.getExecutions()) {
            boolean testGoal = execution.getGoals().contains("test-compile");
            if (testGoal != test || !(execution.getConfiguration() instanceof Xpp3Dom configuration) || configuration.getChild("sourceDirs") == null) {
                continue;
            }
            for (Xpp3Dom dir : configuration.getChild("sourceDirs").getChildren()) {
                if (dir.getValue() != null && !dir.getValue().isBlank()) {
                    roots.add(absolute(project, dir.getValue().strip()));
                }
            }
        }
        if (roots.isEmpty()) {
            roots.add(absolute(project, test ? "src/test/kotlin" : "src/main/kotlin"));
        }
        return roots;
    }

    private static List<String> generated(MavenProject project, String folder) {
        Path generated = Path.of(project.getBuild().getDirectory(), folder);
        List<String> roots = new ArrayList<>();
        if (!Files.isDirectory(generated)) {
            return roots;
        }
        try (DirectoryStream<Path> children = Files.newDirectoryStream(generated, Files::isDirectory)) {
            Set<String> sorted = new TreeSet<>();
            children.forEach(child -> sorted.add(child.toString()));
            roots.addAll(sorted);
        } catch (IOException unreadable) {
            System.err.println("wayfarer maven: could not list " + generated + ": " + unreadable);
        }
        return roots;
    }

    private static List<MainClass> configuredMains(MavenProject project) {
        Map<String, MainClass> mains = new LinkedHashMap<>();
        for (String key : List.of(EXEC, BOOT)) {
            Plugin plugin = plugin(project, key);
            if (plugin != null && plugin.getConfiguration() instanceof Xpp3Dom configuration) {
                String main = text(configuration.getChild("mainClass"));
                if (main != null && !main.isBlank()) {
                    mains.putIfAbsent(main.strip(), new MainClass(main.strip(), null, "configured"));
                }
            }
        }
        String startClass = project.getProperties().getProperty("start-class");
        if (startClass != null && !startClass.isBlank()) {
            mains.putIfAbsent(startClass.strip(), new MainClass(startClass.strip(), null, "configured"));
        }
        return new ArrayList<>(mains.values());
    }

    /** The release the compiler plugin compiles for: its release, else its target, else its source. */
    private static Integer release(MavenProject project) {
        Plugin compiler = plugin(project, COMPILER);
        Xpp3Dom configuration = compiler != null && compiler.getConfiguration() instanceof Xpp3Dom dom ? dom : null;
        for (String name : List.of("release", "target", "source")) {
            String value = configuration == null ? null : text(configuration.getChild(name));
            if (value == null) {
                value = project.getProperties().getProperty("maven.compiler." + name);
            }
            Integer feature = feature(value);
            if (feature != null) {
                return feature;
            }
        }
        return null;
    }

    /** The JDK maven-toolchains-plugin asks for (its jdk version and vendor), in its own or an execution's configuration. */
    private static Requirement toolchain(MavenProject project) {
        Plugin toolchains = plugin(project, TOOLCHAINS);
        if (toolchains == null) {
            return null;
        }
        List<Object> configurations = new ArrayList<>();
        configurations.add(toolchains.getConfiguration());
        toolchains.getExecutions().forEach(execution -> configurations.add(execution.getConfiguration()));
        for (Object configuration : configurations) {
            if (configuration instanceof Xpp3Dom dom && dom.getChild("toolchains") != null && dom.getChild("toolchains").getChild("jdk") != null) {
                Xpp3Dom jdk = dom.getChild("toolchains").getChild("jdk");
                String version = text(jdk.getChild("version"));
                Integer feature = feature(version == null ? null : version.strip().replaceFirst("^[\\[(]", ""));
                if (feature != null) {
                    String vendor = text(jdk.getChild("vendor"));
                    return new Requirement(null, feature, vendor == null || vendor.isBlank() || vendor.contains("${") ? null : vendor.strip(), "maven-release");
                }
            }
        }
        return null;
    }

    static Integer feature(String version) {
        if (version == null || version.isBlank() || version.contains("${")) {
            return null;
        }
        String rest = version.strip().startsWith("1.") ? version.strip().substring(2) : version.strip();
        int end = 0;
        while (end < rest.length() && Character.isDigit(rest.charAt(end))) {
            end++;
        }
        return end == 0 ? null : Integer.parseInt(rest.substring(0, end));
    }

    List<String> buildInputs(File rootFolder, File userSettings, File globalSettings) {
        Set<String> inputs = new LinkedHashSet<>();
        for (MavenProject project : projects) {
            for (MavenProject p = project; p != null; p = p.getParent()) {
                if (p.getFile() != null) {
                    inputs.add(p.getFile().getPath());
                }
            }
        }
        for (String name : List.of("maven.config", "jvm.config", "extensions.xml")) {
            inputs.add(new File(new File(rootFolder, ".mvn"), name).getPath());
        }
        inputs.add(new File(new File(new File(rootFolder, ".mvn"), "wrapper"), "maven-wrapper.properties").getPath());
        if (userSettings != null) {
            inputs.add(userSettings.getPath());
        }
        if (globalSettings != null) {
            inputs.add(globalSettings.getPath());
        }
        return new ArrayList<>(inputs);
    }

    private static Plugin plugin(MavenProject project, String key) {
        return project.getBuild() == null ? null : project.getBuild().getPluginsAsMap().get(key);
    }

    private static String text(Xpp3Dom element) {
        return element == null ? null : element.getValue();
    }

    private static String absolute(MavenProject project, String path) {
        File file = new File(path);
        return (file.isAbsolute() ? file : new File(project.getBasedir(), path)).toPath().normalize().toString();
    }

    private static List<SourceItem> dedupe(List<SourceItem> sources) {
        Map<String, SourceItem> unique = new LinkedHashMap<>();
        for (SourceItem source : sources) {
            unique.putIfAbsent(source.uri, source);
        }
        return new ArrayList<>(unique.values());
    }

    // The protocol's build target, as JSON field names.
    static final class Target {
        String id;
        String displayName;
        String baseDirectory;
        List<String> languageIds;
        Environment jvmBuildTarget;
        Environment runEnvironment;
        List<SourceSet> sourceSets;
        List<String> tasks;
        List<MainClass> mainClasses;
        List<String> activeProfiles;
        List<String> absentLibraries = List.of();
        String problem;
    }

    record Environment(String javaHome, Integer javaVersion, String vendor, Requirement required) {
    }

    record Requirement(String name, Integer feature, String vendor, String source) {
    }

    static final class SourceSet {
        final String id;
        final String name;
        final boolean test;
        final List<SourceItem> sources;
        final List<String> outputs;
        final Map<String, List<DependencyModule>> dependencyModules = new LinkedHashMap<>();

        SourceSet(String id, String name, boolean test, List<SourceItem> sources, List<String> outputs) {
            this.id = id;
            this.name = name;
            this.test = test;
            this.sources = sources;
            this.outputs = outputs;
        }
    }

    record SourceItem(String uri, String kind, boolean generated) {
    }

    record DependencyModule(String path, String coordinates, String moduleRef) {
    }

    record MainClass(String className, String sourcePath, String origin) {
    }
}
