package wayfarer.host;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import java.util.jar.JarOutputStream;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/**
 * IntelliJ projects of several modules: module dependencies with their exported entries, each scope in its classpath,
 * project, global and module libraries, path variables, repository libraries downloaded with Maven Resolver (from a
 * repository in a folder, so nothing leaves the machine) and Kotlin's runtime library.
 */
class IdeaProjectsTest {
    @Test
    void modulesScopesAndExportsMakeEachClasspath(@TempDir Path root) throws IOException {
        Path libs = Files.createDirectories(root.resolve("lib"));
        for (String jar : List.of("guava.jar", "junit.jar", "driver.jar", "servlet.jar", "shared.jar", "inline.jar", "global.jar")) {
            jar(libs.resolve(jar));
        }
        idea(root, "core/core.iml", "app/app.iml");
        library(root, "guava", "jar://$PROJECT_DIR$/lib/guava.jar!/");
        library(root, "junit", "jar://$PROJECT_DIR$/lib/junit.jar!/");
        library(root, "driver", "jar://$PROJECT_DIR$/lib/driver.jar!/");
        library(root, "servlet", "jar://$PROJECT_DIR$/lib/servlet.jar!/");
        library(root, "shared", "jar://$SHARED_LIBS$/shared.jar!/");
        module(root.resolve("core"), "core", """
                <orderEntry type="library" exported="" name="guava" level="project" />
                <orderEntry type="library" name="shared" level="project" />
                """);
        module(root.resolve("app"), "app", """
                <orderEntry type="module" module-name="core" />
                <orderEntry type="library" scope="TEST" name="junit" level="project" />
                <orderEntry type="library" scope="RUNTIME" name="driver" level="project" />
                <orderEntry type="library" scope="PROVIDED" name="servlet" level="project" />
                <orderEntry type="library" name="Global" level="application" />
                <orderEntry type="module-library"><library><CLASSES><root url="jar://$MODULE_DIR$/../lib/inline.jar!/" /></CLASSES></library></orderEntry>
                """);
        Protocol.ImportParams params = new Protocol.ImportParams(root.toString(), 1, null, null, List.of(), null, List.of(),
                Map.of("SHARED_LIBS", libs.toString()),
                List.of(new Protocol.Library("Global", List.of("jar://" + libs.resolve("global.jar").toString().replace('\\', '/') + "!/"), null, null)));

        Protocol.WorkspaceResult result = new IdeaEvaluator().evaluate(root, params);
        assertEquals(List.of("core", "app"), result.targets().stream().map(Protocol.BuildTarget::id).toList());
        Protocol.BuildTarget app = result.targets().get(1);
        String coreClasses = root.resolve("out").resolve("production").resolve("core").toString();
        assertEquals(List.of(coreClasses, "guava.jar", "servlet.jar", "global.jar", "inline.jar"), names(app, "main", "compile", coreClasses),
                "core's classes and its exported guava, not its own shared; no test or runtime libraries");
        assertEquals(List.of("app", coreClasses, "guava.jar", "shared.jar", "driver.jar", "global.jar", "inline.jar"), names(app, "main", "runtime", coreClasses),
                "everything core runs with, the runtime library, no test or provided one");
        assertTrue(names(app, "test", "compile", coreClasses).contains("junit.jar"));
        assertTrue(names(app, "test", "runtime", coreClasses).containsAll(List.of("junit.jar", "driver.jar", "servlet.jar")));
        assertEquals("core:main", app.sourceSets().get(0).dependencyModules().get("compile").get(0).moduleRef());
        assertNull(app.problem());
        assertEquals(List.of(), app.absentLibraries());
        assertEquals(root.resolve(".idea").resolve("modules.xml").toString(), result.buildFile());
    }

    @Test
    void aPathVariableNoOneSetIsAModuleProblemNamingIt(@TempDir Path root) throws IOException {
        idea(root, "app/app.iml");
        library(root, "shared", "jar://$SHARED_LIBS$/shared.jar!/");
        module(root.resolve("app"), "app", "<orderEntry type=\"library\" name=\"shared\" level=\"project\" />\n");
        Protocol.BuildTarget app = new IdeaEvaluator().evaluate(root, params(root)).targets().get(0);
        assertNotNull(app.problem());
        assertTrue(app.problem().contains("path variable SHARED_LIBS is not set"), app.problem());
        assertTrue(app.problem().contains("shared"), app.problem());
    }

