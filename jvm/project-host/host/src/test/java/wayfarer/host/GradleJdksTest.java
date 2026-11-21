package wayfarer.host;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/** The JDK homes Gradle is told about: the person's and the project's own paths kept first, then the table's. */
class GradleJdksTest {
    @Test
    void thePersonsAndTheProjectsPathsComeFirstAndEachPathOnce(@TempDir Path folder) throws IOException {
        Path user = Files.createDirectories(folder.resolve("gradle-home"));
        Path root = Files.createDirectories(folder.resolve("project"));
        Files.writeString(user.resolve("gradle.properties"), "org.gradle.java.installations.paths=/opt/mine, /opt/shared\n");
        Files.writeString(root.resolve("gradle.properties"), "org.gradle.java.installations.paths=/opt/project\n");
        List<Protocol.Jdk> table = List.of(
                new Protocol.Jdk("temurin-21", "/opt/shared", "21.0.5", 21, "Eclipse Adoptium"),
                new Protocol.Jdk("corretto-17", "/opt/corretto-17", "17.0.12", 17, "Amazon.com Inc."));

        assertEquals(List.of("/opt/mine", "/opt/shared", "/opt/project", "/opt/corretto-17"), GradleJdks.paths(root, table, user));
        assertEquals("-Porg.gradle.java.installations.paths=/opt/mine,/opt/shared,/opt/project,/opt/corretto-17",
                GradleJdks.argument(root, table, user));
    }

    @Test
    void anEmptyTableLeavesGradlesOwnSettingAlone(@TempDir Path folder) throws IOException {
        Path root = Files.createDirectories(folder.resolve("project"));
        Files.writeString(root.resolve("gradle.properties"), "org.gradle.java.installations.paths=/opt/project\n");
        assertNull(GradleJdks.argument(root, List.of(), folder.resolve("gradle-home")));
    }
}
