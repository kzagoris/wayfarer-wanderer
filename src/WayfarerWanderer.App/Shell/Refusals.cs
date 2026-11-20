// From Passenger 2044e49b src/Passenger.App/Refusals.cs, MIT (notices/Passenger/LICENSE): the refusals Git's views and
// commands give.
using Passenger.Core;

namespace WayfarerWanderer.App.Shell;

/// <summary>Why an action cannot run here, in the words every place that refuses it uses: the action's reason (its menu
/// item's tooltip, Find Action's row) and the status bar's.</summary>
public static class Refusals
{
    /// <summary>The folder open is not trusted, so Wayfarer runs none of its code (<see cref="WorkspaceTrust"/>).</summary>
    public static string Untrusted => WorkspaceTrust.Untrusted;

    /// <summary>The window has no folder open.</summary>
    public const string NoFolder = "No folder is open";

    /// <summary>The folder open is in no Git working tree.</summary>
    public const string NoRepository = "The folder is not in a Git repository";

    /// <summary>The folder open is in a Git working tree already, which Create Git Repository would make.</summary>
    public const string InRepository = "The folder is in a Git repository already";

    /// <summary>Git has files in conflict, which no commit takes as they are.</summary>
    public const string CommitWithConflicts = "Resolve conflicts in order to commit";

    /// <summary>The Commit window has no file chosen, and no merge to finish or commit to amend.</summary>
    public const string NoFilesChosenToCommit = "No files are chosen to commit";

    /// <summary>The Commit window's message is empty or blank.</summary>
    public const string EmptyCommitMessage = "The commit message is empty";

    /// <summary>The Git log's branches panel has HEAD, a group or no row chosen, where a branch or tag is acted on.</summary>
    public const string NoRefChosen = "No branch or tag is chosen";

    /// <summary>The branches panel has no branch chosen, which alone is marked as a favourite.</summary>
    public const string NoBranchChosen = "No branch is chosen";

    /// <summary>The branches panel has no local branch chosen, which alone Update brings up to date.</summary>
    public const string NoLocalBranchChosen = "No local branch is chosen";

    /// <summary>A local branch to update that tracks no remote's branch to bring it up to.</summary>
    public static string Untracked(string branch) => $"{branch} tracks no remote's branch to update from";

    /// <summary>A remote's branch, which a fetch brings up to date where Update brings a local branch.</summary>
    public static string FetchedNotUpdated(string branch) => $"{branch} is a remote's branch, which Git › Fetch brings up to date";

    /// <summary>The branch checked out, which git deletes only once another is.</summary>
    public static string CheckedOut(string branch) => $"{branch} is checked out";

    /// <summary>A file shown read-only (a commit's side of a diff), which nothing edits or saves.</summary>
    public static string ReadOnly(string name) => $"{name} is read-only";

    /// <summary>A merge's own sides, which it takes changes from into the result between them.</summary>
    public const string MergeSideReadOnly = "A merge's sides are read-only: edit the result between them";
}
