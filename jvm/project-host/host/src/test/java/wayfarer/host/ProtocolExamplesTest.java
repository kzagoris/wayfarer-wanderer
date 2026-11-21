package wayfarer.host;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import org.junit.jupiter.api.Test;

/** The golden examples the C# client's tests read too: each reads into the host's records and writes back the same. */
class ProtocolExamplesTest {
    private static final Path EXAMPLES = Path.of(System.getProperty("wayfarer.protocolExamples"));

    @Test
    void repliesRoundTripThroughTheRecords() throws IOException {
        roundTrip("ready.json", "result", Protocol.Ready.class);
        roundTrip("partial-target.json", "partial", Protocol.TargetEvaluated.class);
        roundTrip("result-import.json", "result", Protocol.WorkspaceResult.class);
    }

    @Test
    void requestsRead() throws IOException {
        Protocol.Request request = Host.JSON.fromJson(Files.readString(EXAMPLES.resolve("request-import.json")), Protocol.Request.class);
        Protocol.ImportParams params = Host.read(request, Protocol.ImportParams.class);
        assertEquals("/work/layouts", params.folder());
        assertEquals(12, params.generation());
        assertEquals("-slow", params.mavenProfiles().get(0));
        assertEquals("temurin-24", params.jdks().get(0).name());
        assertEquals(24, params.jdks().get(0).feature());
        assertEquals(new Protocol.Requirement("temurin-24", 24, "temurin", "project"), params.jdkRequired());
        assertEquals("/libs", params.pathVariables().get("SHARED_LIBS"));
        assertEquals("Shared", params.globalLibraries().get(0).name());
        Protocol.Request cancel = Host.JSON.fromJson(Files.readString(EXAMPLES.resolve("request-cancel.json")), Protocol.Request.class);
        assertEquals(7, Host.read(cancel, Protocol.CancelParams.class).request());
    }

    @Test
    void theHostAnswersPingAndAnErrorAsTheExamplesSay() throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        Host host = new Host(new Replies(out), Methods.standard(false));
        String input = Files.readString(EXAMPLES.resolve("request-ping.json"));
        host.run(new java.io.StringReader(input));
        JsonElement expected = JsonParser.parseString(Files.readString(EXAMPLES.resolve("result-ping.json")));
        assertEquals(expected, JsonParser.parseString(out.toString(StandardCharsets.UTF_8).strip()));
    }

    @Test
    void writtenLinesAreSingleLines() {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        new Replies(out).error(3, "line one\nline two");
        String written = out.toString(StandardCharsets.UTF_8);
        assertTrue(written.endsWith("\n"));
        assertEquals(1, written.strip().lines().count());
    }

    private static <T> void roundTrip(String file, String kind, Class<T> type) throws IOException {
        JsonObject line = JsonParser.parseString(Files.readString(EXAMPLES.resolve(file))).getAsJsonObject();
        JsonElement value = line.get(kind);
        T read = Host.JSON.fromJson(value, type);
        assertEquals(value, Host.JSON.toJsonTree(read), file);
    }
}
