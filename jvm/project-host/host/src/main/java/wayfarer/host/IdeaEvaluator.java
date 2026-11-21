package wayfarer.host;

import java.io.IOException;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.nio.file.DirectoryStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.stream.Stream;
import javax.xml.XMLConstants;
import javax.xml.parsers.DocumentBuilder;
import javax.xml.parsers.DocumentBuilderFactory;
import javax.xml.parsers.ParserConfigurationException;
import org.w3c.dom.Document;
import org.w3c.dom.Element;
import org.w3c.dom.Node;
import org.w3c.dom.NodeList;
import org.xml.sax.SAXException;

/**
 * Reads an IntelliJ project without a build tool, as IntelliJ keeps one: the modules {@code .idea/modules.xml} lists
 * (or the folder's one {@code .iml}), each module's source folders and its order entries in order (the JDK, other
 * modules with their exported entries, project, global and module libraries, each with its scope), the project's
 * {@code .idea/misc.xml} (output folder, language level, project JDK) and {@code .idea/libraries}. Paths go through
 * IntelliJ's macros ({@code $PROJECT_DIR$}, {@code $MODULE_DIR$}, {@code $USER_HOME$}, {@code $MAVEN_REPOSITORY$} and
 * the path variables the import was sent); one no variable answers is a module problem naming it. A repository
 * library whose jars are not on disk is downloaded with Maven Resolver, and Kotlin's runtime library, whose jars
 * IntelliJ's own plugin carries, is resolved at the project's Kotlin version. Jars still missing are reported absent.
 */
final class IdeaEvaluator {
    private static final Pattern MACRO = Pattern.compile("\\$([A-Za-z0-9_.-]+)\\$");
    private volatile RepositoryLibraries repositories;

    static boolean owns(Path folder) {
        return moduleFile(folder) != null || !listedModules(folder).isEmpty();
    }

    /** Where repository libraries are resolved from; the person's local repository and Maven Central unless set. */
    void repositories(RepositoryLibraries libraries) {
        repositories = libraries;
    }

