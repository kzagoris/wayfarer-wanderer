package wayfarer.host;

import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;

/** Whether a request was cancelled, with what to tell when it is: the thread running it and any build tool's own token. */
public final class Cancellation {
    private final List<Runnable> listeners = new CopyOnWriteArrayList<>();
    private volatile boolean cancelled;
    private volatile Thread thread;

    public boolean isCancelled() {
        return cancelled;
    }

    public void throwIfCancelled() {
        if (cancelled) {
            throw new Cancelled();
        }
    }

    /** Runs {@code listener} on cancel, at once when the request is cancelled already. */
    public void onCancel(Runnable listener) {
        listeners.add(listener);
        if (cancelled) {
            listener.run();
        }
    }

    public void cancel() {
        if (cancelled) {
            return;
        }
        cancelled = true;
        listeners.forEach(Runnable::run);
        Thread running = thread;
        if (running != null) {
            running.interrupt();
        }
    }

    void bind(Thread running) {
        thread = running;
        if (cancelled) {
            running.interrupt();
        }
    }

    void unbind() {
        thread = null;
    }

    public static final class Cancelled extends RuntimeException {
        private static final long serialVersionUID = 1L;

        public Cancelled() {
            super("cancelled", null, false, false);
        }
    }
}
