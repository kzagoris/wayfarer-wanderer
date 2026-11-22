package wayfarer.host;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.charset.MalformedInputException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.stream.Stream;

/**
 * Finds runnable entry points in a target's main source roots, after the ones the build configures: a Java type with a
 * launchable {@code main}, and a Kotlin file with a top-level {@code main} (its class {@code FileKt}, or the name
 * {@code @file:JvmName} gives it).
 *
 * <p>A Java {@code main} is launchable as the launcher of Java 25 and later (JEP 512) has it: static or instance, with a
 * {@code String[]} parameter or none, and anything but private. An instance {@code main} needs a class to construct, so
 * it counts in a class only. A {@code main} outside every type makes a compact source file, whose implicit class is
 * named after the file and sits in the unnamed package. A nested type's {@code main} is launched by its binary name,
 * {@code Outer$Inner}.
 */
public final class MainScan {
    private static final Pattern PACKAGE = Pattern.compile("(?m)^\\s*package\\s+([\\w.]+)");
    private static final Pattern JAVA_TOKEN = Pattern.compile("[A-Za-z_$][\\w$]*|\\.\\.\\.|\\S");
    private static final Pattern STRING_PARAMETER = Pattern.compile(
            "(?:final)?(?:java\\.lang\\.)?String(?:\\[]|\\.\\.\\.)[A-Za-z_$][\\w$]*|(?:final)?(?:java\\.lang\\.)?String[A-Za-z_$][\\w$]*\\[]");
    /** A brace that opens no type's body. A type declared in one is local, and the launcher cannot name it. */
    private static final String[] BLOCK = {};
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
        if (name.endsWith(".java")) {
            return javaMainOf(text, prefix, name.substring(0, name.length() - 5));
        }
        if (name.endsWith(".kt") && KOTLIN_MAIN.matcher(text).find()) {
            Matcher jvmName = KOTLIN_JVM_NAME.matcher(text);
            String stem = name.substring(0, name.length() - 3);
            return prefix + (jvmName.find() ? jvmName.group(1) : Character.toUpperCase(stem.charAt(0)) + stem.substring(1) + "Kt");
        }
        return null;
    }

    /**
     * The class of the first launchable {@code main} in Java source: the binary name of the type that declares it, or
     * the file's stem for a compact source file.
     */
    static String javaMainOf(String text, String prefix, String stem) {
        List<String> tokens = javaTokens(text);
        // Each open brace: the type whose body it opens ({binary name, kind}), or BLOCK for any other.
        Deque<String[]> scopes = new ArrayDeque<>();
        String pendingName = null;
        String pendingKind = null;
        for (int i = 0; i < tokens.size(); i++) {
            String token = tokens.get(i);
            String previous = i > 0 ? tokens.get(i - 1) : "";
            String next = i + 1 < tokens.size() ? tokens.get(i + 1) : "";
            String afterNext = i + 2 < tokens.size() ? tokens.get(i + 2) : "";
            switch (token) {
                case "class", "interface", "enum" -> {
                    if (!previous.equals(".") && isIdentifier(next)) {
                        pendingName = next;
                        pendingKind = previous.equals("@") ? "interface" : token;
                    }
                }
                case "record" -> {
                    if (isIdentifier(next) && (afterNext.equals("(") || afterNext.equals("<"))) {
                        pendingName = next;
                        pendingKind = token;
                    }
                }
                case "{" -> {
                    if (pendingName != null) {
                        // A type in a block or an anonymous class is local, and the launcher cannot name it.
                        String[] outer = scopes.peek();
                        scopes.push(outer == BLOCK ? BLOCK
                                : new String[] {outer == null ? prefix + pendingName : outer[0] + "$" + pendingName, pendingKind});
                        pendingName = null;
                    } else {
                        scopes.push(BLOCK);
                    }
                }
                case "}" -> {
                    if (!scopes.isEmpty()) {
                        scopes.pop();
                    }
                }
                case ";" -> pendingName = null;
                case "main" -> {
                    if (previous.equals("void") && next.equals("(")) {
                        String launched = launchedBy(tokens, i, scopes, stem);
                        if (launched != null) {
                            return launched;
                        }
                    }
                }
                default -> {
                }
            }
        }
        return null;
    }

    /** The class that launches the {@code main} declared at {@code at}, or null when the launcher would not run it. */
    private static String launchedBy(List<String> tokens, int at, Deque<String[]> scopes, String stem) {
        String[] type;
        if (scopes.isEmpty()) {
            type = new String[] {stem, "class"};
        } else if (scopes.peek() != BLOCK) {
            type = scopes.peek();
        } else {
            return null;
        }
        // The modifiers, back to the end of the previous member.
        boolean isStatic = false;
        for (int j = at - 2; j >= 0; j--) {
            String modifier = tokens.get(j);
            if (modifier.equals(";") || modifier.equals("{") || modifier.equals("}")) {
                break;
            }
            if (modifier.equals("private") || modifier.equals("abstract")) {
                return null;
            }
            isStatic |= modifier.equals("static");
        }
        if (!isStatic && !type[1].equals("class")) {
            return null;
        }
        StringBuilder parameters = new StringBuilder();
        int depth = 0;
        for (int j = at + 1; j < tokens.size(); j++) {
            String token = tokens.get(j);
            if (token.equals("(")) {
                depth++;
            } else if (token.equals(")") && --depth == 0) {
                break;
            }
            if (j > at + 1) {
                parameters.append(token);
            }
        }
        String parameter = parameters.toString();
        return parameter.isEmpty() || STRING_PARAMETER.matcher(parameter).matches() ? type[0] : null;
    }

    private static boolean isIdentifier(String token) {
        return !token.isEmpty() && Character.isJavaIdentifierStart(token.charAt(0));
    }

    /** Java's tokens, without comments, each string, text block and character literal standing as the token 0. */
    private static List<String> javaTokens(String text) {
        StringBuilder code = new StringBuilder(text.length());
        int i = 0;
        while (i < text.length()) {
            char c = text.charAt(i);
            if (text.startsWith("//", i)) {
                int end = text.indexOf('\n', i);
                i = end < 0 ? text.length() : end;
            } else if (text.startsWith("/*", i)) {
                int end = text.indexOf("*/", i + 2);
                i = end < 0 ? text.length() : end + 2;
                code.append(' ');
            } else if (text.startsWith("\"\"\"", i)) {
                int end = text.indexOf("\"\"\"", i + 3);
                while (end > 0 && text.charAt(end - 1) == '\\') {
                    end = text.indexOf("\"\"\"", end + 1);
                }
                i = end < 0 ? text.length() : end + 3;
                code.append(" 0 ");
            } else if (c == '"' || c == '\'') {
                int j = i + 1;
                while (j < text.length() && text.charAt(j) != c && text.charAt(j) != '\n') {
                    j += text.charAt(j) == '\\' ? 2 : 1;
                }
                i = j + 1;
                code.append(" 0 ");
            } else {
                code.append(c);
                i++;
            }
        }
        List<String> tokens = new ArrayList<>();
        Matcher token = JAVA_TOKEN.matcher(code);
        while (token.find()) {
            tokens.add(token.group());
        }
        return tokens;
    }

    private static <T> List<T> nullToEmpty(List<T> list) {
        return list == null ? List.of() : list;
    }
}
