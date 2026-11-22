package wayfarer.host;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/** Which class launches a source file's {@code main}, as the Java 25 launcher (JEP 512) and Kotlin name it. */
class MainScanTest {
    @TempDir
    Path root;

    @Test
    void aCompactSourceFileLaunchesByItsFileName() throws IOException {
        assertEquals("Main", main("Main.java", "\nvoid main() {\n\n}\n"));
        assertEquals("Hello", main("Hello.java", """
                import java.util.List;

                String greeting = "class Nope { public static void main(String[] a) {} }";

                void main() {
                    IO.println(greeting + List.of(1, 2));
                }
                """));
        assertEquals("Tool", main("Tool.java", "static void main(String[] args) { }"));
    }

    @Test
    void aCompactSourceFileMayDeclareTypesAndMethodsBeforeMain() throws IOException {
        assertEquals("Shapes", main("Shapes.java", """
                record Point(int x, int y) { }

                int area(Point p) { return p.x() * p.y(); }

                void main() {
                    int[] values = {1, 2};
                    IO.println(area(new Point(values[0], values[1])));
                }
                """));
    }

    @Test
    void anInstanceMainLaunchesItsClass() throws IOException {
        assertEquals("demo.App", main("App.java", """
                package demo;

                class App {
                    void main() {
                        System.out.println("hi");
                    }
                }
                """));
        assertEquals("demo.Args", main("Args.java", """
                package demo;
                public class Args { protected void main(final String... args) { } }
                """));
    }

    @Test
    void theClassicMainStillLaunches() throws IOException {
        assertEquals("demo.Classic", main("Classic.java", """
                package demo;
                /** A class with a main. */
                public final class Classic {
                    public static void main(String args[]) { }
                }
                """));
        assertEquals("demo.Iface", main("Iface.java", "package demo; interface Iface { static void main(java.lang.String[] a) { } }"));
    }

    @Test
    void aNestedTypesMainLaunchesByItsBinaryName() throws IOException {
        assertEquals("demo.Outer$Inner", main("Outer.java", """
                package demo;
                class Outer {
                    void helper() { Runnable r = new Runnable() { public void run() { } }; }
                    static class Inner {
                        public static void main(String[] args) { }
                    }
                }
                """));
    }

    @Test
    void aMainTheLauncherCannotRunIsNotOne() throws IOException {
        assertNull(main("Private.java", "class Private { private static void main(String[] a) { } }"));
        assertNull(main("Wrong.java", "class Wrong { static void main(int x) { } void main(String a, String b) { } }"));
        assertNull(main("Returns.java", "class Returns { static int main(String[] a) { return 0; } }"));
        assertNull(main("Instance.java", "interface Instance { default void main() { } }"));
        assertNull(main("Abstract.java", "abstract class Abstract { abstract void main(); }"));
        assertNull(main("Local.java", "class Local { void run() { class L { void main() { } } } }"));
        assertNull(main("Commented.java", "// void main() { }\n/* static void main(String[] a) {} */\nclass Commented { }"));
        assertNull(main("Call.java", "class Call { void run() { main(); } }"));
    }

    @Test
    void aKotlinFileLaunchesByItsFileClass() throws IOException {
        assertEquals("demo.MainKt", main("main.kt", "package demo\n\nfun main() { }\n"));
        assertEquals("Tool", main("tool.kt", "@file:JvmName(\"Tool\")\nfun main(args: Array<String>) { }\n"));
    }

    private String main(String name, String text) throws IOException {
        Path file = root.resolve(name);
        Files.writeString(file, text);
        return MainScan.mainOf(file);
    }
}
