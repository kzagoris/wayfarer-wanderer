// After Passenger 494009d7 src/Passenger.App/Templates/RepositoryChoices.cs, MIT (notices/Passenger/LICENSE): no .gitignore
// choice, as every starter writes its own.
using System.Text.Json.Nodes;
using WayfarerWanderer.App.Shell.Vcs;

namespace WayfarerWanderer.App.Shell.State;

/// <summary>What New Project makes for Git with a project, as the person last made one: a repository, off until the person
/// asks for one as in IntelliJ, and in it each file a starter does not write itself (<see cref="RepositoryFiles"/>), every
/// one of them made until the person says not.</summary>
public sealed record RepositoryChoices : IStateDocument<RepositoryChoices>
{
    public bool Repository { get; init; }

    public bool GitAttributes { get; init; } = true;

    public bool EditorConfig { get; init; } = true;

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    /// <summary>The files New Project offers for its repository: every starter writes its own .gitignore.</summary>
    public static IReadOnlyList<RepositoryFile> Offered { get; } = [RepositoryFile.GitAttributes, RepositoryFile.EditorConfig];

    /// <summary>Whether the repository is given the file.</summary>
    public bool Gives(RepositoryFile file) => file switch
    {
        RepositoryFile.GitAttributes => GitAttributes,
        RepositoryFile.EditorConfig => EditorConfig,
        _ => false,
    };

    /// <summary>With the repository given the file or not.</summary>
    public RepositoryChoices Giving(RepositoryFile file, bool given) => file switch
    {
        RepositoryFile.GitAttributes => this with { GitAttributes = given },
        RepositoryFile.EditorConfig => this with { EditorConfig = given },
        _ => this,
    };

    public RepositoryChoices Clamped() => this;
}
