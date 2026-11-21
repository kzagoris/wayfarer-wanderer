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
