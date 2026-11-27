// From Passenger 494009d7 src/Passenger.App/Refusals.cs, MIT (notices/Passenger/LICENSE): the refusals of what Wayfarer
// has (Git's views and commands, the search, the editor's files, the tool windows, the Welcome screen's list); .NET's,
// Godot's, Aspire's, the debugger's, the agents' and the other refusals of what Wayfarer cut, cut with them.
using System.Globalization;
using Passenger.Core;
using Passenger.Text;

namespace WayfarerWanderer.App.Shell;

/// <summary>Why an action cannot run here, in the words every place that refuses it uses: the action's reason (its menu
/// item's tooltip, Find Action's row) and the status bar's.</summary>
public static class Refusals
{
    /// <summary>The folder open is not trusted, so Wayfarer runs none of its code (<see cref="WorkspaceTrust"/>).</summary>
    public static string Untrusted => WorkspaceTrust.Untrusted;

    /// <summary>The window has no folder open.</summary>
    public const string NoFolder = "No folder is open";

    /// <summary>The editor shows no file.</summary>
    public const string NoFile = "No file is open";

    /// <summary>A regex stopped at its time limit, wherever that cuts an answer short (<see cref="TextSearch.RegexStopped"/>).</summary>
    public const string RegexStopped = TextSearch.RegexStopped;

    /// <summary><see cref="RegexStopped"/> naming where it stopped: a file, or what it was matching.</summary>
    public static string RegexStoppedIn(string where) => TextSearch.RegexStoppedIn(where);

    /// <summary>Why no match of a search is replaced: replacing every one would make the text longer than a replacement
    /// may (<see cref="SearchResult.TooLong"/>), worked out before any is made; null when it would not.</summary>
    public static string? TooLongReplaced(SearchResult found, string replacement) =>
        found.ReplacedLength(replacement) is var length && found.TooLong(length)
            ? string.Create(CultureInfo.InvariantCulture, $"the text replaced would be {length:N0} characters long, more than {SearchResult.MostReplaced:N0}")
            : null;

    /// <summary>Read as Text's view, the keys in it.</summary>
    public const string OnlyToRead = "The text is only to read: Escape goes back to where you were";

    /// <summary>Read as Text where the keys are in nothing it reads.</summary>
    public const string NothingToRead = "Nothing here to read as text: put the keys in the editor, the terminal, a diff or the status bar";

    /// <summary>Read as Text in the editor where its language server says nothing of what the caret is on.</summary>
    public const string NothingAtCaret = "Nothing at the caret to read as text: its language server says nothing of it";

    /// <summary>Read as Text asked again in its own view.</summary>
    public const string ReadingAlready = "Its text is open: Escape goes back to where you were";

    /// <summary>No file is open and no tool window serves the folder, for the Switcher.</summary>
    public const string NothingToSwitchTo = "No file or tool window is open to switch to";

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

    /// <summary>A file of a language tabs can't indent, which a conversion to them would break.</summary>
    public static string SpacesAlone(string name, string language) => $"{language} takes no tabs in its indentation, so {name} would no longer read as {language}";

    /// <summary>A file of a language whose blocks are its indentation, a line of which a conversion could move to another block
    /// or leave the file refused.</summary>
    public static string BlockAtRisk(string name, string language, int line) =>
        $"Converted, line {line} of {name} could read as another block, as {language} counts tabs and spaces apart";

    /// <summary>A file whose grammar has yet to read it whole, so the lines inside its strings are not yet known.</summary>
    public static string ReadingStrings(string name) => $"{name} is still being read, to tell the lines inside its strings";

    /// <summary>A file whose grammar failed as it read it, and stopped, so the lines inside its strings will never be known.</summary>
    public static string GrammarFailed(string name, string language, string why) =>
        $"{Product.Name} can't tell which lines of {name} begin inside a string: its {language} grammar failed reading it ({why})";

