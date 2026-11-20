package wayfarer.gradle;

import java.io.File;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;
import org.gradle.api.JavaVersion;
import org.gradle.api.Project;
import org.gradle.api.Task;
import org.gradle.api.artifacts.ArtifactCollection;
import org.gradle.api.artifacts.Configuration;
import org.gradle.api.artifacts.component.ComponentIdentifier;
import org.gradle.api.artifacts.component.ModuleComponentIdentifier;
import org.gradle.api.artifacts.component.ProjectComponentIdentifier;
import org.gradle.api.artifacts.result.ResolvedArtifactResult;
import org.gradle.api.capabilities.Capability;
import org.gradle.api.file.FileCollection;
import org.gradle.api.file.SourceDirectorySet;
import org.gradle.api.plugins.JavaApplication;
import org.gradle.api.plugins.JavaPluginExtension;
import org.gradle.api.provider.Property;
import org.gradle.api.tasks.SourceSet;
import org.gradle.api.tasks.SourceSetContainer;
import org.gradle.api.tasks.compile.JavaCompile;
import org.gradle.api.tasks.testing.Test;
import org.gradle.jvm.toolchain.JavaToolchainService;
import org.gradle.jvm.toolchain.JavaToolchainSpec;
import org.gradle.tooling.provider.model.ToolingModelBuilder;

/**
 * Builds Wayfarer's model of one Gradle project, as the build itself evaluates it: its source sets with every root
 * they declare (Kotlin's and generated ones included), outputs and ordered compile and runtime classpaths (another
 * source set's outputs named as such, libraries by their coordinates), its toolchain, tasks and configured mains.
 * Only API present since Gradle 8.0 is used. A dependency that does not resolve, or a toolchain that is not installed,
 * becomes the project's problem while the rest of its model stands.
 */
public final class WayfarerModelBuilder implements ToolingModelBuilder {
    private static final Set<String> TASK_GROUPS = new HashSet<>(Arrays.asList("application", "build", "verification"));
    private static final Set<String> TASK_NAMES = new HashSet<>(Arrays.asList("run", "bootRun"));

    @Override
    public boolean canBuild(String modelName) {
        return modelName.equals(WayfarerModel.class.getName());
    }

    @Override
    public Object buildAll(String modelName, Project project) {
        return new DefaultWayfarerModel(Json.write(target(project)));
    }

    private static Map<String, Object> target(Project project) {
        List<String> problems = new ArrayList<>();
        Map<String, Object> target = new LinkedHashMap<>();
        target.put("id", id(project));
        target.put("displayName", project.getName());
        target.put("baseDirectory", project.getProjectDir().getAbsolutePath());
        boolean kotlin = project.getPluginManager().hasPlugin("org.jetbrains.kotlin.jvm");
        target.put("languageIds", kotlin ? Arrays.asList("java", "kotlin") : Collections.singletonList("java"));
        JavaPluginExtension java = project.getExtensions().findByType(JavaPluginExtension.class);
        Map<String, Object> compileJvm = new LinkedHashMap<>();
        Map<String, Object> runJvm = new LinkedHashMap<>();
        toolchain(project, java, compileJvm, runJvm, problems);
        target.put("jvmBuildTarget", java == null ? null : compileJvm);
        target.put("runEnvironment", java == null ? null : runJvm);
        SourceSetContainer sets = project.getExtensions().findByType(SourceSetContainer.class);
        List<Object> sourceSets = new ArrayList<>();
        if (sets != null) {
            Set<File> testClasses = testClasses(project);
            for (SourceSet set : sets) {
                sourceSets.add(sourceSet(project, set, sets, testClasses, kotlin, problems));
            }
        }
        target.put("sourceSets", sourceSets);
        target.put("tasks", tasks(project));
        target.put("mainClasses", mains(project));
        target.put("activeProfiles", Collections.emptyList());
        target.put("absentLibraries", Collections.emptyList());
        target.put("problem", problems.isEmpty() ? null : String.join("; ", new LinkedHashSet<>(problems)));
        target.put("buildFiles", Collections.singletonList(project.getBuildFile().getAbsolutePath()));
        return target;
    }

    /** The module id: the project's path without its leading colon, "." for the root, and an included build's name before it. */
    static String id(Project project) {
        String prefix = project.getGradle().getParent() != null ? project.getRootProject().getName() : "";
        return id(prefix, project.getPath());
    }