    Protocol.WorkspaceResult evaluate(Path root, Protocol.ImportParams params) {
        List<Path> imls = listedModules(root);
        if (imls.isEmpty()) {
            Path single = moduleFile(root);
            if (single == null) {
                throw new Problems.Problem(root + " has no single IntelliJ module file. An IntelliJ module is a folder that contains its .iml file.");
            }
            imls = List.of(single);
        }
        Macros macros = new Macros(root, params.pathVariables());
        Element project = projectRoot(root);
        List<Path> inputs = new ArrayList<>(List.of(root.resolve(".idea").resolve("misc.xml"), root.resolve(".idea").resolve("modules.xml"),
                root.resolve(".idea").resolve("kotlinc.xml")));
        inputs.addAll(imls);
        Path librariesFolder = root.resolve(".idea").resolve("libraries");
        if (Files.isDirectory(librariesFolder)) {
            inputs.add(librariesFolder);
        }
        Map<String, Protocol.Library> projectLibraries = projectLibraries(librariesFolder);
        Map<String, Protocol.Library> globalLibraries = new LinkedHashMap<>();
        if (params.globalLibraries() != null) {
            for (Protocol.Library library : params.globalLibraries()) {
                if (library != null && library.name() != null) {
                    globalLibraries.putIfAbsent(library.name(), library);
                }
            }
        }
        String kotlinVersion = kotlinVersion(root);

        Map<String, Module> modules = new LinkedHashMap<>();
        for (Path iml : imls) {
            Module module = readModule(iml, root, project, params, macros);
            modules.putIfAbsent(module.name, module);
        }
        for (Module module : modules.values()) {
            for (Entry entry : module.entries) {
                if (entry.library != null) {
                    entry.coordinates = entry.library.mavenId();
                    entry.jars = libraryJars(entry.library, module, root, macros, kotlinVersion);
                } else if (entry.libraryName != null) {
                    Protocol.Library library = "application".equals(entry.level) ? globalLibraries.get(entry.libraryName)
                            : projectLibraries.get(entry.libraryName);
                    if (library == null) {
                        module.absent.add(entry.libraryName);
                        entry.jars = List.of();
                    } else {
                        entry.coordinates = library.mavenId();
                        entry.jars = libraryJars(library, module, root, macros, kotlinVersion);
                    }
                } else if (entry.moduleName != null && !modules.containsKey(entry.moduleName)) {
                    module.problems.add("module " + module.name + " depends on module " + entry.moduleName + ", which the project does not list");
                }
            }
        }

        List<Protocol.BuildTarget> targets = new ArrayList<>();
        String outputRoot = null;
        Protocol.Jvm importJvm = null;
        for (Module module : modules.values()) {
            outputRoot = outputRoot == null ? module.outputRoot : outputRoot;
            List<Protocol.DependencyModule> mainCompile = new ArrayList<>();
            List<Protocol.DependencyModule> mainRuntime = new ArrayList<>();
            List<Protocol.DependencyModule> testCompile = new ArrayList<>();
            List<Protocol.DependencyModule> testRuntime = new ArrayList<>();
            mainRuntime.add(new Protocol.DependencyModule(module.classes, null, module.name + ":main"));
            testCompile.add(new Protocol.DependencyModule(module.classes, null, module.name + ":main"));
            testRuntime.add(new Protocol.DependencyModule(module.testClasses, null, module.name + ":test"));
            testRuntime.add(new Protocol.DependencyModule(module.classes, null, module.name + ":main"));
            for (Entry entry : module.entries) {
                List<Protocol.DependencyModule> compile = entry.moduleName != null ? compileClosure(modules, entry.moduleName, new HashSet<>())
                        : jarEntries(entry);
                List<Protocol.DependencyModule> runtime = entry.moduleName != null ? runtimeClosure(modules, entry.moduleName, new HashSet<>())
                        : jarEntries(entry);
                switch (entry.scope) {
                    case "TEST" -> {
                        addAll(testCompile, compile);
                        addAll(testRuntime, runtime);
                    }
                    case "RUNTIME" -> {
                        addAll(mainRuntime, runtime);
                        addAll(testRuntime, runtime);
                    }
                    case "PROVIDED" -> {
                        addAll(mainCompile, compile);
                        addAll(testCompile, compile);
                        addAll(testRuntime, runtime);
                    }
                    default -> {
                        addAll(mainCompile, compile);
                        addAll(mainRuntime, runtime);
                        addAll(testCompile, compile);
                        addAll(testRuntime, runtime);
                    }
                }
            }
            List<Protocol.SourceSet> sets = new ArrayList<>();
            Map<String, List<Protocol.DependencyModule>> mainModules = new LinkedHashMap<>();
            mainModules.put("compile", mainCompile);
            mainModules.put("runtime", mainRuntime);
            sets.add(new Protocol.SourceSet(module.name + ":main", "main", false, module.main, List.of(module.classes), mainModules));
            if (!module.test.isEmpty()) {
                Map<String, List<Protocol.DependencyModule>> testModules = new LinkedHashMap<>();
                testModules.put("compile", testCompile);
                testModules.put("runtime", testRuntime);
                sets.add(new Protocol.SourceSet(module.name + ":test", "test", true, module.test, List.of(module.testClasses), testModules));
            }
            List<String> languages = module.kotlin ? List.of("java", "kotlin") : List.of("java");
            String problem = module.problems.isEmpty() ? null : String.join("; ", module.problems);
            targets.add(MainScan.withDiscovered(new Protocol.BuildTarget(module.name, module.name, module.dir.toString(), languages, module.jvm, module.jvm,
                    sets, List.of("classes"), List.of(), List.of(), module.absent, problem)));
            if (importJvm == null) {
                importJvm = new Protocol.Jvm(module.home, module.level == null ? null : String.valueOf(module.level), module.level, module.jdkName);
            }
        }
        List<String> stamped = new ArrayList<>();
        for (Path input : inputs) {
            if (Files.isDirectory(input)) {
                try (Stream<Path> files = Files.list(input)) {
                    files.filter(file -> file.toString().endsWith(".xml")).sorted().forEach(file -> stamped.add(file.toString()));
                } catch (IOException unreadable) {
                    // Nothing more to stamp there.
                }
            } else {
                stamped.add(input.toString());
            }
        }
        Path buildFile = imls.size() == 1 && listedModules(root).isEmpty() ? imls.get(0) : root.resolve(".idea").resolve("modules.xml");
        return new Protocol.WorkspaceResult(params.generation(), root.toString(), "idea", buildFile.toString(), importJvm, targets,
                BuildInputs.stamp(stamped), outputRoot);
    }

