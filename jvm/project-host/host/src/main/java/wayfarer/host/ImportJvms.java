package wayfarer.host;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Properties;
import java.util.Set;

/** The JDK a build tool runs on during import, recorded as the snapshot's import toolchain. */
final class ImportJvms {
    private ImportJvms() {
    }

    /** Maven runs on the JDK the workspace records, else JAVA_HOME, else the host's own JVM. */
    static Protocol.Jvm forMaven(Protocol.ImportParams params) {
        Protocol.Jvm recorded = usable(params.jdk());
        if (recorded != null) {
            return recorded;
        }
        Protocol.Jvm environment = usable(System.getenv("JAVA_HOME"));
        return environment != null ? environment : Jvms.current();
    }

    /**
     * The JVM the Gradle daemon runs on: the version gradle/gradle-daemon-jvm.properties asks for; else none, where
     * gradle.properties names org.gradle.java.home (Gradle honours it itself); else the JDK the workspace records, else
     * JAVA_HOME, as the wrapper on the command line would take it; else the host's own JVM.
     */
    static Protocol.Jvm forGradle(Path root, Protocol.ImportParams params) {
        Properties criteria = GradleEvaluator.properties(root.resolve("gradle").resolve("gradle-daemon-jvm.properties"));
        String wanted = criteria.getProperty("toolchainVersion");
        if (wanted != null && !wanted.isBlank()) {
            int feature = Jvms.feature(wanted.strip());
            List<Protocol.Jdk> table = new ArrayList<>();
            for (Protocol.Jvm candidate : candidates(params)) {
                if (candidate.feature() != null) {
                    table.add(new Protocol.Jdk(candidate.home(), candidate.home(), candidate.version(), candidate.feature(), candidate.vendor()));
                }
            }
            Protocol.Jdk chosen = choose(new Protocol.Requirement(null, feature, criteria.getProperty("toolchainVendor"), "gradle-daemon"), table);
            if (chosen != null) {
                return usable(chosen.home());
            }
            throw new Problems.Problem("gradle/gradle-daemon-jvm.properties asks for Java " + feature
                    + " to run Gradle, and no JDK " + feature + " was found. Install one, or record it as the project's JDK.");
        }
        if (GradleEvaluator.properties(root.resolve("gradle.properties")).getProperty("org.gradle.java.home") != null) {
            return null;
        }
        Protocol.Jvm recorded = usable(params.jdk());
        if (recorded != null) {
            return recorded;
        }
        Protocol.Jvm environment = usable(System.getenv("JAVA_HOME"));
        return environment != null ? environment : Jvms.current();
    }

    /** A JDK home that has a java to run and a release file that says its version; null otherwise. */
    static Protocol.Jvm usable(String home) {
        if (home == null || home.isBlank()) {
            return null;
        }
        Path path = Path.of(home).toAbsolutePath().normalize();
        String java = MavenEvaluator.isWindows() ? "java.exe" : "java";
        if (!Files.isRegularFile(path.resolve("bin").resolve(java))) {
            return null;
        }
        Protocol.Jvm jvm = Jvms.at(path);
        return jvm.version() == null ? null : jvm;
    }

    /**
     * The JDKs the workspace and environment name, the host's own, then those the IDE's table holds. The host never
     * searches the disk for JDKs: the IDE sends its table with each import.
     */
    static List<Protocol.Jvm> candidates(Protocol.ImportParams params) {
        List<Protocol.Jvm> found = new ArrayList<>();
        add(found, usable(params.jdk()));
        add(found, usable(System.getenv("JAVA_HOME")));
        add(found, usable(System.getProperty("java.home")));
        if (params.jdks() != null) {
            for (Protocol.Jdk jdk : params.jdks()) {
                add(found, usable(jdk.home()));
            }
        }
        return found;
    }

    /** The home of the JDK the IDE's table holds under {@code name} (IntelliJ's names included), while it is usable. */
    static String named(String name, List<Protocol.Jdk> jdks) {
        if (name == null || name.isBlank() || jdks == null) {
            return null;
        }
        for (Protocol.Jdk jdk : jdks) {
            if (name.equalsIgnoreCase(jdk.name()) && usable(jdk.home()) != null) {
                return usable(jdk.home()).home();
            }
        }
        return null;
    }

    /**
     * The JDK a requirement picks from the table: the exact name (any case), else the same feature and distribution, else
     * the same feature from any vendor; the newest update first and table order on a tie; null for none. The app follows
     * the same order (JdkResolution.Choose), and both test suites read the cases in protocol/jdk-resolution.json.
     */
    static Protocol.Jdk choose(Protocol.Requirement requirement, List<Protocol.Jdk> jdks) {
        if (requirement == null || jdks == null) {
            return null;
        }
        if (requirement.name() != null && !requirement.name().isBlank()) {
            for (Protocol.Jdk jdk : jdks) {
                if (requirement.name().equalsIgnoreCase(jdk.name())) {
                    return jdk;
                }
            }
        }
        if (requirement.feature() == null) {
            return null;
        }
        List<Protocol.Jdk> sameFeature = new ArrayList<>();
        for (Protocol.Jdk jdk : jdks) {
            if (requirement.feature().equals(jdk.feature())) {
                sameFeature.add(jdk);
            }
        }
        if (requirement.vendor() != null && !requirement.vendor().isBlank()) {
            String wanted = distribution(requirement.vendor());
            List<Protocol.Jdk> ofVendor = new ArrayList<>();
            for (Protocol.Jdk jdk : sameFeature) {
                if (wanted.equals(distribution(jdk.vendor()))) {
                    ofVendor.add(jdk);
                }
            }
            Protocol.Jdk newest = newest(ofVendor);
            if (newest != null) {
                return newest;
            }
        }
        return newest(sameFeature);
    }

