using System.Diagnostics;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

/// <summary>Uses user-installed servers. No language-server binary or license is redistributed.</summary>
internal static class JvmLanguageServers
{
    public static ProcessStartInfo Command(string language, string root, string state)
    {
        string extensions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        string Installation(string variable, string prefix, string? folder = null)
        {
            string? configured = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(configured)) return Path.GetFullPath(configured);
            string? extension = Directory.Exists(extensions)
                ? Directory.GetDirectories(extensions, prefix + "-*").OrderDescending(StringComparer.Ordinal).FirstOrDefault()
                : null;
            return extension is null ? throw new InvalidOperationException(folder is null
                ? $"Set {variable} to your installed language server directory."
                : $"Unpack the language server into {Path.Combine(folder, "<version>")}, or set {variable} to its directory.") : Path.Combine(extension, "server");
        }

        if (language == "kotlin")
        {
            string? configured = Environment.GetEnvironmentVariable(KotlinServerInstalls.Variable);
            string home = string.IsNullOrEmpty(configured)
                ? KotlinServerInstalls.Newest(KotlinServerInstalls.Folder) ?? Installation(KotlinServerInstalls.Variable, "jetbrains.kotlin-server", KotlinServerInstalls.Folder)
                : Path.GetFullPath(configured);
            return ChildProcesses.Command(KotlinServerInstalls.Launcher(home), ["--stdio", "--system-path", state], root);
        }

        string jdt = Installation("WAYFARER_JDTLS_HOME", "redhat.java");
        string java = OperatingSystem.IsWindows() ? "java.exe" : "java";
        string bundled = Path.Combine(Path.GetDirectoryName(jdt)!, "jre");
        string? runtime = Directory.Exists(bundled)
            ? Directory.EnumerateFiles(bundled, java, SearchOption.AllDirectories).FirstOrDefault()
            : null;
        runtime ??= Environment.GetEnvironmentVariable("JAVA_HOME") is string jdk ? Path.Combine(jdk, "bin", java) : java;
        string config = Path.Combine(jdt, OperatingSystem.IsWindows() ? "config_win" : OperatingSystem.IsMacOS() ? "config_mac" : "config_linux");
        string jar = Directory.GetFiles(Path.Combine(jdt, "plugins"), "org.eclipse.equinox.launcher_*.jar").Single();
        return ChildProcesses.Command(runtime,
            ["-Declipse.application=org.eclipse.jdt.ls.core.id1", "-Dosgi.bundles.defaultStartLevel=4",
             "-Declipse.product=org.eclipse.jdt.ls.core.product", "-Dosgi.sharedConfiguration.area=" + config,
             "-Dosgi.sharedConfiguration.area.readOnly=true", "-Dosgi.configuration.cascaded=true",
             "--add-modules=ALL-SYSTEM", "--add-opens=java.base/java.util=ALL-UNNAMED", "--add-opens=java.base/java.lang=ALL-UNNAMED",
             "-jar", jar, "-configuration", Path.Combine(state, "configuration"), "-data", Path.Combine(state, "workspace")], root);
    }
}

/// <summary>Kotlin servers the user unpacked into Wayfarer's own folder, one per version:
/// <c>~/.local/share/wayfarer/kotlin-lsp/&lt;version&gt;/</c> on Linux, the local application data folder elsewhere.
/// The variable overrides the folder; a VS Code extension is the last resort.</summary>
public static class KotlinServerInstalls
{
    public const string Variable = "WAYFARER_KOTLIN_LSP_HOME";

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wayfarer", "kotlin-lsp");

    public static string Launcher(string home) =>
        Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "intellij-server.exe" : "intellij-server");

    /// <summary>The highest version under <paramref name="folder"/> that has a launcher, or null.</summary>
    public static string? Newest(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetDirectories(folder)
                .Select(home => (home, version: Version.TryParse(Path.GetFileName(home), out var v) ? v : null))
                .Where(entry => entry.version is not null && File.Exists(Launcher(entry.home)))
                .OrderByDescending(entry => entry.version)
                .Select(entry => entry.home)
                .FirstOrDefault()
            : null;
}
