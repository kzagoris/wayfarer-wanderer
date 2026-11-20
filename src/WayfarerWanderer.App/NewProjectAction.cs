using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>New Project: the dialog (<see cref="NewProjectDialog"/>) over the window, and the project it made opened in it.</summary>
public sealed class NewProjectAction
{
    public async Task<ProjectGenerationResult?> RunAsync(EditorWindow owner, CancellationToken cancellationToken = default)
    {
        NewProjectDialog dialog = new(DefaultFolder(owner.Session?.Root));
        ProjectGenerationResult? result = await dialog.ShowDialog<ProjectGenerationResult?>(owner);
        if (result is null)
        {
            return null;
        }
        await owner.OpenWorkspaceAsync(result.Destination, cancellationToken);
        return result;
    }

    /// <summary>Where a new project goes: beside the project open, else in WayfarerProjects in the person's home.</summary>
    public static string DefaultFolder(string? open) =>
        open is not null && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(open)) is { Length: > 0 } beside
            ? beside
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "WayfarerProjects");
}
