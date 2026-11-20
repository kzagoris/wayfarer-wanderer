package wayfarer.host;

import com.google.gson.JsonObject;
import java.io.IOException;
import java.io.OutputStream;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;

/** Replies written to the protocol's stream, each line whole under one lock. */
public final class Replies {
    private final OutputStream out;

    public Replies(OutputStream out) {
        this.out = out;
    }

    public void partial(int id, Object value) {
        write(id, "partial", value);
    }

    public void result(int id, Object value) {
        write(id, "result", value);
    }

    public void error(int id, String reason) {
        write(id, "error", reason == null ? "failed" : reason);
    }

    private void write(int id, String kind, Object value) {
        JsonObject line = new JsonObject();
        line.addProperty("id", id);
        line.add(kind, Host.JSON.toJsonTree(value));
        byte[] bytes = (Host.JSON.toJson(line) + "\n").getBytes(StandardCharsets.UTF_8);
        synchronized (this) {
            try {
                out.write(bytes);
                out.flush();
            } catch (IOException error) {
                // The app has gone, and nothing reads what is left.
                throw new UncheckedIOException(error);
            }
        }
    }
}
