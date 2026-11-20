package wayfarer.host;

import java.io.FileDescriptor;
import java.io.FileOutputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;

/**
 * The host's entry point, compiled for Java 8 so that any JVM can run it: it checks the JVM is new enough before
 * loading the host itself, and otherwise writes the reason as the protocol's first line.
 */
public final class Launcher {
    public static final int MINIMUM_JAVA = 21;

    private Launcher() {
    }

    public static void main(String[] args) throws Exception {
        int feature = feature(System.getProperty("java.specification.version", "0"));
        if (feature < MINIMUM_JAVA) {
            String reason = "the JVM at " + System.getProperty("java.home") + " is Java " + feature
                    + ", and the project host needs Java " + MINIMUM_JAVA + " or later";
            OutputStream out = new FileOutputStream(FileDescriptor.out);
            out.write(("{\"id\":0,\"error\":\"" + escape(reason) + "\"}\n").getBytes(StandardCharsets.UTF_8));
            out.flush();
            System.exit(2);
        }
        Class.forName("wayfarer.host.Host").getMethod("main", String[].class).invoke(null, (Object) args);
    }

    static int feature(String specification) {
        String version = specification.startsWith("1.") ? specification.substring(2) : specification;
        int end = 0;
        while (end < version.length() && Character.isDigit(version.charAt(end))) {
            end++;
        }
        return end == 0 ? 0 : Integer.parseInt(version.substring(0, end));
    }

    private static String escape(String text) {
        StringBuilder escaped = new StringBuilder(text.length() + 8);
        for (char c : text.toCharArray()) {
            switch (c) {
                case '"': escaped.append("\\\""); break;
                case '\\': escaped.append("\\\\"); break;
                case '\n': escaped.append("\\n"); break;
                case '\r': escaped.append("\\r"); break;
                case '\t': escaped.append("\\t"); break;
                default:
                    if (c < 0x20) {
                        escaped.append(String.format("\\u%04x", (int) c));
                    } else {
                        escaped.append(c);
                    }
            }
        }
        return escaped.toString();
    }
}