    private static String id(String buildPrefix, String path) {
        String own = ":".equals(path) ? "" : path.substring(1);
        if (buildPrefix.isEmpty()) {
            return own.isEmpty() ? "." : own;
        }
        return own.isEmpty() ? buildPrefix : buildPrefix + ":" + own;
    }

    private static void toolchain(Project project, JavaPluginExtension java, Map<String, Object> compile, Map<String, Object> run, List<String> problems) {
        if (java == null) {
            return;
        }
        Integer version = null;
        String compileHome = null;
        String runHome = null;
        JavaToolchainSpec spec = java.getToolchain();
        if (spec.getLanguageVersion().isPresent()) {
            version = spec.getLanguageVersion().get().asInt();
            JavaToolchainService service = project.getExtensions().getByType(JavaToolchainService.class);
            try {
                compileHome = service.compilerFor(spec).get().getMetadata().getInstallationPath().getAsFile().getAbsolutePath();
                runHome = service.launcherFor(spec).get().getMetadata().getInstallationPath().getAsFile().getAbsolutePath();
            } catch (RuntimeException missing) {
                problems.add("no JDK " + version + " for " + project.getPath() + ": " + firstLine(missing));
            }
        } else {
            Task compileJava = project.getTasks().findByName("compileJava");
            if (compileJava instanceof JavaCompile && ((JavaCompile) compileJava).getOptions().getRelease().isPresent()) {
                version = ((JavaCompile) compileJava).getOptions().getRelease().get();
            } else {
                JavaVersion target = java.getTargetCompatibility();
                version = target == null ? null : Integer.valueOf(target.getMajorVersion());
            }
        }
        compile.put("javaHome", compileHome);
        compile.put("javaVersion", version);
        compile.put("vendor", null);
        run.put("javaHome", runHome != null ? runHome : compileHome);
        run.put("javaVersion", version);
        run.put("vendor", null);
    }

    private static Set<File> testClasses(Project project) {
        Set<File> dirs = new HashSet<>();
        for (Test test : project.getTasks().withType(Test.class)) {
            try {
                dirs.addAll(test.getTestClassesDirs().getFiles());
            } catch (RuntimeException unreadable) {
                // A test task whose classes are not a source set's.
            }
        }
        return dirs;
    }

    private static Map<String, Object> sourceSet(Project project, SourceSet set, SourceSetContainer sets, Set<File> testClasses, boolean kotlin, List<String> problems) {
        Map<String, Object> result = new LinkedHashMap<>();
        String id = id(project) + ":" + set.getName();
        result.put("id", id);
        result.put("name", set.getName());
        boolean test = false;
        for (File dir : set.getOutput().getClassesDirs().getFiles()) {
            test |= testClasses.contains(dir);
        }
        result.put("test", test);
        File buildDir = project.getLayout().getBuildDirectory().get().getAsFile();
        List<Object> sources = new ArrayList<>();
        Set<File> seen = new HashSet<>();
        for (File dir : set.getJava().getSrcDirs()) {
            addRoot(sources, seen, dir, "java", buildDir);
        }
        Object kotlinDirs = set.getExtensions().findByName("kotlin");
        if (kotlinDirs instanceof SourceDirectorySet) {
            for (File dir : ((SourceDirectorySet) kotlinDirs).getSrcDirs()) {
                addRoot(sources, seen, dir, "kotlin", buildDir);
            }
        } else if (kotlin) {
            addRoot(sources, seen, project.file("src/" + set.getName() + "/kotlin"), "kotlin", buildDir);
        }
        for (File dir : set.getResources().getSrcDirs()) {
            addRoot(sources, seen, dir, "resources", buildDir);
        }
        result.put("sources", sources);
        result.put("outputs", paths(outputs(set, true)));
        Map<String, Object> modules = new LinkedHashMap<>();
        modules.put("compile", classpath(project, set.getCompileClasspath(), set.getCompileClasspathConfigurationName(), false, set, problems));
        modules.put("runtime", classpath(project, set.getRuntimeClasspath(), set.getRuntimeClasspathConfigurationName(), true, set, problems));
        result.put("dependencyModules", modules);
        return result;
    }

    private static void addRoot(List<Object> sources, Set<File> seen, File dir, String kind, File buildDir) {
        File absolute = dir.getAbsoluteFile();
        if (!seen.add(absolute)) {
            return;
        }
        Map<String, Object> root = new LinkedHashMap<>();
        root.put("uri", absolute.getPath());
        root.put("kind", kind);
        root.put("generated", absolute.toPath().startsWith(buildDir.getAbsoluteFile().toPath()));
        sources.add(root);
    }

