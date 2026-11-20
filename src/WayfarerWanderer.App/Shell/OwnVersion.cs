using System.Reflection;

namespace WayfarerWanderer.App.Shell;

/// <summary>Wayfarer's own version, as Directory.Build.props declares it, without the build's source revision: what its
/// windows show. Passenger.Core's <c>Product.Version</c> is the vendored libraries' version, not Wayfarer's.</summary>
public static class OwnVersion
{
    public static string Shown { get; } =
        (typeof(OwnVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
}
