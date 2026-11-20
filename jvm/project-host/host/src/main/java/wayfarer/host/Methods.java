package wayfarer.host;

import com.google.gson.JsonObject;
import java.util.Set;

/**
 * The methods a request may name beyond ping and cancel. The test methods exist only when the host is started with
 * WAYFARER_HOST_TEST set, for the client's tests of partial answers, errors, cancelling, a crash and a hang.
 */
public final class Methods {
    private static final Set<String> READER_THREAD = Set.of("test.hang");

    private final boolean testing;
    private final Workspaces workspaces = new Workspaces();

    private Methods(boolean testing) {
        this.testing = testing;
    }

    public static Methods standard(boolean testing) {
        return new Methods(testing);
    }

    boolean onReaderThread(String method) {
        return testing && READER_THREAD.contains(method);
    }

    Object run(Protocol.Request request, Host.Context context) throws Exception {
        if (Protocol.IMPORT.equals(request.method())) {
            return workspaces.importWorkspace(Host.read(request, Protocol.ImportParams.class), context);
        }
        if (testing && request.method().startsWith("test.")) {
            return test(request, context);
        }
        throw new Problems.Problem("the project host has no method " + request.method());
    }

    private static Object test(Protocol.Request request, Host.Context context) throws InterruptedException {
        JsonObject params = request.params() != null && request.params().isJsonObject()
                ? request.params().getAsJsonObject()
                : new JsonObject();
        int count = params.has("count") ? params.get("count").getAsInt() : 0;
        switch (request.method()) {
            case "test.partials" -> {
                for (int i = 0; i < count; i++) {
                    context.cancellation().throwIfCancelled();
                    context.partial(i);
                }
                return count;
            }
            case "test.fail" -> throw new Problems.Problem(params.has("reason") ? params.get("reason").getAsString() : "failed on purpose");
            case "test.wait" -> {
                context.partial("waiting");
                while (true) {
                    context.cancellation().throwIfCancelled();
                    Thread.sleep(20);
                }
            }
            case "test.crash" -> {
                context.partial("crashing");
                System.err.println("Unhandled exception. wayfarer host test crash");
                Runtime.getRuntime().halt(3);
                return null;
            }
            case "test.hang" -> {
                // On the reader thread: nothing more is read, pings included, as in a host gone unresponsive.
                while (true) {
                    Thread.sleep(60_000);
                }
            }
            default -> throw new Problems.Problem("the project host has no method " + request.method());
        }
    }
}