    @Test
    void aRepositoryLibraryWithoutItsJarsIsDownloadedWithItsDependencies(@TempDir Path root) throws IOException {
        Path remote = root.resolve("remote");
        Path local = root.resolve("local");
        artifact(remote, "org.example", "widgets", "1.2", "<dependencies><dependency><groupId>org.example</groupId><artifactId>gears</artifactId><version>3.0</version></dependency></dependencies>");
        artifact(remote, "org.example", "gears", "3.0", "");
        Path project = Files.createDirectories(root.resolve("project"));
        idea(project, "app/app.iml");
        Files.writeString(project.resolve(".idea").resolve("libraries").resolve("widgets.xml"), """
                <component name="libraryTable">
                  <library name="org.example:widgets:1.2" type="repository">
                    <properties maven-id="org.example:widgets:1.2" include-transitive-deps="true" />
                    <CLASSES><root url="jar://$MAVEN_REPOSITORY$/org/example/widgets/1.2/widgets-1.2.jar!/" /></CLASSES>
                  </library>
                </component>
                """);
        module(project.resolve("app"), "app", "<orderEntry type=\"library\" name=\"org.example:widgets:1.2\" level=\"project\" />\n");
        IdeaEvaluator evaluator = new IdeaEvaluator();
        evaluator.repositories(new RepositoryLibraries(local, List.of(remote.toUri().toString())));
        Protocol.ImportParams params = new Protocol.ImportParams(project.toString(), 1, null, null, List.of(), null, List.of(),
                Map.of("MAVEN_REPOSITORY", local.toString()), List.of());

        Protocol.BuildTarget app = evaluator.evaluate(project, params).targets().get(0);
        assertNull(app.problem(), app.problem());
        assertEquals(List.of(), app.absentLibraries());
        List<Protocol.DependencyModule> compile = app.sourceSets().get(0).dependencyModules().get("compile");
        assertEquals(List.of("widgets-1.2.jar", "gears-3.0.jar"), compile.stream().map(m -> Path.of(m.path()).getFileName().toString()).toList());
        assertTrue(Files.isRegularFile(local.resolve("org/example/widgets/1.2/widgets-1.2.jar")), "downloaded into the local repository");
        assertEquals("org.example:widgets:1.2", compile.get(0).coordinates());
    }

    @Test
    void kotlinsBundledRuntimeIsResolvedAtTheProjectsKotlinVersion(@TempDir Path root) throws IOException {
        Path remote = root.resolve("remote");
        Path local = root.resolve("local");
        artifact(remote, "org.jetbrains.kotlin", "kotlin-stdlib", "1.9.0", "");
        Path project = Files.createDirectories(root.resolve("project"));
        idea(project, "app/app.iml");
        Files.writeString(project.resolve(".idea").resolve("kotlinc.xml"), """
                <project version="4"><component name="KotlinJpsPluginSettings"><option name="version" value="1.9.0" /></component></project>
                """);
        Files.writeString(project.resolve(".idea").resolve("libraries").resolve("KotlinJavaRuntime.xml"), """
                <component name="libraryTable">
                  <library name="KotlinJavaRuntime">
                    <CLASSES><root url="jar://$KOTLIN_BUNDLED$/lib/kotlin-stdlib.jar!/" /></CLASSES>
                    <SOURCES><root url="jar://$KOTLIN_BUNDLED$/lib/kotlin-stdlib-sources.jar!/" /></SOURCES>
                  </library>
                </component>
                """);
        module(project.resolve("app"), "app", "<orderEntry type=\"library\" name=\"KotlinJavaRuntime\" level=\"project\" />\n");
        IdeaEvaluator evaluator = new IdeaEvaluator();
        evaluator.repositories(new RepositoryLibraries(local, List.of(remote.toUri().toString())));
        Protocol.ImportParams params = new Protocol.ImportParams(project.toString(), 1, null, null, List.of(), null, List.of(),
                Map.of("MAVEN_REPOSITORY", local.toString()), List.of());

        Protocol.BuildTarget app = evaluator.evaluate(project, params).targets().get(0);
        assertNull(app.problem(), app.problem());
        assertEquals(List.of("java", "kotlin"), app.languageIds());
        assertEquals("kotlin-stdlib-1.9.0.jar", Path.of(app.sourceSets().get(0).dependencyModules().get("compile").get(0).path()).getFileName().toString());
    }

