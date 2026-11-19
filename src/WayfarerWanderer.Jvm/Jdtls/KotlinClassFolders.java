package wayfarer.jdtls;

import java.io.File;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

import org.eclipse.core.resources.IProject;
import org.eclipse.core.resources.ResourcesPlugin;
import org.eclipse.core.runtime.IProgressMonitor;
import org.eclipse.core.runtime.Path;
import org.eclipse.jdt.core.IClasspathAttribute;
import org.eclipse.jdt.core.IClasspathEntry;
import org.eclipse.jdt.core.IJavaProject;
import org.eclipse.jdt.core.JavaCore;
import org.eclipse.jdt.ls.core.internal.IDelegateCommandHandler;

/**
 * Maven projects reach JDT LS through m2e, which compiles no Kotlin. Wayfarer compiles the Kotlin
 * classes with the project's wrapper and names a class folder per module root here. Each folder
 * becomes an exported library entry of that module's project, so the module and its dependents
 * resolve Kotlin declarations. Calling again replaces every entry this handler added before.
 */
public final class KotlinClassFolders implements IDelegateCommandHandler {
    public static final String COMMAND = "wayfarer.kotlinClassFolders";
    private static final String MARKER = "wayfarer.kotlinClasses";

    @Override
    public Object executeCommand(String commandId, List<Object> arguments, IProgressMonitor monitor) throws Exception {
        if (!COMMAND.equals(commandId)) throw new UnsupportedOperationException(commandId);
        // Arguments alternate module root and class folder.
        Map<String, String> folders = new HashMap<>();
        for (int i = 0; i + 1 < arguments.size(); i += 2)
            folders.put(canonical(text(arguments.get(i))), text(arguments.get(i + 1)));

        List<String> attached = new ArrayList<>();
        for (IProject project : ResourcesPlugin.getWorkspace().getRoot().getProjects()) {
            if (!project.isOpen() || !project.hasNature(JavaCore.NATURE_ID) || project.getLocation() == null) continue;
            String root = canonical(project.getLocation().toOSString());
            IJavaProject java = JavaCore.create(project);
            List<IClasspathEntry> entries = new ArrayList<>();
            boolean removed = false;
            for (IClasspathEntry entry : java.getRawClasspath()) {
                if (isOurs(entry)) removed = true;
                else entries.add(entry);
            }
            String folder = folders.get(root);
            if (folder != null) {
                IClasspathAttribute[] marker = { JavaCore.newClasspathAttribute(MARKER, "true") };
                entries.add(JavaCore.newLibraryEntry(new Path(folder), null, null, null, marker, true));
                attached.add(root);
            }
            if (folder != null || removed)
                java.setRawClasspath(entries.toArray(IClasspathEntry[]::new), monitor);
        }
        return attached;
    }

    private static boolean isOurs(IClasspathEntry entry) {
        for (IClasspathAttribute attribute : entry.getExtraAttributes())
            if (MARKER.equals(attribute.getName())) return true;
        return false;
    }

    private static String canonical(String path) throws java.io.IOException {
        return new File(path).getCanonicalPath();
    }

    // JDT LS hands over strings as Gson JSON primitives; read them without a compile-time Gson dependency.
    private static String text(Object value) throws Exception {
        if (value instanceof String s) return s;
        Method read = value.getClass().getMethod("getAsString");
        return (String) read.invoke(value);
    }
}
