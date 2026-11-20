package wayfarer.host;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParseException;
import java.io.BufferedReader;
import java.io.FileDescriptor;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.PrintStream;
import java.io.Reader;
import java.nio.charset.StandardCharsets;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * The host process: requests read from standard input until it closes, which is the app letting go. Each request runs
 * on a thread of its own, so one window's import never waits behind another's; pings and cancels are answered on the
 * reader's thread at once. Standard output is the protocol's alone: whatever else writes to it goes to standard error.
 */
public final class Host {
    static final Gson JSON = new GsonBuilder().disableHtmlEscaping().serializeNulls().create();

    private final Replies replies;
    private final Map<Integer, Cancellation> running = new ConcurrentHashMap<>();
    private final ExecutorService workers = Executors.newCachedThreadPool(runnable -> {
        Thread thread = new Thread(runnable, "wayfarer-request");
        thread.setDaemon(true);
        return thread;
    });
    private final Methods methods;

    Host(Replies replies, Methods methods) {
        this.replies = replies;
        this.methods = methods;
    }

    public static void main(String[] args) {
        OutputStream protocol = new FileOutputStream(FileDescriptor.out);
        // Before anything else can print: the Tooling API, Maven and any logging library write to System.out.
        System.setOut(new PrintStream(new FileOutputStream(FileDescriptor.err), true, StandardCharsets.UTF_8));
        Replies replies = new Replies(protocol);
        Host host = new Host(replies, Methods.standard(System.getenv("WAYFARER_HOST_TEST") != null));
        replies.result(0, new Protocol.Ready(Protocol.VERSION, Jvms.current(), ProcessHandle.current().pid()));
        host.run(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        // Input closed: the app has let go. Tooling API connections hold non-daemon threads, so end outright.
        Runtime.getRuntime().halt(0);
    }

    void run(Reader input) {
        try (BufferedReader lines = new BufferedReader(input)) {
            String line;
            while ((line = lines.readLine()) != null) {
                if (!line.isBlank()) {
                    accept(line);
                }
            }
        } catch (IOException error) {
            System.err.println("wayfarer host: standard input failed: " + error);
        }
        running.values().forEach(Cancellation::cancel);
    }

    private void accept(String line) {
        Protocol.Request request;
        try {
            request = JSON.fromJson(line, Protocol.Request.class);
        } catch (JsonParseException error) {
            System.err.println("wayfarer host: a request that does not read: " + error.getMessage());
            return;
        }
        if (request == null || request.method() == null) {
            System.err.println("wayfarer host: a request without a method: " + line);
            return;
        }
        switch (request.method()) {
            case Protocol.PING -> replies.result(request.id(), "pong");
            case Protocol.CANCEL -> {
                Protocol.CancelParams cancel = read(request, Protocol.CancelParams.class);
                Cancellation target = running.get(cancel.request());
                if (target != null) {
                    target.cancel();
                }
                replies.result(request.id(), target != null);
            }
            default -> {
                if (methods.onReaderThread(request.method())) {
                    // Only the test methods that stand for a host gone unresponsive.
                    try {
                        methods.run(request, new Context(request.id(), replies, new Cancellation()));
                    } catch (Exception error) {
                        replies.error(request.id(), Problems.say(error));
                    }
                    return;
                }
                // Registered before the next line is read, or a cancel right behind it would find nothing.
                Cancellation cancellation = new Cancellation();
                running.put(request.id(), cancellation);
                workers.execute(() -> handle(request, cancellation));
            }
        }
    }

    private void handle(Protocol.Request request, Cancellation cancellation) {
        cancellation.bind(Thread.currentThread());
        try {
            Object result = methods.run(request, new Context(request.id(), replies, cancellation));
            if (cancellation.isCancelled()) {
                replies.error(request.id(), "cancelled");
            } else {
                replies.result(request.id(), result);
            }
        } catch (Cancellation.Cancelled cancelled) {
            replies.error(request.id(), "cancelled");
        } catch (Throwable error) {
            if (cancellation.isCancelled()) {
                replies.error(request.id(), "cancelled");
            } else {
                System.err.println("wayfarer host: " + request.method() + " failed:");
                error.printStackTrace(System.err);
                replies.error(request.id(), Problems.say(error));
            }
        } finally {
            cancellation.unbind();
            running.remove(request.id());
            Thread.interrupted();
        }
    }

    static <T> T read(Protocol.Request request, Class<T> type) {
        JsonElement params = request.params() == null ? new JsonObject() : request.params();
        T value = JSON.fromJson(params, type);
        if (value == null) {
            throw new IllegalArgumentException(request.method() + " was sent no parameters");
        }
        return value;
    }

    /** What a method is given: its request's id, where to send partial answers, and whether it was cancelled. */
    public record Context(int id, Replies replies, Cancellation cancellation) {
        public void partial(Object value) {
            replies.partial(id, value);
        }
    }
}