    /** One module as its .iml says: where it lives and compiles to, its sources, JDK and order entries. */
    private static final class Module {
        String name;
        Path dir;
        String outputRoot;
        String classes;
        String testClasses;
        final List<Protocol.SourceItem> main = new ArrayList<>();
        final List<Protocol.SourceItem> test = new ArrayList<>();
        final List<Entry> entries = new ArrayList<>();
        final List<String> absent = new ArrayList<>();
        final List<String> problems = new ArrayList<>();
        Protocol.JvmEnvironment jvm;
        String home;
        Integer level;
        String jdkName;
        boolean kotlin;
    }

    /** An order entry: a library (named at a level, or inline) or another module, with its scope and export. */
    private static final class Entry {
        String scope = "COMPILE";
        boolean exported;
        String moduleName;
        String libraryName;
        String level;
        Protocol.Library library;
        String coordinates;
        List<String> jars = List.of();
    }

    private Module readModule(Path iml, Path root, Element project, Protocol.ImportParams params, Macros macros) {
        Element element = load(iml).getDocumentElement();
        if (!"JAVA_MODULE".equals(element.getAttribute("type"))) {
            throw new Problems.Problem(iml + " is not a Java module.");
        }
        Element manager = component(element, "NewModuleRootManager");
        if (manager == null) {
            throw new Problems.Problem(iml + " has no module roots.");
        }
        Module module = new Module();
        module.name = stem(iml.getFileName().toString());
        module.dir = iml.getParent().toAbsolutePath().normalize();
        Path dir = module.dir;
        boolean inherit = !"false".equalsIgnoreCase(manager.getAttribute("inherit-compiler-output"));
        String projectOutputUrl = childAttribute(project, "output", "url");
        String projectOutput = projectOutputUrl != null ? macros.expand(projectOutputUrl, dir, module.problems) : null;
        String moduleOutputUrl = childAttribute(manager, "output", "url");
        module.outputRoot = inherit
                ? (projectOutput != null ? projectOutput : root.resolve("out").toString())
                : moduleOutputUrl != null ? macros.expand(moduleOutputUrl, dir, module.problems)
                : projectOutput != null ? projectOutput : root.resolve("out").toString();
        boolean ownOutput = !inherit && child(manager, "output") != null;
        module.classes = ownOutput ? module.outputRoot : Path.of(module.outputRoot, "production", module.name).toString();
        module.testClasses = ownOutput
                ? (childAttribute(manager, "output-test", "url") != null ? macros.expand(childAttribute(manager, "output-test", "url"), dir, module.problems) : module.outputRoot)
                : Path.of(module.outputRoot, "test", module.name).toString();

        NodeList folders = manager.getElementsByTagName("sourceFolder");
        for (int i = 0; i < folders.getLength(); i++) {
            Element folder = (Element) folders.item(i);
            String url = folder.getAttribute("url");
            if (url.isBlank()) {
                continue;
            }
            String type = folder.getAttribute("type");
            String kind = type.contains("resource") ? "resources" : "java";
            boolean isTest = "true".equalsIgnoreCase(folder.getAttribute("isTestSource")) || type.equals("java-test-resource");
            boolean generated = "true".equalsIgnoreCase(folder.getAttribute("generated"));
            (isTest ? module.test : module.main).add(new Protocol.SourceItem(macros.expand(url, dir, module.problems), kind, generated));
        }
        Element facets = component(element, "FacetManager");
        if (facets != null) {
            for (Element facet : children(facets, "facet")) {
                if (facet.getAttribute("type").startsWith("kotlin")) {
                    module.kotlin = true;
                }
            }
        }

        for (Element order : children(manager, "orderEntry")) {
            String type = order.getAttribute("type");
            Entry entry = new Entry();
            entry.scope = order.getAttribute("scope").isEmpty() ? "COMPILE" : order.getAttribute("scope").toUpperCase(Locale.ROOT);
            entry.exported = order.hasAttribute("exported");
            switch (type) {
                case "module" -> entry.moduleName = order.getAttribute("module-name");
                case "library" -> {
                    entry.libraryName = order.getAttribute("name");
                    entry.level = order.getAttribute("level").isEmpty() ? "project" : order.getAttribute("level");
                    if (entry.libraryName.toLowerCase(Locale.ROOT).contains("kotlin")) {
                        module.kotlin = true;
                    }
                }
                case "module-library" -> {
                    Element inline = child(order, "library");
                    if (inline == null) {
                        continue;
                    }
                    entry.library = library(inline);
                }
                default -> {
                    continue;
                }
            }
            module.entries.add(entry);
        }

        String jdkName = null;
        for (Element order : children(manager, "orderEntry")) {
            if ("jdk".equals(order.getAttribute("type")) && !order.getAttribute("jdkName").isEmpty()) {
                jdkName = order.getAttribute("jdkName");
                break;
            }
        }
        if (jdkName == null && project != null && !project.getAttribute("project-jdk-name").isEmpty()) {
            jdkName = project.getAttribute("project-jdk-name");
        }
        // A module's own LANGUAGE_LEVEL overrides the project's, as in IntelliJ's module settings.
        Integer level = languageLevel(manager.getAttribute("LANGUAGE_LEVEL"));
        if (level == null) {
            level = languageLevel(project == null ? null : project.getAttribute("languageLevel"));
        }
        // The module's own JDK name is its requirement: the JDK of that name in the IDE's table (which holds IntelliJ's
        // JDK table under IntelliJ's names), else one of the release the name carries (see requirement). The host
        // settles it against the table; none meeting it leaves the module without a JDK. A module that names none takes
        // the JDK the workspace resolved to in Wayfarer, then JAVA_HOME; the import JVM falls back the same way.
        Protocol.Requirement required = requirement(jdkName, level);
        Protocol.Jdk named = ImportJvms.choose(required, ImportJvms.usableJdks(params));
        String home = named != null ? ImportJvms.usable(named.home()).home()
                : ImportJvms.usable(params.jdk()) != null ? params.jdk() : null;
        if (home == null) {
            String javaHome = System.getenv("JAVA_HOME");
            home = javaHome == null || javaHome.isBlank() ? null : Path.of(javaHome).toAbsolutePath().normalize().toString();
        }
        module.home = home;
        module.level = level;
        module.jdkName = jdkName;
        module.jvm = new Protocol.JvmEnvironment(required == null ? home : null, level, jdkName, required);
        return module;
    }

