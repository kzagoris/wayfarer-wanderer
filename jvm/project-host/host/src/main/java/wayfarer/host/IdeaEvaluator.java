package wayfarer.host;

import java.io.IOException;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.nio.file.DirectoryStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
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
 * Reads one IntelliJ {@code JAVA_MODULE} folder without a build tool: its {@code .iml} source folders and library order
 * entries, the project's {@code .idea/misc.xml} (output folder, language level, project JDK) and {@code .idea/libraries},
 * and the JDK it names, looked up by that name in the IDE's JDK table. Library jars that are not on disk are reported
 * as absent; IntelliJ's {@code out} folder is the output root.
 */
final class IdeaEvaluator {
    static boolean owns(Path folder) {
        return moduleFile(folder) != null;
    }

    Protocol.WorkspaceResult evaluate(Path root, Protocol.ImportParams params) {
        Path iml = moduleFile(root);
        if (iml == null) {
            throw new Problems.Problem(root + " has no single IntelliJ module file. An IntelliJ module is a folder that contains its .iml file.");
        }
        Element module = load(iml).getDocumentElement();
        if (!"JAVA_MODULE".equals(module.getAttribute("type"))) {
            throw new Problems.Problem(iml + " is not a Java module.");
        }
        Element manager = component(module, "NewModuleRootManager");
        if (manager == null) {
            throw new Problems.Problem(iml + " has no module roots.");
        }
        String name = stem(iml.getFileName().toString());
        List<Path> inputs = new ArrayList<>(List.of(iml, root.resolve(".idea").resolve("misc.xml")));
        Element project = projectRoot(root);
        boolean inherit = !"false".equalsIgnoreCase(manager.getAttribute("inherit-compiler-output"));
        String projectOutputUrl = childAttribute(project, "output", "url");
        String projectOutput = projectOutputUrl != null ? expand(projectOutputUrl, root, root) : null;
        String moduleOutputUrl = childAttribute(manager, "output", "url");
        String outputRoot = inherit
                ? (projectOutput != null ? projectOutput : root.resolve("out").toString())
                : moduleOutputUrl != null ? expand(moduleOutputUrl, root, root)
                : projectOutput != null ? projectOutput : root.resolve("out").toString();
        boolean ownOutput = !inherit && child(manager, "output") != null;
        String classes = ownOutput ? outputRoot : Path.of(outputRoot, "production", name).toString();
        String testClasses = ownOutput
                ? (childAttribute(manager, "output-test", "url") != null ? expand(childAttribute(manager, "output-test", "url"), root, root) : outputRoot)
                : Path.of(outputRoot, "test", name).toString();

        List<Protocol.SourceItem> main = new ArrayList<>();
        List<Protocol.SourceItem> test = new ArrayList<>();
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
            (isTest ? test : main).add(new Protocol.SourceItem(expand(url, root, root), kind, generated));
        }

        List<Protocol.DependencyModule> jars = new ArrayList<>();
        List<String> absent = new ArrayList<>();
        Path libraries = root.resolve(".idea").resolve("libraries");
        for (Element entry : children(manager, "orderEntry")) {
            if (!"library".equals(entry.getAttribute("type"))) {
                continue;
            }
            String library = entry.getAttribute("name");
            List<String> roots = libraryJars(root, library, inputs);
            if (roots.isEmpty()) {
                absent.add(library.isEmpty() ? "(unnamed library)" : library);
                continue;
            }
            for (String jar : roots) {
                if (Files.exists(Path.of(jar))) {
                    jars.add(new Protocol.DependencyModule(jar, null, null));
                } else {
                    absent.add(jar);
                }
            }
        }
        if (Files.isDirectory(libraries)) {
            inputs.add(libraries);
        }

