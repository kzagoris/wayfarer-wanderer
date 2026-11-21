namespace WayfarerWanderer.Jvm;

/// <summary>A launch configuration names a JDK no JDK here meets: Run shows the missing-JDK banner for it rather than
/// starting on another JDK.</summary>
public sealed class LaunchJdkMissingException(string configuration, JdkRequirement requirement)
    : Exception($"The run configuration '{configuration}' runs on {requirement.Describe()}, and no installed JDK matches.")
{
    public string Configuration { get; } = configuration;

    public JdkRequirement Requirement { get; } = requirement;
}

/// <summary>
/// The JDK a launch configuration runs on (<see cref="JvmLaunchConfiguration.Jdk"/>): the JDK of that name in the table,
/// else one of the release the name carries, as an IntelliJ JDK name resolves. A JDK older than the module compiles for
/// is refused before launch, naming both versions, rather than failing with <c>UnsupportedClassVersionError</c>. Gradle
/// and Maven test runs keep the build's own toolchain; this applies to Wayfarer's direct application and debug launches.
/// </summary>
public static class LaunchJdks
{
    /// <summary>The home a configuration's named JDK resolves to for <paramref name="module"/>.</summary>
    /// <exception cref="LaunchJdkMissingException">No JDK in the table meets the name.</exception>
    /// <exception cref="InvalidDataException">The JDK is older than the module's compile target.</exception>
    public static string Home(JvmModule module, JvmLaunchConfiguration configuration, JdkRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(registry);
        JdkRequirement requirement = JdkRequirement.FromName(configuration.Jdk, JdkRequirement.Launch);
        Jdk jdk = JdkResolution.Choose(requirement, registry.All())
            ?? throw new LaunchJdkMissingException(configuration.Name.Length > 0 ? configuration.Name : configuration.MainClass, requirement);
        if (module.CompileToolchain.LanguageVersion is int target && jdk.Feature < target)
        {
            throw new InvalidDataException(
                $"The run configuration '{configuration.Name}' runs on {jdk.Name} (Java {jdk.Feature}), and module '{module.Name}' compiles for Java {target}. Choose a JDK {target} or later for it.");
        }
        return jdk.Home;
    }
}