    /** The jars of a library for a module: its class roots expanded (a jar directory's jars listed), downloaded for a
     * repository library whose jars are missing, and Kotlin's bundled runtime resolved from Maven Central. */
    private List<String> libraryJars(Protocol.Library library, Module module, Path root, Macros macros, String kotlinVersion) {
        String label = library.name() == null || library.name().isBlank() ? "(unnamed library)" : library.name();
        List<String> jars = new ArrayList<>();
        List<String> kotlinArtifacts = new ArrayList<>();
        boolean missing = false;
        List<String> unset = new ArrayList<>();
        for (String url : library.classes() == null ? List.<String>of() : library.classes()) {
            Matcher bundled = KOTLIN_BUNDLED.matcher(url);
            if (bundled.find() && macros.value("KOTLIN_BUNDLED") == null) {
                if (!bundled.group(1).endsWith("-sources")) {
                    kotlinArtifacts.add(bundled.group(1));
                }
                continue;
            }
            List<String> problems = new ArrayList<>();
            String path = macros.expand(url, module.dir, problems);
            if (!problems.isEmpty()) {
                unset.addAll(problems);
                continue;
            }
            Path file = Path.of(path);
            if (Files.isDirectory(file) && url.startsWith("file://") && !url.contains("!/")) {
                // A jar directory, or a folder of classes.
                try (Stream<Path> listed = Files.list(file)) {
                    List<Path> inside = listed.filter(each -> each.toString().endsWith(".jar")).sorted().toList();
                    if (inside.isEmpty()) {
                        jars.add(path);
                    } else {
                        inside.forEach(each -> jars.add(each.toString()));
                    }
                } catch (IOException unreadable) {
                    jars.add(path);
                }
            } else {
                jars.add(path);
                missing |= !Files.exists(file);
            }
        }
        if (library.mavenId() != null && !library.mavenId().isBlank() && (missing || jars.isEmpty() || !unset.isEmpty())) {
            try {
                List<String> downloaded = repositories(macros).resolve(library.mavenId(), !Boolean.FALSE.equals(library.transitive()));
                return downloaded;
            } catch (Problems.Problem failed) {
                module.problems.add(failed.getMessage());
                module.absent.add(label);
                return List.of();
            }
        }
        for (String problem : unset) {
            module.problems.add(problem + " (used by library " + label + ")");
        }
        if (!kotlinArtifacts.isEmpty()) {
            if (kotlinVersion == null) {
                module.problems.add("library " + label + " points into IntelliJ's Kotlin plugin, and .idea/kotlinc.xml names no Kotlin version to download it at");
                module.absent.add(label);
            } else {
                for (String artifact : kotlinArtifacts) {
                    try {
                        jars.addAll(repositories(macros).resolve("org.jetbrains.kotlin:" + artifact + ":" + kotlinVersion, true));
                    } catch (Problems.Problem failed) {
                        module.problems.add(failed.getMessage());
                        module.absent.add(label);
                    }
                }
            }
            module.kotlin = true;
        }
        if (jars.isEmpty() && kotlinArtifacts.isEmpty() && unset.isEmpty()) {
            module.absent.add(label);
        }
        List<String> present = new ArrayList<>();
        for (String jar : jars) {
            if (Files.exists(Path.of(jar))) {
                if (!present.contains(jar)) {
                    present.add(jar);
                }
            } else {
                module.absent.add(jar);
            }
        }
        return present;
    }

