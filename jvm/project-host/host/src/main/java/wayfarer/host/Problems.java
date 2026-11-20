package wayfarer.host;

/** A failure in words the window can show: the outermost message that says something, without exception class names. */
public final class Problems {
    private Problems() {
    }

    public static String say(Throwable error) {
        String said = null;
        for (Throwable cause = error; cause != null; cause = cause.getCause() == cause ? null : cause.getCause()) {
            if (cause instanceof Problem problem) {
                return problem.getMessage();
            }
            String message = cause.getMessage();
            if (said == null && message != null && !message.isBlank()) {
                said = message.strip();
            }
        }
        return said != null ? said : error.getClass().getSimpleName();
    }

    /** A failure whose message is already what the window should show. */
    public static final class Problem extends RuntimeException {
        private static final long serialVersionUID = 1L;

        public Problem(String message) {
            super(message);
        }

        public Problem(String message, Throwable cause) {
            super(message, cause);
        }
    }
}