        String jdkName = null;
        for (Element entry : children(manager, "orderEntry")) {
            if ("jdk".equals(entry.getAttribute("type")) && !entry.getAttribute("jdkName").isEmpty()) {
                jdkName = entry.getAttribute("jdkName");
                break;
            }
        }
        if (jdkName == null && project != null && !project.getAttribute("project-jdk-name").isEmpty()) {
            jdkName = project.getAttribute("project-jdk-name");
        }
        Integer level = languageLevel(project == null ? null : project.getAttribute("languageLevel"));
        // A JDK recorded for the project in Wayfarer wins; otherwise the one of that name in the IDE's table (which holds
        // IntelliJ's JDK table under IntelliJ's names), then JAVA_HOME.
        String home = ImportJvms.usable(params.jdk()) != null ? params.jdk() : ImportJvms.named(jdkName, params.jdks());
        if (home == null) {
            String javaHome = System.getenv("JAVA_HOME");
            home = javaHome == null || javaHome.isBlank() ? null : Path.of(javaHome).toAbsolutePath().normalize().toString();
        }
        Protocol.JvmEnvironment jvm = new Protocol.JvmEnvironment(home, level, jdkName);

        String id = name;
        List<Protocol.SourceSet> sets = new ArrayList<>();
        Map<String, List<Protocol.DependencyModule>> mainModules = new LinkedHashMap<>();
        mainModules.put("compile", jars);
        List<Protocol.DependencyModule> mainRuntime = new ArrayList<>();
        mainRuntime.add(new Protocol.DependencyModule(classes, null, id + ":main"));
        mainRuntime.addAll(jars);
        mainModules.put("runtime", mainRuntime);
        sets.add(new Protocol.SourceSet(id + ":main", "main", false, main, List.of(classes), mainModules));
        if (!test.isEmpty()) {
            Map<String, List<Protocol.DependencyModule>> testModules = new LinkedHashMap<>();
            List<Protocol.DependencyModule> testCompile = new ArrayList<>();
            testCompile.add(new Protocol.DependencyModule(classes, null, id + ":main"));
            testCompile.addAll(jars);
            testModules.put("compile", testCompile);
            List<Protocol.DependencyModule> testRuntime = new ArrayList<>();
            testRuntime.add(new Protocol.DependencyModule(testClasses, null, id + ":test"));
            testRuntime.addAll(testCompile);
            testModules.put("runtime", testRuntime);
            sets.add(new Protocol.SourceSet(id + ":test", "test", true, test, List.of(testClasses), testModules));
        }
        Protocol.BuildTarget target = MainScan.withDiscovered(new Protocol.BuildTarget(id, name, root.toString(), List.of("java"), jvm, jvm,
                sets, List.of("classes"), List.of(), List.of(), absent, null));
        Protocol.Jvm importJvm = new Protocol.Jvm(home, level == null ? null : String.valueOf(level), level, jdkName);
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
        return new Protocol.WorkspaceResult(params.generation(), root.toString(), "idea", iml.toString(), importJvm, List.of(target),
                BuildInputs.stamp(stamped), outputRoot);
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
        path = path.replace("$MODULE_DIR$", moduleDir.toString())
                .replace("$PROJECT_DIR$", projectDir.toString())
                .replace("$USER_HOME$", System.getProperty("user.home"));
        path = URLDecoder.decode(path.replace("+", "%2B"), StandardCharsets.UTF_8);
        if (MavenEvaluator.isWindows() && path.length() >= 3 && path.charAt(0) == '/' && path.charAt(2) == ':') {
            path = path.substring(1);
        }
        Path result = Path.of(path);
        if (!result.isAbsolute()) {
            result = projectDir.resolve(path);
        }
        return result.toAbsolutePath().normalize().toString();
    }

    private static List<String> libraryJars(Path root, String name, List<Path> inputs) {
        Path folder = root.resolve(".idea").resolve("libraries");
        List<String> jars = new ArrayList<>();
        if (name.isEmpty() || !Files.isDirectory(folder)) {
            return jars;
        }
        try (DirectoryStream<Path> files = Files.newDirectoryStream(folder, "*.xml")) {
            for (Path file : files) {
                NodeList libraries = load(file).getElementsByTagName("library");
                for (int i = 0; i < libraries.getLength(); i++) {
                    Element library = (Element) libraries.item(i);
                    if (!name.equals(library.getAttribute("name"))) {
                        continue;
                    }
                    Element classes = child(library, "CLASSES");
                    if (classes != null) {
                        for (Element entry : children(classes, "root")) {
                            if (!entry.getAttribute("url").isEmpty()) {
                                jars.add(expand(entry.getAttribute("url"), root, root));
                            }
                        }
                    }
                    return jars;
                }
            }
        } catch (IOException unreadable) {
            return jars;
        }
        return jars;
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
