namespace WayfarerWanderer.Jvm;

/// <summary>Imports a workspace's model for one generation. <see cref="WorkspaceImportSession"/> calls it, and handles
/// generations, cancelling a superseded import, stale replies and keeping the last good snapshot.</summary>
public interface IWorkspaceImporter
{
    Task<WorkspaceSnapshot> ImportAsync(string workspaceRoot, long generation, CancellationToken cancellationToken);
}

/// <summary>How far an import has got: the modules evaluated so far in one generation.</summary>
public sealed record ImportProgress(long Generation, int ModulesEvaluated, string? LastModule);