    /** A source set's classes folders, and its resources folder for a runtime classpath. */
    private static List<File> outputs(SourceSet set, boolean runtime) {
        List<File> outputs = new ArrayList<>(set.getOutput().getClassesDirs().getFiles());
        if (runtime && set.getOutput().getResourcesDir() != null) {
            outputs.add(set.getOutput().getResourcesDir());
        }
        return outputs;
    }

    private static List<Object> classpath(Project project, FileCollection files, String configurationName, boolean runtime, SourceSet owner, List<String> problems) {
        Map<File, ResolvedArtifactResult> artifacts = new LinkedHashMap<>();
        Configuration configuration = project.getConfigurations().findByName(configurationName);
        if (configuration != null) {
            try {
                ArtifactCollection collection = configuration.getIncoming().artifactView(view -> view.setLenient(true)).getArtifacts();
                for (ResolvedArtifactResult artifact : collection.getArtifacts()) {
                    artifacts.put(artifact.getFile().getAbsoluteFile(), artifact);
                }
                for (Throwable failure : collection.getFailures()) {
                    problems.add(firstLine(failure));
                }
            } catch (RuntimeException failure) {
                problems.add(firstLine(failure));
            }
        }
        List<File> ordered = new ArrayList<>();
        try {
            for (File file : files.getFiles()) {
                ordered.add(file.getAbsoluteFile());
            }
        } catch (RuntimeException unresolved) {
            // What resolved, after the set's own outputs, in the order resolution gave.
            if (runtime) {
                ordered.addAll(outputs(owner, true));
            }
            ordered.addAll(artifacts.keySet());
        }
        List<Object> entries = new ArrayList<>();
        // A source set appears once, where it first comes: a project depending on itself (test fixtures) names its main again.
        Set<String> seenRefs = new HashSet<>();
        for (File file : ordered) {
            ResolvedArtifactResult artifact = artifacts.get(file);
            String coordinates = null;
            String ref = null;
            List<File> refFiles = null;
            if (artifact != null) {
                ComponentIdentifier component = artifact.getId().getComponentIdentifier();
                if (component instanceof ProjectComponentIdentifier) {
                    Object[] found = projectRef(project, (ProjectComponentIdentifier) component, artifact, file, runtime);
                    ref = (String) found[0];
                    @SuppressWarnings("unchecked")
                    List<File> found1 = (List<File>) found[1];
                    refFiles = found1;
                } else if (component instanceof ModuleComponentIdentifier) {
                    coordinates = coordinates((ModuleComponentIdentifier) component, file);
                }
            } else {
                for (SourceSet set : project.getExtensions().getByType(SourceSetContainer.class)) {
                    if (outputs(set, true).contains(file)) {
                        ref = id(project) + ":" + set.getName();
                        refFiles = outputs(set, runtime);
                        break;
                    }
                }
            }
            if (ref != null) {
                if (!seenRefs.add(ref)) {
                    continue;
                }
                for (File output : refFiles) {
                    entries.add(entry(output.getPath(), null, ref));
                }
            } else {
                entries.add(entry(file.getPath(), coordinates, null));
            }
        }
        return entries;
    }

    /** The source set another project's artifact is the outputs of, and those outputs: by the files where that project is
     * this build's, else by the artifact's capability (the default one is main, {@code name-test-fixtures} is testFixtures). */
    private static Object[] projectRef(Project project, ProjectComponentIdentifier component, ResolvedArtifactResult artifact, File file, boolean runtime) {
        Project other = project.findProject(component.getProjectPath());
        boolean sameBuild = other != null && file.toPath().startsWith(other.getProjectDir().getAbsoluteFile().toPath());
        SourceSetContainer sets = sameBuild ? other.getExtensions().findByType(SourceSetContainer.class) : null;
        if (sets != null) {
            for (SourceSet set : sets) {
                if (outputs(set, true).contains(file)) {
                    return new Object[] {id(other) + ":" + set.getName(), outputs(set, runtime)};
                }
            }
        }
        String name = setByCapability(component, artifact);
        if (sets != null && sets.findByName(name) != null) {
            return new Object[] {id(other) + ":" + name, outputs(sets.getByName(name), runtime)};
        }
        String prefix = sameBuild ? id(other) : id(buildName(component), component.getProjectPath());
        return new Object[] {prefix + ":" + name, Collections.singletonList(file)};
    }

