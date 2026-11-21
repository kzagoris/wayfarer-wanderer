namespace WayfarerWanderer.Jvm;

/// <summary>A JDK asked for that no JDK on this machine meets: the module that asked (null for the project's own
/// requirement, which every module without one of its own shares) and what it asked for.</summary>
public sealed record MissingJdk(string? Module, JdkRequirement Requirement);

/// <summary>
/// What the window's missing-JDK banner shows of a snapshot, as IntelliJ shows a missing SDK, and where a JDK chosen for
/// one goes: a project's requirement into this machine's <see cref="ProjectJdkChoices"/> (never the shared project file),
/// an IntelliJ JDK name into the <see cref="JdkRegistry"/> under that name, a build's (a Gradle toolchain, a Maven
/// toolchains requirement) into the table, where the build sees it once Wayfarer passes its JDKs to the build.
/// </summary>
public static class MissingJdks
{
    /// <summary>Each module's first missing requirement (compile, then runtime), in the snapshot's order, with the
    /// project's requirement once; none for no snapshot.</summary>
    public static IReadOnlyList<MissingJdk> Of(WorkspaceSnapshot? snapshot)
    {
        List<MissingJdk> missing = [];
        foreach (JvmModule module in snapshot?.Modules ?? [])
        {
            JvmToolchain? toolchain = module.CompileToolchain.Missing ? module.CompileToolchain
                : module.RuntimeToolchain.Missing ? module.RuntimeToolchain
                : null;
            if (toolchain?.Requirement is not { } requirement)
            {
                continue;
            }
            MissingJdk entry = requirement.Source == JdkRequirement.Project ? new MissingJdk(null, requirement) : new MissingJdk(module.Name, requirement);
            if (!missing.Contains(entry))
            {
                missing.Add(entry);
            }
        }
        return missing;
    }

    /// <summary>The banner's words: the first missing requirement, and how many more there are.</summary>
    public static string Say(IReadOnlyList<MissingJdk> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        if (missing.Count == 0)
        {
            return string.Empty;
        }
        MissingJdk first = missing[0];
        string who = first.Module is null ? "The project" : $"Module '{first.Module}'";
        string more = missing.Count > 1 ? $", and {missing.Count - 1} more" : string.Empty;
        return $"{who} needs {first.Requirement.Describe()}{more}. No installed JDK matches.";
    }

    /// <summary>The JDKs Choose… offers: those of the feature asked for, or every one when no feature was asked for.</summary>
    public static IReadOnlyList<Jdk> Candidates(JdkRequirement requirement, IEnumerable<Jdk> jdks)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return [.. jdks.Where(jdk => requirement.Feature is not int feature || jdk.Feature == feature)];
    }

    /// <summary>What Choose… says beside the list, where a chosen JDK may not be seen at once; null where it is.</summary>
    public static string? Note(JdkRequirement requirement) => requirement.Source switch
    {
        JdkRequirement.GradleToolchain => "Gradle looks for toolchains among the JDKs it finds itself; it may not use a JDK chosen here until Wayfarer passes its JDKs to Gradle.",
        JdkRequirement.MavenRelease => "Maven looks for toolchains in its toolchains.xml; it may not use a JDK chosen here until Wayfarer passes its JDKs to Maven.",
        _ => null,
    };

    /// <summary>Puts <paramref name="chosen"/> where <paramref name="missing"/>'s requirement looks, keeping it in the
    /// table; returns it as the table now names it.</summary>
    public static Jdk Choose(string projectRoot, MissingJdk missing, Jdk chosen, JdkRegistry registry, ProjectJdkChoices choices)
    {
        ArgumentNullException.ThrowIfNull(missing);
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(choices);
        Jdk kept;
        if (missing.Requirement.Source == JdkRequirement.Idea && missing.Requirement.Name is { Length: > 0 } name)
        {
            try
            {
                kept = registry.Add(chosen.Home, name);
            }
            catch (ArgumentException)
            {
                // Another JDK is kept under that name: this one is kept under its own.
                kept = registry.Add(chosen.Home);
            }
        }
        else
        {
            kept = chosen.Origin == JdkOrigin.Detected ? registry.Add(chosen.Home) : chosen;
        }
        if (missing.Requirement.Source == JdkRequirement.Project)
        {
            choices.Choose(projectRoot, kept.Name);
        }
        return kept;
    }
}
