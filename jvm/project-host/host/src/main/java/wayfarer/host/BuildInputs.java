package wayfarer.host;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.BasicFileAttributes;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;

/**
 * Stamps the files an evaluation read, by the rule the app checks them with: last write in milliseconds since the
 * epoch and length, or 0 and -1 for a file that was absent (whose coming is a change too).
 */
final class BuildInputs {
    private BuildInputs() {
    }

    static List<Protocol.BuildInput> stamp(Iterable<String> paths) {
        List<Protocol.BuildInput> stamped = new ArrayList<>();
        for (String path : new LinkedHashSet<>(toList(paths))) {
            stamped.add(stamp(Path.of(path)));
        }
        return stamped;
    }

    static Protocol.BuildInput stamp(Path path) {
        Path absolute = path.toAbsolutePath().normalize();
        try {
            BasicFileAttributes attributes = Files.readAttributes(absolute, BasicFileAttributes.class);
            return attributes.isRegularFile()
                    ? new Protocol.BuildInput(absolute.toString(), attributes.lastModifiedTime().toMillis(), attributes.size())
                    : new Protocol.BuildInput(absolute.toString(), 0, -1);
        } catch (IOException absent) {
            return new Protocol.BuildInput(absolute.toString(), 0, -1);
        }
    }

    private static List<String> toList(Iterable<String> paths) {
        List<String> list = new ArrayList<>();
        paths.forEach(list::add);
        return list;
    }
}
