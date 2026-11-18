using System.Text.Json;

namespace WayfarerWanderer.Jvm;

public interface ILaunchConfigurationStore
{
    Task SaveAsync(JvmLaunchConfiguration configuration, CancellationToken cancellationToken = default);

    Task<JvmLaunchConfiguration?> LoadAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JvmLaunchConfiguration>> ListAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

public sealed class LaunchConfigurationStore : ILaunchConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string root;

    public LaunchConfigurationStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.GetFullPath(root);
    }

    public async Task SaveAsync(JvmLaunchConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Validate(configuration);
        Directory.CreateDirectory(root);
        string destination = FileOf(configuration.Id);
        string staging = destination + ".new";
        try
        {
            await using (FileStream stream = new(staging, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, configuration, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }

    public async Task<JvmLaunchConfiguration?> LoadAsync(string id, CancellationToken cancellationToken = default)
    {
        string path = FileOf(id);
        if (!File.Exists(path))
        {
            return null;
        }
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        JvmLaunchConfiguration? configuration = await JsonSerializer.DeserializeAsync<JvmLaunchConfiguration>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        return configuration is null ? null : ValidateAndReturn(configuration);
    }

    public async Task<IReadOnlyList<JvmLaunchConfiguration>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }
        List<JvmLaunchConfiguration> configurations = [];
        foreach (string file in Directory.EnumerateFiles(root, "*.json").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            JvmLaunchConfiguration? configuration = await JsonSerializer.DeserializeAsync<JvmLaunchConfiguration>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (configuration is not null)
            {
                configurations.Add(ValidateAndReturn(configuration));
            }
        }
        return configurations;
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = FileOf(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string FileOf(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Path.GetFileName(id) != id || id is "." or "..")
        {
            throw new ArgumentException("A launch configuration id must be a file name.", nameof(id));
        }
        return Path.Combine(root, id + ".json");
    }

    private static void Validate(JvmLaunchConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.Module);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.MainClass);
    }

    private static JvmLaunchConfiguration ValidateAndReturn(JvmLaunchConfiguration configuration)
    {
        Validate(configuration);
        return configuration;
    }
}
