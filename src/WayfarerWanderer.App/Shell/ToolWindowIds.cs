// From Passenger 2044e49b src/Passenger.App/ToolWindowIds.cs, MIT (notices/Passenger/LICENSE).
namespace WayfarerWanderer.App.Shell;

/// <summary>Every tool window's id: what the layout keeps, a theme's layout names and the keymap's Show actions use. Kept as
/// Passenger names the ones both products have, so a theme's layout means the same in each.</summary>
public static class ToolWindowIds
{
    public const string Explorer = "explorer";
    public const string Build = "build";
    public const string Tests = "tests";
    public const string Debug = "debug";
    public const string Run = "run";
    public const string Problems = "problems";
    public const string Terminal = "terminal";
    public const string Git = "git";
    public const string Commit = "commit";

    /// <summary>Every id above.</summary>
    public static IReadOnlySet<string> All { get; } = typeof(ToolWindowIds).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
}
