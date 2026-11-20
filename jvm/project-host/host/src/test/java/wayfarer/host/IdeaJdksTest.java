package wayfarer.host;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/** Ported from the app's IdeaJdkTableResolvesTheNamedHome: the IntelliJ JDK table names a home by its JDK name. */
class IdeaJdksTest {
    @Test
    void theJdkTableResolvesTheNamedHome(@TempDir Path root) throws IOException {
        Path jdk = Files.createDirectories(root.resolve("fake-jdk"));
        Path options = Files.createDirectories(root.resolve("config").resolve("IntelliJIdeaTest").resolve("options"));
        Files.writeString(options.resolve("jdk.table.xml"),
                "<application><component name=\"ProjectJdkTable\"><jdk version=\"2\"><name value=\"openjdk-25\" /><homePath value=\""
                        + jdk + "\" /></jdk></component></application>");
        assertEquals(jdk.toAbsolutePath().normalize().toString(), IdeaEvaluator.IdeaJdks.home("openjdk-25", root.resolve("config"), null));
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
