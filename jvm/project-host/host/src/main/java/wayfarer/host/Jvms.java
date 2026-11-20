package wayfarer.host;

import java.io.IOException;
import java.io.InputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Properties;

/** JVMs as the protocol names them, read from a home's {@code release} file. */
public final class Jvms {
    private Jvms() {
    }

    public static Protocol.Jvm current() {
        return new Protocol.Jvm(
                System.getProperty("java.home"),
                System.getProperty("java.version"),
                Runtime.version().feature(),
                System.getProperty("java.vendor"));
    }

    /** The JVM at {@code home}, or one with the home alone when its release file does not read. */
    public static Protocol.Jvm at(Path home) {
        Properties release = new Properties();
        try (InputStream in = Files.newInputStream(home.resolve("release"))) {
            release.load(in);
        } catch (IOException missing) {
            return new Protocol.Jvm(home.toString(), null, null, null);
        }
        String version = unquote(release.getProperty("JAVA_VERSION"));
        Integer feature = version == null ? null : feature(version);
        return new Protocol.Jvm(home.toString(), version, feature, unquote(release.getProperty("IMPLEMENTOR")));
    }

    /** The feature release a version string names: 8 for "1.8.0_402", 22 for "22.0.1". */
    public static int feature(String version) {
        String rest = version.startsWith("1.") ? version.substring(2) : version;
        int end = 0;
        while (end < rest.length() && Character.isDigit(rest.charAt(end))) {
            end++;
        }
        return end == 0 ? 0 : Integer.parseInt(rest.substring(0, end));
    }

    private static String unquote(String value) {
        if (value == null) {
            return null;
        }
        String trimmed = value.strip();
        return trimmed.length() >= 2 && trimmed.startsWith("\"") && trimmed.endsWith("\"")
                ? trimmed.substring(1, trimmed.length() - 1)
                : trimmed;
    }
}