    private static final Pattern KOTLIN_BUNDLED = Pattern.compile("\\$KOTLIN_BUNDLED\\$/lib/([^/!]+)\\.jar");

    private RepositoryLibraries repositories(Macros macros) {
        RepositoryLibraries set = repositories;
        Path local = macros.value("MAVEN_REPOSITORY") != null ? Path.of(macros.value("MAVEN_REPOSITORY")) : RepositoryLibraries.defaultLocal();
        if (set == null || !set.local().equals(local)) {
            set = RepositoryLibraries.forThisMachine(local);
            repositories = set;
        }
        return set;
    }

    private static List<Protocol.DependencyModule> jarEntries(Entry entry) {
        List<Protocol.DependencyModule> found = new ArrayList<>();
        for (int i = 0; i < entry.jars.size(); i++) {
            // The library's own jar comes first; the jars it brought with it are its dependencies, not it.
            found.add(new Protocol.DependencyModule(entry.jars.get(i), i == 0 ? entry.coordinates : null, null));
        }
        return found;
    }

    /** What a module gives a module that depends on it at compile time: its classes and its exported entries, theirs
     * in turn. */
    private static List<Protocol.DependencyModule> compileClosure(Map<String, Module> modules, String name, Set<String> seen) {
        List<Protocol.DependencyModule> found = new ArrayList<>();
        Module module = modules.get(name);
        if (module == null || !seen.add(name)) {
            return found;
        }
        found.add(new Protocol.DependencyModule(module.classes, null, name + ":main"));
        for (Entry entry : module.entries) {
            if (!entry.exported || entry.scope.equals("TEST") || entry.scope.equals("RUNTIME")) {
                continue;
            }
            addAll(found, entry.moduleName != null ? compileClosure(modules, entry.moduleName, seen) : jarEntries(entry));
        }
        return found;
    }

