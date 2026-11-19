using System.Diagnostics;
using Passenger.Core;

namespace WayfarerWanderer.Jvm;

/// <summary>Uses user-installed servers. No language-server binary or license is redistributed.</summary>
internal static class JvmLanguageServers
{
    public static ProcessStartInfo Command(string language, string root, string state)
    {
        string extensions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        string Installation(string variable, string prefix)
        {
            string? configured = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(configured)) return Path.GetFullPath(configured);
            string? extension = Directory.Exists(extensions)
                ? Directory.GetDirectories(extensions, prefix + "-*").OrderDescending(StringComparer.Ordinal).FirstOrDefault()
                : null;
            return extension is null ? throw new InvalidOperationException($"Set {variable} to your installed language server directory.") : Path.Combine(extension, "server");
        }

        if (language == "kotlin")
        {
            string home = Installation("WAYFARER_KOTLIN_LSP_HOME", "jetbrains.kotlin-server");
            string launcher = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "intellij-server.exe" : "intellij-server");
            return ChildProcesses.Command(launcher, ["--stdio", "--system-path", state], root);
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
