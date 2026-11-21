package wayfarer.host;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/**
 * How the host picks JDKs: from the IDE's table sent with the import, by name for an IntelliJ module, and by feature for
 * the Gradle daemon. IntelliJ's own jdk.table.xml is read by the app, which sends its JDKs under IntelliJ's names.
 */
class IdeaJdksTest {
    @Test
    void anIntelliJJdkNameResolvesThroughTheSentTable(@TempDir Path root) throws IOException {
        Path jdk = fakeJdk(root.resolve("fake-jdk"), "25.0.1");
        List<Protocol.Jdk> table = List.of(
                new Protocol.Jdk("corretto-17", root.resolve("gone").toString(), "17", 17, null),
                new Protocol.Jdk("openjdk-25", jdk.toString(), "25.0.1", 25, null));
        assertEquals(jdk.toAbsolutePath().normalize().toString(), ImportJvms.named("OpenJDK-25", table));
        assertNull(ImportJvms.named("corretto-17", table), "a JDK whose home is gone is not taken");
        assertNull(ImportJvms.named("zulu-11", table));
        assertNull(ImportJvms.named("openjdk-25", null));
    }

    @Test
    void theGradleDaemonCriteriaPickFromTheSentTable(@TempDir Path root) throws IOException {
        int wanted = Runtime.version().feature() + 7;
        Path jdk = fakeJdk(root.resolve("jdks").resolve("wanted"), wanted + ".0.1");
        Path project = Files.createDirectories(root.resolve("project").resolve("gradle"));
        Files.writeString(project.resolve("gradle-daemon-jvm.properties"), "toolchainVersion=" + wanted + System.lineSeparator());
        Protocol.ImportParams without = new Protocol.ImportParams(project.getParent().toString(), 1, null, null, List.of(), null, List.of());
        assertThrows(Problems.Problem.class, () -> ImportJvms.forGradle(project.getParent(), without));
        Protocol.ImportParams with = new Protocol.ImportParams(project.getParent().toString(), 1, null, null, List.of(), null,
                List.of(new Protocol.Jdk("temurin-" + wanted, jdk.toString(), wanted + ".0.1", wanted, "Eclipse Adoptium")));
        assertEquals(jdk.toAbsolutePath().normalize().toString(), ImportJvms.forGradle(project.getParent(), with).home());
    }

    static Path fakeJdk(Path home, String version) throws IOException {
        Path bin = Files.createDirectories(home.resolve("bin"));
        Files.writeString(bin.resolve(MavenEvaluator.isWindows() ? "java.exe" : "java"), "");
        Files.writeString(home.resolve("release"), "JAVA_VERSION=\"" + version + "\"" + System.lineSeparator());
        return home;
    }

    @Test
    void languageLevelsReadAsIntelliJWritesThem() {
        assertEquals(8, IdeaEvaluator.languageLevel("JDK_1_8"));
        assertEquals(25, IdeaEvaluator.languageLevel("JDK_25"));
        assertNull(IdeaEvaluator.languageLevel("JDK_X"));
        assertNull(IdeaEvaluator.languageLevel(null));
    }

    @Test
    void anIntelliJJdkNameAsksForItsOwnFeatureBeforeTheLanguageLevel() {
        assertEquals(new Protocol.Requirement("corretto-21", 21, null, "idea"), IdeaEvaluator.requirement("corretto-21", 17));
        assertEquals(21, IdeaEvaluator.requirement("temurin-21.0.5", 17).feature());
        assertEquals(17, IdeaEvaluator.requirement("17", 11).feature());
        assertEquals(17, IdeaEvaluator.requirement("17 (2)", 11).feature());
        assertEquals(8, IdeaEvaluator.requirement("1.8", 8).feature());
        assertEquals(8, IdeaEvaluator.requirement("jdk1.8.0_202", null).feature());
        assertEquals(21, IdeaEvaluator.requirement("JDK-21", null).feature());
        // Digits inside a word or a hash are not a release.
        assertEquals(17, IdeaEvaluator.requirement("my-jdk-v2", 17).feature());
        assertEquals(17, IdeaEvaluator.requirement("wanted-0012ab34", 17).feature());
        // A name that carries no release falls back to the language level, the least the module compiles with.
        assertEquals(17, IdeaEvaluator.requirement("Project JDK", 17).feature());
        assertNull(IdeaEvaluator.requirement("Project JDK", null).feature());
        assertNull(IdeaEvaluator.requirement(null, 17));
    }

