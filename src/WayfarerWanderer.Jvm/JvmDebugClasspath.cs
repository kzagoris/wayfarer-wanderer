namespace WayfarerWanderer.Jvm;

public static class JvmDebugClasspath
{
    public static async Task<IReadOnlyList<string>> ResolveAsync(
        WorkspaceSnapshot snapshot, JvmLaunchConfiguration configuration, CancellationToken cancellationToken = default, JvmBuild? build = null)
    {
        Passenger.Core.WorkspaceTrust.Check(snapshot.Root);
        JvmModule module = snapshot.Module(configuration.Module)
            ?? throw new InvalidDataException($"Unknown debug module '{configuration.Module}'.");
        JvmBuild compiler = build ?? new();
        if (snapshot.BuildSystem == JvmBuildSystem.Idea)
        {
            ProcessResult compiled = await compiler.RunAsync(snapshot, new(snapshot.Root, module.Id, "classes", []), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (compiled.ExitCode != 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, compiled.Output.TakeLast(16).Select(line => line.Line)));
            }
            IReadOnlyList<string> classes = module.Classpath.Runtime;
            if (classes.Count == 0 || classes.Any(path => !Path.IsPathFullyQualified(path)))
            {
                throw new InvalidDataException($"Module '{module.Name}' has no output directory.");
            }
            return classes;
        }
        IReadOnlyList<string> classpath;
        if (snapshot.BuildSystem == JvmBuildSystem.Gradle)
        {
            string script = Path.Combine(AppContext.BaseDirectory, "Gradle", "debug-runtime.gradle");
            ProcessResult result = await Run("wayfarerDebugRuntime", ["--init-script", script]);
            const string prefix = "WAYFARER_RUNTIME=";
            classpath = result.Output.Where(line => line.Line.StartsWith(prefix, StringComparison.Ordinal))
                .Select(line => line.Line[prefix.Length..]).ToArray();
        }
        else
        {
            string output = Path.GetTempFileName();
            try
            {
                // The dependency goal runs in reactor order. The selected module writes last.
                await Run("package", ["-DskipTests", "org.apache.maven.plugins:maven-dependency-plugin:3.8.1:build-classpath",
                    "-DincludeScope=runtime", "-Dmdep.regenerateFile=true", "-Dmdep.outputFile=" + output]);
                string dependencies = (await File.ReadAllTextAsync(output, cancellationToken)).Trim();
                // help:evaluate is an aggregator. Evaluate from the selected POM, outside the reactor.
                await Run("org.apache.maven.plugins:maven-help-plugin:3.5.1:evaluate",
                    ["-N", "-f", Path.Combine(module.Root, "pom.xml"), "-Dexpression=project.build.outputDirectory", "-Doutput=" + output], ".");
                string classes = (await File.ReadAllTextAsync(output, cancellationToken)).Trim();
                classpath = [classes, .. dependencies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)];
            }
            finally
            {
                File.Delete(output);
            }
        }
        if (classpath.Count == 0 || classpath.Any(path => !Path.IsPathFullyQualified(path)))
            throw new InvalidDataException($"The wrapper returned no usable runtime classpath for '{module.Name}'.");
        return classpath;



        async Task<ProcessResult> Run(string task, IReadOnlyList<string> args, string? selected = null)
        {
            ProcessResult result = await compiler.RunAsync(snapshot, new(snapshot.Root, selected ?? module.Id, task, args),
                cancellationToken: cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, result.Output.TakeLast(16).Select(line => line.Line)));
            return result;
        }
    }
}