    /** What a module needs at run time: its classes and every compile and runtime entry, other modules' in turn. */
    private static List<Protocol.DependencyModule> runtimeClosure(Map<String, Module> modules, String name, Set<String> seen) {
        List<Protocol.DependencyModule> found = new ArrayList<>();
        Module module = modules.get(name);
        if (module == null || !seen.add(name)) {
            return found;
        }
        found.add(new Protocol.DependencyModule(module.classes, null, name + ":main"));
        for (Entry entry : module.entries) {
            if (entry.scope.equals("TEST") || entry.scope.equals("PROVIDED")) {
                continue;
            }
            addAll(found, entry.moduleName != null ? runtimeClosure(modules, entry.moduleName, seen) : jarEntries(entry));
        }
        return found;
    }

    private static void addAll(List<Protocol.DependencyModule> into, List<Protocol.DependencyModule> more) {
        for (Protocol.DependencyModule each : more) {
            if (into.stream().noneMatch(known -> known.path().equals(each.path()))) {
                into.add(each);
            }
        }
    }

    /** The modules .idea/modules.xml lists that exist; none when it lists none or is absent. */
    static List<Path> listedModules(Path root) {
        Path file = root.resolve(".idea").resolve("modules.xml");
        List<Path> found = new ArrayList<>();
        if (!Files.isRegularFile(file)) {
            return found;
        }
        Macros macros = new Macros(root, null);
        NodeList listed = load(file).getElementsByTagName("module");
        for (int i = 0; i < listed.getLength(); i++) {
            Element module = (Element) listed.item(i);
            String path = !module.getAttribute("filepath").isEmpty() ? module.getAttribute("filepath") : module.getAttribute("fileurl");
            if (path.isEmpty()) {
                continue;
            }
            Path iml = Path.of(macros.expand(path, root, new ArrayList<>()));
            if (Files.isRegularFile(iml) && !found.contains(iml)) {
                found.add(iml);
            }
        }
        return found;
    }

    private static Map<String, Protocol.Library> projectLibraries(Path folder) {
        Map<String, Protocol.Library> found = new LinkedHashMap<>();
        if (!Files.isDirectory(folder)) {
            return found;
        }
        try (DirectoryStream<Path> files = Files.newDirectoryStream(folder, "*.xml")) {
            List<Path> sorted = new ArrayList<>();
            files.forEach(sorted::add);
            sorted.sort(Comparator.naturalOrder());
            for (Path file : sorted) {
                NodeList libraries = load(file).getElementsByTagName("library");
                for (int i = 0; i < libraries.getLength(); i++) {
                    Protocol.Library library = library((Element) libraries.item(i));
                    if (library.name() != null && !library.name().isEmpty()) {
                        found.putIfAbsent(library.name(), library);
                    }
                }
            }
        } catch (IOException unreadable) {
            return found;
        }
        return found;
    }

    /** A {@code <library>} element: its name, CLASSES roots and, for a repository library, its maven-id. */
    static Protocol.Library library(Element element) {
        List<String> classes = new ArrayList<>();
        Element roots = child(element, "CLASSES");
        if (roots != null) {
            for (Element root : children(roots, "root")) {
                if (!root.getAttribute("url").isEmpty()) {
                    classes.add(root.getAttribute("url"));
                }
            }
            // A jar directory: <jarDirectory url="file://..." recursive="false" /> beside the roots.
        }
        for (Element directory : children(element, "jarDirectory")) {
            if (!directory.getAttribute("url").isEmpty()) {
                classes.add(directory.getAttribute("url"));
            }
        }
        String mavenId = null;
        Boolean transitive = null;
        Element properties = child(element, "properties");
        if ("repository".equals(element.getAttribute("type")) && properties != null && !properties.getAttribute("maven-id").isEmpty()) {
            mavenId = properties.getAttribute("maven-id");
            transitive = !"false".equalsIgnoreCase(properties.getAttribute("include-transitive-deps"));
        }
        return new Protocol.Library(element.getAttribute("name"), classes, mavenId, transitive);
    }