    @Test
    void aJdkOfTheNamedReleaseServesALowerLanguageLevel(@TempDir Path root) throws IOException {
        // IntelliJ names corretto-<n>, the language level is lower and no JDK of that name is here: a JDK <n> of
        // another vendor serves it rather than a missing JDK of the language level.
        int wanted = Runtime.version().feature() + 7;
        Path jdk = fakeJdk(root.resolve("jdks").resolve("temurin"), wanted + ".0.1");
        Path project = intellijProject(root.resolve("project"), "corretto-" + wanted, "JDK_17", null);
        Protocol.Jdk temurin = new Protocol.Jdk("temurin-" + wanted, jdk.toString(), wanted + ".0.1", wanted, "Eclipse Adoptium");
        Protocol.BuildTarget module = evaluate(project, temurin);
        assertEquals(new Protocol.Requirement("corretto-" + wanted, wanted, null, "idea"), module.jvmBuildTarget().required());
        Protocol.JvmEnvironment settled = ImportJvms.settle(module.jvmBuildTarget(),
                new Protocol.ImportParams(project.toString(), 1, null, null, List.of(), null, List.of(temurin)));
        assertEquals(jdk.toAbsolutePath().normalize().toString(), settled.javaHome());
        assertEquals(17, settled.javaVersion());
    }

    @Test
    void aModuleLanguageLevelOverridesTheProjects(@TempDir Path root) throws IOException {
        Path project = intellijProject(root.resolve("project"), "Project JDK", "JDK_17", "JDK_11");
        Protocol.BuildTarget module = evaluate(project);
        assertEquals(11, module.jvmBuildTarget().javaVersion());
        assertEquals(11, module.jvmBuildTarget().required().feature());
        Path inherits = intellijProject(root.resolve("inherits"), "Project JDK", "JDK_17", null);
        assertEquals(17, evaluate(inherits).jvmBuildTarget().javaVersion());
    }

    private static Protocol.BuildTarget evaluate(Path project, Protocol.Jdk... jdks) {
        Protocol.ImportParams params = new Protocol.ImportParams(project.toString(), 1, null, null, List.of(), null, List.of(jdks));
        return new IdeaEvaluator().evaluate(project, params).targets().get(0);
    }

    private static Path intellijProject(Path root, String projectJdk, String projectLevel, String moduleLevel) throws IOException {
        Files.createDirectories(root.resolve("src"));
        Files.createDirectories(root.resolve(".idea"));
        Files.writeString(root.resolve(".idea").resolve("misc.xml"), """
                <?xml version="1.0" encoding="UTF-8"?>
                <project version="4">
                  <component name="ProjectRootManager" version="2" languageLevel="%s" project-jdk-name="%s" project-jdk-type="JavaSDK">
                    <output url="file://$PROJECT_DIR$/out" />
                  </component>
                </project>
                """.formatted(projectLevel, projectJdk));
        Files.writeString(root.resolve("demo.iml"), """
                <?xml version="1.0" encoding="UTF-8"?>
                <module type="JAVA_MODULE" version="4">
                  <component name="NewModuleRootManager" inherit-compiler-output="true"%s>
                    <content url="file://$MODULE_DIR$">
                      <sourceFolder url="file://$MODULE_DIR$/src" isTestSource="false" />
                    </content>
                    <orderEntry type="inheritedJdk" />
                    <orderEntry type="sourceFolder" forTests="false" />
                  </component>
                </module>
                """.formatted(moduleLevel == null ? "" : " LANGUAGE_LEVEL=\"" + moduleLevel + "\""));
        return root;
    }

    @Test
    void urlsExpandTheirMacros(@TempDir Path root) {
        assertEquals(root.resolve("lib").resolve("demo.jar").toString(),
                IdeaEvaluator.expand("jar://$MODULE_DIR$/lib/demo.jar!/", root, root));
        assertEquals(root.resolve("src").toString(), IdeaEvaluator.expand("file://$MODULE_DIR$/src", root, root));
    }

    @Test
    void anXmlFileWithADoctypeIsRefused(@TempDir Path root) throws IOException {
        Path evil = root.resolve("evil.iml");
        Files.writeString(evil, "<!DOCTYPE module [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><module type=\"JAVA_MODULE\">&x;</module>");
        org.junit.jupiter.api.Assertions.assertThrows(Problems.Problem.class, () -> IdeaEvaluator.load(evil));
    }
}
