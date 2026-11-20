// From Passenger 2044e49b src/Passenger.App/Settings/SettingsDialog.Trust.cs, MIT (notices/Passenger/LICENSE).
using Avalonia.Controls;
using WayfarerWanderer.App.Shell.State;
using Passenger.Core;

namespace WayfarerWanderer.App.Shell.Settings;

/// <summary>What the person answered of the folders they opened, as Settings shows it and what puts a change of it in force:
/// the answers as kept (<see cref="TrustState"/>), and <see cref="Apply"/> given those Settings showed and those it has now,
/// answering those kept then.</summary>
public sealed record TrustAnswers(TrustState Kept, Func<TrustState, TrustState, TrustState> Apply);

/// <summary>The Trusted Folders page: each folder the person trusted or opened without trusting, its Trusted box trusting it or
/// taking its trust back, and Forget, so it is asked of again as it next opens; each put in force by OK or Apply.</summary>
public sealed partial class SettingsDialog
{
    public const string TrustPage = "Trusted Folders";

    /// <summary>The page's table of folders; empty where the window keeps no answers.</summary>
    public TrustedFoldersTable TrustedFolders { get; } = new();

    /// <summary>What the window keeps of the answers and puts a change of them in force; null where it keeps none.</summary>
    private readonly TrustAnswers? trust;

    /// <summary>The answers as the dialog opened on them or last applied them.</summary>
    private TrustState trustApplied = new();

    private void AddTrust()
    {
        if (trust is null)
        {
            return;
        }
        trustApplied = trust.Kept;
        TrustedFolders.Set(trust.Kept);
        Add(TrustPage, "Folders answered for", new StackPanel { MaxWidth = OptionRow.EditorWidth, Children = { TrustedFolders } },
            "workspace trust trusted untrusted restricted safe mode folders forget stop trusting code runs security",
            "A folder you trust has its code run as Wayfarer works on it: its Maven or Gradle build and wrapper, its tests, and the "
            + "programs you run or debug from it. Clear Trusted to stop trusting a folder, and Forget to be asked again as it next opens. Taken back from the folder open in the window, "
            + "its language servers end.");
    }

    /// <summary>The answers' change put in force, from those applied last to those the table has now.</summary>
    private void ApplyTrust()
    {
        if (trust is null)
        {
            return;
        }
        TrustState now = TrustedFolders.Answers();
        if (!Alike(now.Trusted, trustApplied.Trusted) || !Alike(now.Untrusted, trustApplied.Untrusted))
        {
            TrustState from = trustApplied;
            TrustState kept = trust.Apply(from, now);
            trustApplied = kept;
            if (!Alike(kept.Trusted, now.Trusted) || !Alike(kept.Untrusted, now.Untrusted))
            {
                TrustedFolders.Set(kept);
            }
        }

        static bool Alike(IReadOnlyList<string> one, IReadOnlyList<string> other) => new HashSet<string>(one, PathNames.Comparer).SetEquals(other);
    }
}
