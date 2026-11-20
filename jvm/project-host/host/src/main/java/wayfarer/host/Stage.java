package wayfarer.host;

import java.net.URISyntaxException;
import java.nio.file.Path;

/** The folder the host was staged into: host.jar's own, holding lib/ and the evaluators' jars. */
final class Stage {
    private Stage() {
    }

    static Path folder() {
        String named = System.getProperty("wayfarer.stage");
        if (named != null) {
            return Path.of(named);
        }
        try {
            Path jar = Path.of(Host.class.getProtectionDomain().getCodeSource().getLocation().toURI());
            return jar.getParent();
        } catch (URISyntaxException | SecurityException failure) {
            throw new IllegalStateException("the project host cannot tell where it was staged", failure);
        }
    }
}
