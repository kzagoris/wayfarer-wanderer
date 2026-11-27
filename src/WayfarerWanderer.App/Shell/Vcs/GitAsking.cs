// From Passenger 494009d7 src/Passenger.App/Containers/ContainerSecrets.cs (Mark, InSaid) and Vcs/VcsController.cs (AskerOf),
// MIT (notices/Passenger/LICENSE): only the secrets a person typed for git, without containers' variables and flags.
using System.Collections.Concurrent;
using Passenger.Core;
using Passenger.Vcs;
using WayfarerWanderer.App.Shell.State;

namespace WayfarerWanderer.App.Shell.Vcs;

/// <summary>What a person typed for git to sign in or sign a commit, masked wherever git's words are shown again: its
/// errors in a dialog, the log.</summary>
public static class TypedSecrets
{
    /// <summary>What a secret is shown as.</summary>
    public const string Masked = "••••••";

    private static readonly ConcurrentDictionary<string, byte> Marked = new(StringComparer.Ordinal);

    /// <summary>A password, passphrase or user typed, masked from now on.</summary>
    public static void Mark(string secret)
    {
        if (secret.Length > 0)
        {
            Marked.TryAdd(secret, 0);
        }
    }

    /// <summary>What a program said, each secret typed in it masked, the longest first so one within another stays hidden.</summary>
    public static string InSaid(string said)
    {
        foreach (string secret in Marked.Keys.OrderByDescending(s => s.Length))
        {
            said = said.Replace(secret, Masked, StringComparison.Ordinal);
        }
        return said;
    }
}

/// <summary>Who answers git's questions as it reaches a remote or signs a commit.</summary>
public static class GitAskers
{
    /// <summary>Who answers git through <paramref name="ask"/>: Wayfarer's own executable, run by git as its helper
    /// (Program.Main hands it to <see cref="GitAskPass.Answer"/>), asking over a socket in the state's socket folder; null
    /// where that folder cannot be had, when git asks nobody.</summary>
    public static GitAsker? Of(Func<GitQuestion, CancellationToken, Task<GitAnswer>> ask)
    {
        try
        {
            return new GitAsker(Product.Program, SocketFolders.Own(StateStore.DefaultRoot), ask);
        }
        catch (Exception unowned) when (unowned is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Git: asks nobody for a login, as the socket folder cannot be had: {unowned.Message}");
            return null;
        }
    }
}
