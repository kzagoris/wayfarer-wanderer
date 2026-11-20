package wayfarer.host;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.charset.MalformedInputException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.stream.Stream;

/**
 * Finds runnable entry points in a target's main source roots, after the ones the build configures: a Java type with
 * {@code static void main(String[])}, and a Kotlin file with a top-level {@code main} (its class {@code FileKt}, or the
 * name {@code @file:JvmName} gives it).
 */
public final class MainScan {
    private static final Pattern PACKAGE = Pattern.compile("(?m)^\\s*package\\s+([\\w.]+)");
    private static final Pattern JAVA_MAIN = Pattern.compile("\\b(?:public\\s+static|static\\s+public)\\s+void\\s+main\\s*\\(");
    private static final Pattern JAVA_TYPE = Pattern.compile("\\b(?:class|record|interface|enum)\\s+([A-Za-z_$][\\w$]*)");
    private static final Pattern KOTLIN_MAIN = Pattern.compile("(?m)^\\s*fun\\s+main\\s*\\(");
    private static final Pattern KOTLIN_JVM_NAME = Pattern.compile("@file\\s*:\\s*JvmName\\s*\\(\\s*\"([^\"]+)\"");

    private MainScan() {
    }

    /** The target with the mains its sources hold added after the configured ones, each class once. */
    public static Protocol.BuildTarget withDiscovered(Protocol.BuildTarget target) {
        Map<String, Protocol.MainClass> mains = new LinkedHashMap<>();
        for (Protocol.MainClass configured : nullToEmpty(target.mainClasses())) {
            mains.putIfAbsent(configured.className(), configured);
        }
        for (Protocol.SourceSet set : nullToEmpty(target.sourceSets())) {
            if (set.test()) {
                continue;
            }
            for (Protocol.SourceItem source : nullToEmpty(set.sources())) {
                if (!"resources".equals(source.kind())) {
                    scan(Path.of(source.uri()), mains);
                }
            }
        }
        // A configured main found in the sources takes its file from there.
        List<Protocol.MainClass> merged = new ArrayList<>(mains.values());
        return new Protocol.BuildTarget(target.id(), target.displayName(), target.baseDirectory(), target.languageIds(),
                target.jvmBuildTarget(), target.runEnvironment(), target.sourceSets(), target.tasks(), merged,
                target.activeProfiles(), target.absentLibraries(), target.problem());
    }

    private static void scan(Path root, Map<String, Protocol.MainClass> mains) {
        if (!Files.isDirectory(root)) {
            return;
        }
        try (Stream<Path> files = Files.walk(root)) {
            files.filter(Files::isRegularFile)
                    .filter(file -> file.toString().endsWith(".java") || file.toString().endsWith(".kt"))
                    .sorted()
                    .forEach(file -> {
                        String className = mainOf(file);
                        if (className == null) {
                            return;
                        }
                        Protocol.MainClass known = mains.get(className);
                        if (known == null) {
                            mains.put(className, new Protocol.MainClass(className, file.toString(), "discovered"));
                        } else if (known.sourcePath() == null || known.sourcePath().isEmpty()) {
                            mains.put(className, new Protocol.MainClass(className, file.toString(), known.origin()));
                        }
                    });
        } catch (IOException | RuntimeException unreadable) {
            System.err.println("wayfarer host: could not scan " + root + " for mains: " + unreadable);
        }
    }

    static String mainOf(Path file) {
        String text;
        try {
            text = Files.readString(file, StandardCharsets.UTF_8);
        } catch (MalformedInputException notUtf8) {
            return null;
        } catch (IOException unreadable) {
            return null;
        }
        Matcher pkg = PACKAGE.matcher(text);
        String prefix = pkg.find() ? pkg.group(1) + "." : "";
        String name = file.getFileName().toString();
        if (name.endsWith(".java") && JAVA_MAIN.matcher(text).find()) {
            Matcher type = JAVA_TYPE.matcher(text);
            return type.find() ? prefix + type.group(1) : null;
        }
        if (name.endsWith(".kt") && KOTLIN_MAIN.matcher(text).find()) {
            Matcher jvmName = KOTLIN_JVM_NAME.matcher(text);
            String stem = name.substring(0, name.length() - 3);
            return prefix + (jvmName.find() ? jvmName.group(1) : Character.toUpperCase(stem.charAt(0)) + stem.substring(1) + "Kt");
        }
        return null;
    }

    private static <T> List<T> nullToEmpty(List<T> list) {
        return list == null ? List.of() : list;
    }
}