    /** The Kotlin version IntelliJ's JPS plugin settings name for the project; null for none. */
    static String kotlinVersion(Path root) {
        Path file = root.resolve(".idea").resolve("kotlinc.xml");
        if (!Files.isRegularFile(file)) {
            return null;
        }
        NodeList options = load(file).getElementsByTagName("option");
        for (int i = 0; i < options.getLength(); i++) {
            Element option = (Element) options.item(i);
            if ("version".equals(option.getAttribute("name")) && !option.getAttribute("value").isBlank()) {
                return option.getAttribute("value").strip();
            }
        }
        return null;
    }

    /**
     * IntelliJ's macros: {@code $PROJECT_DIR$}, {@code $MODULE_DIR$}, {@code $USER_HOME$}, {@code $MAVEN_REPOSITORY$}
     * and the path variables the import was sent. A macro none answers is reported, never left as an empty root.
     */
    static final class Macros {
        private final Path project;
        private final Map<String, String> variables = new LinkedHashMap<>();

        Macros(Path project, Map<String, String> pathVariables) {
            this.project = project;
            if (pathVariables != null) {
                pathVariables.forEach((name, value) -> {
                    if (name != null && value != null && !value.isBlank()) {
                        variables.putIfAbsent(name, value);
                    }
                });
            }
        }

        /** A variable's value: one sent, else the built-in ones; null for none. */
        String value(String name) {
            String sent = variables.get(name);
            if (sent != null) {
                return sent;
            }
            return switch (name) {
                case "USER_HOME" -> System.getProperty("user.home");
                case "MAVEN_REPOSITORY" -> RepositoryLibraries.defaultLocal().toString();
                default -> null;
            };
        }

        String expand(String url, Path moduleDir, List<String> problems) {
            String path = url.strip();
            if (path.regionMatches(true, 0, "jar://", 0, 6)) {
                path = path.substring(6);
            } else if (path.regionMatches(true, 0, "file://", 0, 7)) {
                path = path.substring(7);
            }
            int bang = path.indexOf("!/");
            if (bang >= 0) {
                path = path.substring(0, bang);
            }
            path = path.replace("$MODULE_DIR$", moduleDir.toString()).replace("$PROJECT_DIR$", project.toString());
            Matcher macro = MACRO.matcher(path);
            StringBuilder expanded = new StringBuilder();
            while (macro.find()) {
                String value = value(macro.group(1));
                if (value == null) {
                    problems.add("path variable " + macro.group(1) + " is not set");
                    value = macro.group();
                }
                macro.appendReplacement(expanded, Matcher.quoteReplacement(value));
            }
            macro.appendTail(expanded);
            path = URLDecoder.decode(expanded.toString().replace("+", "%2B"), StandardCharsets.UTF_8);
            if (MavenEvaluator.isWindows() && path.length() >= 3 && path.charAt(0) == '/' && path.charAt(2) == ':') {
                path = path.substring(1);
            }
            Path result;
            try {
                result = Path.of(path);
            } catch (java.nio.file.InvalidPathException unreadable) {
                problems.add(url + " is not a path");
                return path;
            }
            if (!result.isAbsolute()) {
                result = project.resolve(path);
            }
            return result.toAbsolutePath().normalize().toString();
        }
    }

    /** The folder's one .iml file; null for none or several. */
    static Path moduleFile(Path folder) {
        if (!Files.isDirectory(folder)) {
            return null;
        }
        List<Path> files = new ArrayList<>();
        try (DirectoryStream<Path> stream = Files.newDirectoryStream(folder, "*.iml")) {
            stream.forEach(files::add);
        } catch (IOException unreadable) {
            return null;
        }
        return files.size() == 1 ? files.get(0) : null;
    }

    /** Folders under {@code folder} that are IntelliJ modules, for a message naming which one to open. */
    static List<String> moduleFolders(Path folder) {
        List<String> found = new ArrayList<>();
        try (DirectoryStream<Path> stream = Files.newDirectoryStream(folder, Files::isDirectory)) {
            for (Path child : stream) {
                if (moduleFile(child) != null) {
                    found.add(child.getFileName().toString());
                }
            }
        } catch (IOException unreadable) {
            // None to name.
        }
        found.sort(Comparator.naturalOrder());
        return found;
    }

    static String expand(String url, Path moduleDir, Path projectDir) {
        return new Macros(projectDir, null).expand(url, moduleDir, new ArrayList<>());
    }