    private static Protocol.ImportParams params(Path root) {
        return new Protocol.ImportParams(root.toString(), 1, null, null, List.of(), null, List.of());
    }

    /** The class roots of a source set's scope by file name, the given folder whole and the module's own classes by
     * module name. */
    private static List<String> names(Protocol.BuildTarget target, String set, String scope, String keep) {
        Protocol.SourceSet sourceSet = target.sourceSets().stream().filter(s -> s.name().equals(set)).findFirst().orElseThrow();
        return sourceSet.dependencyModules().get(scope).stream()
                .map(m -> m.path().equals(keep) ? keep : m.moduleRef() != null && m.moduleRef().startsWith(target.id() + ":") ? target.id()
                        : Path.of(m.path()).getFileName().toString())
                .toList();
    }

    static void idea(Path root, String... modules) throws IOException {
        Path idea = Files.createDirectories(root.resolve(".idea"));
        Files.createDirectories(idea.resolve("libraries"));
        Files.writeString(idea.resolve("misc.xml"), """
                <project version="4">
                  <component name="ProjectRootManager" version="2" languageLevel="JDK_17" project-jdk-name="17" project-jdk-type="JavaSDK">
                    <output url="file://$PROJECT_DIR$/out" />
                  </component>
                </project>
                """);
        StringBuilder listed = new StringBuilder();
        for (String module : modules) {
            listed.append("      <module fileurl=\"file://$PROJECT_DIR$/").append(module).append("\" filepath=\"$PROJECT_DIR$/").append(module).append("\" />\n");
        }
        Files.writeString(idea.resolve("modules.xml"), """
                <project version="4">
                  <component name="ProjectModuleManager">
                    <modules>
                %s    </modules>
                  </component>
                </project>
                """.formatted(listed));
    }

    static void library(Path root, String name, String url) throws IOException {
        Files.writeString(root.resolve(".idea").resolve("libraries").resolve(name + ".xml"), """
                <component name="libraryTable">
                  <library name="%s"><CLASSES><root url="%s" /></CLASSES><JAVADOC /><SOURCES /></library>
                </component>
                """.formatted(name, url));
    }

    static void module(Path dir, String name, String entries) throws IOException {
        Files.createDirectories(dir.resolve("src"));
        Files.writeString(dir.resolve(name + ".iml"), """
                <?xml version="1.0" encoding="UTF-8"?>
                <module type="JAVA_MODULE" version="4">
                  <component name="NewModuleRootManager" inherit-compiler-output="true">
                    <exclude-output />
                    <content url="file://$MODULE_DIR$">
                      <sourceFolder url="file://$MODULE_DIR$/src" isTestSource="false" />
                      <sourceFolder url="file://$MODULE_DIR$/test" isTestSource="true" />
                    </content>
                    <orderEntry type="inheritedJdk" />
                    <orderEntry type="sourceFolder" forTests="false" />
                %s  </component>
                </module>
                """.formatted(entries));
    }

    private static void artifact(Path repository, String group, String artifact, String version, String dependencies) throws IOException {
        Path folder = Files.createDirectories(repository.resolve(group.replace('.', '/')).resolve(artifact).resolve(version));
        Files.writeString(folder.resolve(artifact + "-" + version + ".pom"), """
                <project xmlns="http://maven.apache.org/POM/4.0.0">
                  <modelVersion>4.0.0</modelVersion>
                  <groupId>%s</groupId><artifactId>%s</artifactId><version>%s</version>
                  %s
                </project>
                """.formatted(group, artifact, version, dependencies));
        jar(folder.resolve(artifact + "-" + version + ".jar"));
    }

    private static void jar(Path file) throws IOException {
        // An empty jar is enough to be on a classpath.
        JarOutputStream empty = new JarOutputStream(Files.newOutputStream(file));
        empty.close();
    }
}