    /// <summary>A file in a language with no parse or grammar here, whose lines may begin inside a string that converting
    /// their indentation would change.</summary>
    public static string UnreadStrings(string name) => $"{Product.Name} can't tell which lines of {name} begin inside a string, whose text a conversion would change";

    /// <summary>A merge's own sides, which it takes changes from into the result between them.</summary>
    public const string MergeSideReadOnly = "A merge's sides are read-only: edit the result between them";

    /// <summary>A few lines shown of a file (a usage's, a search's match, a place gone to), not the file itself.</summary>
    public const string PreviewReadOnly = "The preview is read-only: open its file to change it";

    /// <summary>A text box shown to read and copy from (an error's details, a log, a response, a licence).</summary>
    public const string TextReadOnly = "The text is read-only";

    /// <summary>A refusal's clause said alone, as a sentence of its own.</summary>
    public static string Sentence(string clause) => clause.Length == 0 ? clause : char.ToUpperInvariant(clause[0]) + clause[1..];

    /// <summary>Why an edit left files as they were: each was edited after the edit was worked out, so its places no longer
    /// stand where it was made for.</summary>
    public static string EditedMeanwhile(int files) => $"{Plural.Agreeing(files, "it was", "they were")} edited meanwhile";

    /// <summary>What an edit did, and the files it left as they were for being edited meanwhile: "Renamed in 3 files, but not
    /// in A.java and B.java: they were edited meanwhile"; what it did alone where it left none.</summary>
    public static string EditedMeanwhile(string done, IReadOnlyList<string> files) =>
        files.Count == 0 ? done : $"{done}, but not in {Plural.Listed(files)}: {EditedMeanwhile(files.Count)}";

    /// <summary>A changed file's Go to Source or Delete where the change deleted it.</summary>
    public const string FileDeleted = "The file is deleted";

    /// <summary>Add to .gitignore on a file Git tracks, where only an unversioned one is ignored.</summary>
    public const string Tracked = "Git tracks this file: only an unversioned file is added to .gitignore";

    /// <summary>Add to .gitignore on what the Commit window does not list as unversioned: tracked, or ignored already.</summary>
    public const string NotUnversioned = "Git tracks it or ignores it already: only an unversioned file or folder is added to .gitignore";

    /// <summary>Compare with Local on a file the commit deleted.</summary>
    public const string DeletedByCommit = "The commit deleted this file, so it holds none to compare";

    /// <summary>A rebase on a branch while HEAD is detached, with no branch of its own.</summary>
    public const string Detached = "HEAD is detached: no branch is checked out";

    /// <summary>A menu's list (the recent folders) while it lists nothing.</summary>
    public const string NothingListed = "Nothing is listed here yet";

    /// <summary>Copy where no text is selected.</summary>
    public const string NothingSelected = "No text is selected";

    /// <summary>Copy or Select All in a table with no cells.</summary>
    public const string NoCells = "The table has no cells";

    /// <summary>Show in the file manager of a recent folder gone from disk.</summary>
    public const string RecentGone = "It is gone from disk";

    /// <summary>Clear Filters while the list is shown whole as at first.</summary>
    public const string NotNarrowed = "The list is neither narrowed nor ordered otherwise";

    /// <summary>No tool window's view has had the keys yet, for Jump to Last Tool Window.</summary>
    public const string NoToolWindowHadKeys = "No tool window has had the keys yet";

    /// <summary>What has the keys holds no tabs, for Close Tool Window Tab.</summary>
    public const string NoToolTabs = "No tool window with tabs has the keys";

    /// <summary>The tab shown has no ×, for Close Tool Window Tab.</summary>
    public const string ToolTabStays = "The tab shown has no close: it stays open";

    /// <summary>The window's scale is at the end of Settings' Display scale, for Zoom In and Zoom Out.</summary>
    public static string ZoomedAsFarAsItGoes(bool up) => up ? "The window is as large as it goes" : "The window is as small as it goes";

    /// <summary>Maximize of a tool window that floats or is shown over the editors for a moment.</summary>
    public const string Unmaximized = "Only a docked tool window is maximized";
}