    private static Element projectRoot(Path root) {
        Path misc = root.resolve(".idea").resolve("misc.xml");
        if (!Files.isRegularFile(misc)) {
            return null;
        }
        NodeList components = load(misc).getElementsByTagName("component");
        for (int i = 0; i < components.getLength(); i++) {
            Element component = (Element) components.item(i);
            if ("ProjectRootManager".equals(component.getAttribute("name"))) {
                return component;
            }
        }
        return null;
    }

    /**
     * What an IntelliJ JDK name asks for. IntelliJ names the JDKs it sets up by their release (corretto-21, 21,
     * temurin-21.0.5, jdk1.8.0_202), and that release is the feature a JDK of another name must have to stand in. The
     * language level is only the least the module compiles with (a JDK 21 compiles JDK_17 code), so it is the feature
     * only when the name carries no release.
     */
    static Protocol.Requirement requirement(String jdkName, Integer level) {
        if (jdkName == null || jdkName.isBlank()) {
            return null;
        }
        Integer feature = featureOfName(jdkName);
        return new Protocol.Requirement(jdkName, feature != null ? feature : level, null, "idea");
    }

    // A release stands on its own in the name, or right after "jdk": not inside a word or a hash (my-jdk-v2, wanted-19ab).
    private static final java.util.regex.Pattern RELEASE = java.util.regex.Pattern.compile(
            "(?<![a-z0-9.])(?:jdk)?(\\d+)(?:\\.(\\d+))?(?![a-z0-9])");

    private static Integer featureOfName(String name) {
        java.util.regex.Matcher release = RELEASE.matcher(name.toLowerCase(java.util.Locale.ROOT));
        if (!release.find()) {
            return null;
        }
        try {
            int first = Integer.parseInt(release.group(1));
            int feature = first == 1 && release.group(2) != null ? Integer.parseInt(release.group(2)) : first;
            return feature >= 1 && feature < 1000 ? feature : null;
        } catch (NumberFormatException tooLong) {
            return null;
        }
    }

    static Integer languageLevel(String value) {
        if (value == null || !value.startsWith("JDK_")) {
            return null;
        }
        String rest = value.substring(4);
        if (rest.startsWith("1_")) {
            rest = rest.substring(2);
        }
        try {
            return Integer.parseInt(rest);
        } catch (NumberFormatException unknown) {
            return null;
        }
    }

    private static Element component(Element parent, String name) {
        for (Element component : children(parent, "component")) {
            if (name.equals(component.getAttribute("name"))) {
                return component;
            }
        }
        return null;
    }

    private static String childAttribute(Element parent, String child, String attribute) {
        Element element = parent == null ? null : child(parent, child);
        return element == null || element.getAttribute(attribute).isEmpty() ? null : element.getAttribute(attribute);
    }

    private static Element child(Element parent, String name) {
        List<Element> found = children(parent, name);
        return found.isEmpty() ? null : found.get(0);
    }

    static List<Element> children(Element parent, String name) {
        List<Element> found = new ArrayList<>();
        for (Node node = parent.getFirstChild(); node != null; node = node.getNextSibling()) {
            if (node instanceof Element element && element.getTagName().equals(name)) {
                found.add(element);
            }
        }
        return found;
    }

    private static String stem(String fileName) {
        int dot = fileName.lastIndexOf('.');
        return dot < 0 ? fileName : fileName.substring(0, dot);
    }

    /** An IntelliJ XML file, read with DTDs and external entities refused. */
    static Document load(Path file) {
        try {
            DocumentBuilderFactory factory = DocumentBuilderFactory.newInstance();
            factory.setFeature(XMLConstants.FEATURE_SECURE_PROCESSING, true);
            factory.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true);
            factory.setXIncludeAware(false);
            factory.setExpandEntityReferences(false);
            DocumentBuilder builder = factory.newDocumentBuilder();
            builder.setErrorHandler(null);
            return builder.parse(file.toFile());
        } catch (ParserConfigurationException | SAXException | IOException failure) {
            throw new Problems.Problem(file + " is not an IntelliJ project file: " + failure.getMessage(), failure);
        }
    }
}
