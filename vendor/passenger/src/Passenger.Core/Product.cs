using System.Reflection;
using System.Runtime.InteropServices;

namespace Passenger.Core;

/// <summary>What this build of Passenger is, as its build wrote it into the assembly: the version (a vX.Y.Z tag's, which
/// tools/package-mac.sh gives the build, else Directory.Build.props') and the commit it was built from (the SDK adds it to
/// the informational version). About shows them, the log and an agent are told them, and a check for a newer release
/// compares its tag with <see cref="Version"/>. Its licence and notices are files beside it.</summary>
public static class Product
{
    private static readonly string Informational =
        typeof(Product).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>The version a package of this build says it is ("0.1.0").</summary>
    public static string Version { get; } = Informational.Split('+')[0];

    /// <summary>The full hash of the commit it was built from, or null for a build from source outside Git.</summary>
    public static string? Commit { get; } = Informational.Split('+') is [_, { Length: > 0 } commit, ..] ? commit : null;

    /// <summary>The .NET it runs on and the processor it runs as (".NET 11.0.0 arm64").</summary>
    public static string Runtime => $"{RuntimeInformation.FrameworkDescription} {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";

    /// <summary>The product's name as titles, About and the empty editor say it: Passenger's own, or the name a host running
    /// Passenger's shell gave (<see cref="Identify"/>).</summary>
    public static string Name { get; private set; } = "Passenger";

    /// <summary>The file name of the product's app host beside its assemblies, without the system's extension.</summary>
    public static string ProgramName { get; private set; } = "Passenger";

    /// <summary>A host running Passenger's shell as its own product names it, before anything reads the program's path or
    /// the state folder, so its windows, state and helpers (git's askpass, gpg) are its own and never Passenger's.</summary>
    public static void Identify(string name, string programName)
    {
        if (Own.IsValueCreated)
        {
            throw new InvalidOperationException("The product is identified before its program's path is first read.");
        }
        Name = name;
        ProgramName = programName;
    }

    /// <summary>Passenger's own program, the app host beside its assemblies, by its canonical path: what another program is
    /// told to start to reach Passenger, whatever started this process (dotnet, or a test's host).</summary>
    public static string Program => Own.Value;

    private static readonly Lazy<string> Own = new(() =>
        PathNames.Canonical(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? ProgramName + ".exe" : ProgramName)));

    /// <summary>Passenger's licence, beside the app.</summary>
    public static string License => Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");

    /// <summary>The notices of what Passenger carries, with their licences, written beside the app by its build
    /// (tools/Passenger.Notices).</summary>
    public static string Notices => Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");

    private static readonly Lazy<string[]> Carried = new(() => [PathNames.Canonical(License), PathNames.Canonical(Notices)]);

    /// <summary>Whether a file (by its canonical path) is the licence or the notices the app carries, which an editor
    /// shows read-only: a change would be the installed app's.</summary>
    public static bool Carries(string path) => Carried.Value.Contains(path);
}