    /**
     * The JDK a module ends up with. Its own requirement (the build's declaration) takes a home from the table when the
     * build found none itself; without one, the project's requirement is the module's, and a project requirement no JDK
     * meets leaves the module without a home. A null home with a requirement is what the app shows as a missing JDK.
     */
    static Protocol.JvmEnvironment settle(Protocol.JvmEnvironment environment, Protocol.ImportParams params) {
        if (environment == null) {
            return null;
        }
        if (environment.required() != null) {
            if (environment.javaHome() != null) {
                return environment;
            }
            Protocol.Jdk chosen = choose(environment.required(), usableJdks(params));
            if (chosen == null) {
                return environment;
            }
            String vendor = environment.vendor() != null ? environment.vendor() : chosen.vendor();
            return new Protocol.JvmEnvironment(usable(chosen.home()).home(), environment.javaVersion(), vendor, environment.required());
        }
        if (params.jdkRequired() == null) {
            return environment;
        }
        String home = usable(params.jdk()) != null ? environment.javaHome() : null;
        return new Protocol.JvmEnvironment(home, environment.javaVersion(), home == null ? null : environment.vendor(), params.jdkRequired());
    }

    /** Every module of {@code result} settled against the table its import was sent ({@link #settle}). */
    static Protocol.WorkspaceResult settle(Protocol.WorkspaceResult result, Protocol.ImportParams params) {
        List<Protocol.BuildTarget> targets = new ArrayList<>();
        for (Protocol.BuildTarget t : result.targets()) {
            targets.add(new Protocol.BuildTarget(t.id(), t.displayName(), t.baseDirectory(), t.languageIds(),
                    settle(t.jvmBuildTarget(), params), settle(t.runEnvironment(), params), t.sourceSets(), t.tasks(),
                    t.mainClasses(), t.activeProfiles(), t.absentLibraries(), t.problem()));
        }
        return new Protocol.WorkspaceResult(result.generation(), result.root(), result.buildSystem(), result.buildFile(),
                result.importJvm(), targets, result.buildInputs(), result.outputRoot());
    }

    /** The table's JDKs whose homes still hold a JDK. */
    static List<Protocol.Jdk> usableJdks(Protocol.ImportParams params) {
        List<Protocol.Jdk> found = new ArrayList<>();
        if (params.jdks() != null) {
            for (Protocol.Jdk jdk : params.jdks()) {
                if (usable(jdk.home()) != null) {
                    found.add(jdk);
                }
            }
        }
        return found;
    }

    private static final Set<String> DISTRIBUTIONS = Set.of("temurin", "adopt", "corretto", "zulu", "liberica", "ms", "jbr",
            "graalvm", "semeru", "sapmachine", "dragonwell", "kona", "openjdk", "jdk");

    /** The short name of a distribution from the IMPLEMENTOR a release file names, as the app's JdkRegistry.Distribution. */
    static String distribution(String vendor) {
        String v = vendor == null ? "" : vendor.strip().toLowerCase(Locale.ROOT);
        if (DISTRIBUTIONS.contains(v)) {
            return v;
        }
        if (v.contains("adoptium") || v.contains("temurin")) {
            return "temurin";
        }
        if (v.contains("adoptopenjdk")) {
            return "adopt";
        }
        if (v.contains("amazon")) {
            return "corretto";
        }
        if (v.contains("azul")) {
            return "zulu";
        }
        if (v.contains("bellsoft")) {
            return "liberica";
        }
        if (v.contains("microsoft")) {
            return "ms";
        }
        if (v.contains("jetbrains")) {
            return "jbr";
        }
        if (v.contains("graalvm")) {
            return "graalvm";
        }
        if (v.contains("international business machines") || v.startsWith("ibm")) {
            return "semeru";
        }
        if (v.contains("sap")) {
            return "sapmachine";
        }
        if (v.contains("alibaba")) {
            return "dragonwell";
        }
        if (v.contains("tencent")) {
            return "kona";
        }
        if (v.contains("oracle")) {
            return "openjdk";
        }
        return "jdk";
    }

    /** Compares version strings by their numbers in order: 21.0.13 after 21.0.5, 11 before 11.0.25. */
    static int compareVersions(String left, String right) {
        List<Long> a = numbers(left);
        List<Long> b = numbers(right);
        for (int i = 0; i < Math.max(a.size(), b.size()); i++) {
            long x = i < a.size() ? a.get(i) : -1;
            long y = i < b.size() ? b.get(i) : -1;
            if (x != y) {
                return Long.compare(x, y);
            }
        }
        return 0;
    }

    private static Protocol.Jdk newest(List<Protocol.Jdk> jdks) {
        Protocol.Jdk best = null;
        for (Protocol.Jdk jdk : jdks) {
            if (best == null || compareVersions(jdk.version(), best.version()) > 0) {
                best = jdk;
            }
        }
        return best;
    }

    private static List<Long> numbers(String version) {
        List<Long> numbers = new ArrayList<>();
        Long current = null;
        for (char c : (version == null ? "" : version).toCharArray()) {
            if (c >= '0' && c <= '9') {
                current = Math.min((current == null ? 0 : current) * 10 + (c - '0'), Long.MAX_VALUE / 10);
            } else if (current != null) {
                numbers.add(current);
                current = null;
            }
        }
        if (current != null) {
            numbers.add(current);
        }
        return numbers;
    }

    private static void add(List<Protocol.Jvm> found, Protocol.Jvm jvm) {
        if (jvm != null && found.stream().noneMatch(known -> known.home().equalsIgnoreCase(jvm.home()))) {
            found.add(jvm);
        }
    }
}
