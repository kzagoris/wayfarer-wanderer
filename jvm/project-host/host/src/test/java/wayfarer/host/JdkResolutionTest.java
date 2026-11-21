package wayfarer.host;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/**
 * The order a JDK requirement picks from the IDE's table, read from the golden jdk-resolution.json the C# tests read
 * too, and how the host settles each module's requirement against the table it was sent.
 */
class JdkResolutionTest {
    private static final Path EXAMPLES = Path.of(System.getProperty("wayfarer.protocolExamples"));

    @Test
    void requirementsResolveInTheSharedOrder() throws IOException {
        JsonObject golden = golden();
        List<Protocol.Jdk> table = new ArrayList<>();
        for (JsonElement jdk : golden.getAsJsonArray("jdks")) {
            table.add(Host.JSON.fromJson(jdk, Protocol.Jdk.class));
        }
        for (JsonElement item : golden.getAsJsonArray("cases")) {
            JsonObject entry = item.getAsJsonObject();
            Protocol.Requirement requirement = Host.JSON.fromJson(entry.get("requirement"), Protocol.Requirement.class);
            Protocol.Jdk chosen = ImportJvms.choose(requirement, table);
            String expected = entry.get("chosen").isJsonNull() ? null : entry.get("chosen").getAsString();
            assertEquals(expected, chosen == null ? null : chosen.name(), entry.get("why").getAsString());
        }
    }

    @Test
    void distributionsReadAsTheGoldenFileSays() throws IOException {
        for (JsonElement item : golden().getAsJsonArray("distributions")) {
            JsonObject entry = item.getAsJsonObject();
            String vendor = entry.get("vendor").isJsonNull() ? null : entry.get("vendor").getAsString();
            assertEquals(entry.get("distribution").getAsString(), ImportJvms.distribution(vendor), String.valueOf(vendor));
        }
    }

    @Test
    void aModulesOwnRequirementTakesItsHomeFromTheTableAndAMissingOneStaysMissing(@TempDir Path root) throws IOException {
        int absent = Runtime.version().feature() + 40;
        Path jdk = IdeaJdksTest.fakeJdk(root.resolve("temurin-17"), "17.0.12");
        Protocol.ImportParams params = params(root, null, null,
                List.of(new Protocol.Jdk("temurin-17", jdk.toString(), "17.0.12", 17, "Eclipse Adoptium")));
        Protocol.Requirement seventeen = new Protocol.Requirement(null, 17, null, "gradle-toolchain");
        Protocol.Requirement missing = new Protocol.Requirement(null, absent, null, "gradle-toolchain");

        Protocol.JvmEnvironment met = ImportJvms.settle(new Protocol.JvmEnvironment(null, 17, null, seventeen), params);
        assertEquals(jdk.toAbsolutePath().normalize().toString(), met.javaHome());
        assertEquals(seventeen, met.required());
        Protocol.JvmEnvironment unmet = ImportJvms.settle(new Protocol.JvmEnvironment(null, absent, null, missing), params);
        assertNull(unmet.javaHome());
        assertEquals(missing, unmet.required());
        Protocol.JvmEnvironment found = new Protocol.JvmEnvironment("/gradle/found/it", 17, null, seventeen);
        assertEquals(found, ImportJvms.settle(found, params), "a home the build found itself stands");
    }

    @Test
    void aModuleWithoutOneTakesTheProjectsRequirement(@TempDir Path root) throws IOException {
        Path jdk = IdeaJdksTest.fakeJdk(root.resolve("jdk-21"), "21.0.5");
        Protocol.Requirement project = new Protocol.Requirement("temurin-21", 21, "temurin", "project");
        Protocol.JvmEnvironment inherited = new Protocol.JvmEnvironment(jdk.toString(), 21, null, null);

        Protocol.JvmEnvironment met = ImportJvms.settle(inherited, params(root, jdk.toString(), project, List.of()));
        assertEquals(jdk.toString(), met.javaHome());
        assertEquals(project, met.required());
        Protocol.JvmEnvironment unmet = ImportJvms.settle(new Protocol.JvmEnvironment("/java/home", 21, null, null), params(root, null, project, List.of()));
        assertNull(unmet.javaHome(), "a project requirement no JDK meets leaves the module without one");
        assertEquals(project, unmet.required());
        Protocol.JvmEnvironment none = new Protocol.JvmEnvironment("/java/home", 21, null, null);
        assertEquals(none, ImportJvms.settle(none, params(root, null, null, List.of())));
    }

    private static Protocol.ImportParams params(Path root, String jdk, Protocol.Requirement required, List<Protocol.Jdk> jdks) {
        return new Protocol.ImportParams(root.toString(), 1, jdk, required, List.of(), null, jdks);
    }

    private static JsonObject golden() throws IOException {
        return JsonParser.parseString(Files.readString(EXAMPLES.resolve("jdk-resolution.json"))).getAsJsonObject();
    }
}