    private static String setByCapability(ProjectComponentIdentifier component, ResolvedArtifactResult artifact) {
        String projectName = component.getProjectName();
        for (Capability capability : artifact.getVariant().getCapabilities()) {
            String name = capability.getName();
            if (name.equals(projectName)) {
                return "main";
            }
            if (name.startsWith(projectName + "-")) {
                String[] words = name.substring(projectName.length() + 1).split("-");
                StringBuilder camel = new StringBuilder(words[0]);
                for (int i = 1; i < words.length; i++) {
                    camel.append(Character.toUpperCase(words[i].charAt(0))).append(words[i].substring(1));
                }
                return camel.toString();
            }
        }
        return "main";
    }

    /** The build an identifier names, "" for the root build (BuildIdentifier's build path since Gradle 8.2, else its name). */
    private static String buildName(ProjectComponentIdentifier component) {
        Object build = component.getBuild();
        for (String getter : new String[] {"getBuildPath", "getName"}) {
            try {
                Method method = build.getClass().getMethod(getter);
                String value = String.valueOf(method.invoke(build));
                if (getter.equals("getBuildPath")) {
                    return ":".equals(value) ? "" : value.substring(value.lastIndexOf(':') + 1);
                }
                return ":".equals(value) ? "" : value;
            } catch (ReflectiveOperationException | RuntimeException absent) {
                // Try the older name.
            }
        }
        return "";
    }

    private static String coordinates(ModuleComponentIdentifier component, File file) {
        String base = component.getGroup() + ":" + component.getModule() + ":" + component.getVersion();
        String name = file.getName();
        String stem = component.getModule() + "-" + component.getVersion() + "-";
        int dot = name.lastIndexOf('.');
        if (name.startsWith(stem) && dot > stem.length()) {
            return base + ":" + name.substring(stem.length(), dot);
        }
        return base;
    }

    private static Map<String, Object> entry(String path, String coordinates, String moduleRef) {
        Map<String, Object> entry = new LinkedHashMap<>();
        entry.put("path", path);
        entry.put("coordinates", coordinates);
        entry.put("moduleRef", moduleRef);
        return entry;
    }

    private static List<String> tasks(Project project) {
        Set<String> names = new TreeSet<>();
        for (Task task : project.getTasks()) {
            String group = task.getGroup();
            if ((group != null && TASK_GROUPS.contains(group.toLowerCase(Locale.ROOT))) || TASK_NAMES.contains(task.getName())) {
                names.add(task.getName());
            }
        }
        return new ArrayList<>(names);
    }

    private static List<Object> mains(Project project) {
        Set<String> classes = new LinkedHashSet<>();
        JavaApplication application = project.getExtensions().findByType(JavaApplication.class);
        if (application != null && application.getMainClass().isPresent()) {
            classes.add(application.getMainClass().get());
        }
        Object boot = project.getExtensions().findByName("springBoot");
        if (boot != null) {
            try {
                Object property = boot.getClass().getMethod("getMainClass").invoke(boot);
                if (property instanceof Property && ((Property<?>) property).isPresent()) {
                    classes.add(String.valueOf(((Property<?>) property).get()));
                }
            } catch (ReflectiveOperationException | RuntimeException absent) {
                // A Boot plugin without a main class of its own; bootRun finds one when it runs.
            }
        }
        List<Object> mains = new ArrayList<>();
        for (String className : classes) {
            Map<String, Object> main = new LinkedHashMap<>();
            main.put("className", className);
            main.put("sourcePath", null);
            main.put("origin", "configured");
            mains.add(main);
        }
        return mains;
    }

    private static List<String> paths(List<File> files) {
        List<String> paths = new ArrayList<>();
        for (File file : files) {
            paths.add(file.getAbsolutePath());
        }
        return paths;
    }

    private static String firstLine(Throwable failure) {
        String said = null;
        for (Throwable cause = failure; cause != null && cause != cause.getCause(); cause = cause.getCause()) {
            if (cause.getMessage() != null && !cause.getMessage().trim().isEmpty()) {
                said = cause.getMessage().trim();
                break;
            }
        }
        if (said == null) {
            return failure.getClass().getSimpleName();
        }
        int newline = said.indexOf('\n');
        return newline < 0 ? said : said.substring(0, newline).trim();
    }
}
