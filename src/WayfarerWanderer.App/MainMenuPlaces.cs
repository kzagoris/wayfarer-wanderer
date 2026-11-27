using WayfarerWanderer.App.Shell;
using EditorKeys = Passenger.Editor.EditorActions;

namespace WayfarerWanderer.App;

/// <summary>Where the main menu lists each of Wayfarer's actions, in one table: the registry gives an action its places
/// as it is added (<see cref="ActionRegistry.Places"/>), so the menus, Settings' Keymap page ("Main Menu | File") and
/// any later action read them alike. The menus follow IntelliJ IDEA's: File, Edit, View, Navigate, Code, Refactor, Build, Run,
/// Tests, Tools, Git and Help, each a run of groups split by separators.</summary>
public static class MainMenuPlaces
{
    private static readonly Dictionary<string, MenuPlace[]> Table = new(StringComparer.Ordinal)
    {
        // File: making and opening projects (Recent Projects stands at 0, 4), the file, the settings, leaving.
        [EditorActions.NewProject] = [new("File", 0, 0)],
        [EditorActions.OpenWorkspace] = [new("File", 0, 1)],
        [EditorActions.OpenSource] = [new("File", 0, 2)],
        [AppActions.CloneRepository] = [new("File", 0, 3), new("Git", 3, 2)],
        [AppActions.CloseSolution] = [new("File", 0, 5)],
        [EditorActions.SaveSource] = [new("File", 1, 0)],
        // The file's properties, as the status bar's widgets: File Encoding, Line Separators (1, 2) and File Indentation.
        [AppActions.FileEncoding] = [new("File", 1, 1)],
        [AppActions.FileIndentation] = [new("File", 1, 3)],
        [AppActions.LineSeparatorsCrlf] = [new("File/Line Separators", 0, 0)],
        [AppActions.LineSeparatorsLf] = [new("File/Line Separators", 0, 1)],
        [AppActions.LineSeparatorsCr] = [new("File/Line Separators", 0, 2)],
        [AppActions.ShowSettings] = [new("File", 2, 0)],
        [EditorActions.ProjectStructure] = [new("File", 2, 1)],
        [AppActions.Exit] = [new("File", 3, 0)],

        // Edit: the editor's, or a text box's where one has the keys.
        [EditorKeys.Undo] = [new("Edit", 0, 0)],
        [EditorKeys.Redo] = [new("Edit", 0, 1)],
        [EditorKeys.Cut] = [new("Edit", 1, 0)],
        [EditorKeys.Copy] = [new("Edit", 1, 1)],
        [EditorKeys.Paste] = [new("Edit", 1, 2)],
        [EditorKeys.SelectAll] = [new("Edit", 2, 0)],
        [AppActions.FindInFiles] = [new("Edit", 3, 0)],
        [AppActions.ReplaceInFiles] = [new("Edit", 3, 1)],

        // View: Tool Windows (0, 0) lists the dock's windows as it opens.
        [AppActions.ReadAsText] = [new("View", 1, 0)],
        [AppActions.QuickDocumentation] = [new("View", 1, 1)],
        // View's Active Editor (2, 0): the file shown's wrapping and whitespace, the editor's font size.
        [AppActions.SoftWrap] = [new("View/Active Editor", 0, 0)],
        [AppActions.ShowWhitespaces] = [new("View/Active Editor", 0, 1)],
        [AppActions.IncreaseFontSize] = [new("View/Active Editor", 1, 0)],
        [AppActions.DecreaseFontSize] = [new("View/Active Editor", 1, 1)],
        [AppActions.ResetFontSize] = [new("View/Active Editor", 1, 2)],
        // View's tool windows (3) and Appearance (4, 0): the whole window's scale, Full Screen.
        [AppActions.HideSideWindows] = [new("View", 3, 0)],
        [AppActions.LastToolWindow] = [new("View", 3, 1)],
        [AppActions.CloseToolTab] = [new("View", 3, 2)],
        [AppActions.FullScreen] = [new("View/Appearance", 0, 0)],
        [AppActions.ZoomIn] = [new("View/Appearance", 1, 0)],
        [AppActions.ZoomOut] = [new("View/Appearance", 1, 1)],
        [AppActions.ResetZoom] = [new("View/Appearance", 1, 2)],

        // Navigate: Back and Forward, the Go to popups, the recent files.
        [AppActions.Back] = [new("Navigate", 0, 0)],
        [AppActions.Forward] = [new("Navigate", 0, 1)],
        [AppActions.GoToEverything] = [new("Navigate", 1, 0)],
        [AppActions.GoToFile] = [new("Navigate", 1, 1)],
        [AppActions.GoToSymbol] = [new("Navigate", 1, 2)],
        [AppActions.GoToLine] = [new("Navigate", 1, 3)],
        [AppActions.FileStructure] = [new("Navigate", 1, 4)],
        [AppActions.JumpToNavigationBar] = [new("Navigate", 1, 5)],
        [AppActions.GoToDeclaration] = [new("Navigate", 2, 0)],
        [AppActions.RecentFiles] = [new("Navigate", 3, 0)],
        [AppActions.RecentChangedFiles] = [new("Navigate", 3, 1)],

        // Code and Refactor: the language servers'.
        [EditorWindow.CompleteAction] = [new("Code", 0, 0)],
        // Code's Folding (3, 0), its levels' submenus at (0, 3) and (0, 4).
        [EditorKeys.Unfold] = [new("Code/Folding", 0, 0)],
        [EditorKeys.UnfoldAll] = [new("Code/Folding", 0, 1)],
        [EditorKeys.UnfoldRecursively] = [new("Code/Folding", 0, 2)],
        [EditorKeys.Fold] = [new("Code/Folding", 1, 0)],
        [EditorKeys.FoldToDefinitions] = [new("Code/Folding", 1, 1)],
        [EditorKeys.FoldAll] = [new("Code/Folding", 1, 2)],
        [EditorKeys.FoldRecursively] = [new("Code/Folding", 1, 3)],
        [EditorKeys.UnfoldDocComments] = [new("Code/Folding", 2, 0)],
        [EditorKeys.FoldDocComments] = [new("Code/Folding", 2, 1)],
        [EditorKeys.ToggleFold] = [new("Code/Folding", 3, 0)],
        [EditorKeys.UnfoldToLevel1] = [new("Code/Folding/Expand to Level", 0, 0)],
        [EditorKeys.UnfoldToLevel2] = [new("Code/Folding/Expand to Level", 0, 1)],
        [EditorKeys.UnfoldToLevel3] = [new("Code/Folding/Expand to Level", 0, 2)],
        [EditorKeys.UnfoldToLevel4] = [new("Code/Folding/Expand to Level", 0, 3)],
        [EditorKeys.UnfoldToLevel5] = [new("Code/Folding/Expand to Level", 0, 4)],
        [EditorKeys.UnfoldAllToLevel1] = [new("Code/Folding/Expand All to Level", 0, 0)],
        [EditorKeys.UnfoldAllToLevel2] = [new("Code/Folding/Expand All to Level", 0, 1)],
        [EditorKeys.UnfoldAllToLevel3] = [new("Code/Folding/Expand All to Level", 0, 2)],
        [EditorKeys.UnfoldAllToLevel4] = [new("Code/Folding/Expand All to Level", 0, 3)],
        [EditorKeys.UnfoldAllToLevel5] = [new("Code/Folding/Expand All to Level", 0, 4)],
        [EditorWindow.RenameAction] = [new("Refactor", 0, 0)],
        [EditorWindow.UndoRenameAction] = [new("Refactor", 0, 1)],

        [EditorActions.Build] = [new("Build", 0, 0)],

        // Run: starting and stopping, stepping, breakpoints, the run's JDK.
        [EditorActions.Run] = [new("Run", 0, 0)],
        [EditorActions.Debug] = [new("Run", 0, 1)],
        [EditorActions.Stop] = [new("Run", 0, 2)],
        [EditorActions.Rerun] = [new("Run", 0, 3)],
        [EditorActions.Continue] = [new("Run", 1, 0)],
        [EditorActions.Pause] = [new("Run", 1, 1)],
        [EditorActions.StepOver] = [new("Run", 1, 2)],
        [EditorActions.StepInto] = [new("Run", 1, 3)],
        [EditorActions.StepOut] = [new("Run", 1, 4)],
        [AppActions.Evaluate] = [new("Run", 2, 0)],
        [AppActions.QuickEvaluate] = [new("Run", 2, 1)],
        [AppActions.AddToWatches] = [new("Run", 2, 2)],
        [EditorActions.ToggleBreakpoint] = [new("Run", 3, 0)],
        [EditorActions.ChooseRunJdk] = [new("Run", 4, 0)],

        [EditorActions.RunTests] = [new("Tests", 0, 0)],

        [AppActions.NewTerminalSession] = [new("Tools", 0, 0)],

        // Git: sharing, shelving, the file's changes, the repository itself.
        [AppActions.Commit] = [new("Git", 0, 0)],
        [AppActions.Push] = [new("Git", 0, 1)],
        [AppActions.UpdateProject] = [new("Git", 0, 2)],
        [AppActions.Fetch] = [new("Git", 0, 3)],
        [AppActions.Branches] = [new("Git", 0, 4)],
        [AppActions.Stash] = [new("Git", 1, 0)],
        [AppActions.Unstash] = [new("Git", 1, 1)],
        [AppActions.ShowDiff] = [new("Git", 2, 0)],
        [AppActions.Annotate] = [new("Git", 2, 1)],
        [AppActions.RollbackLines] = [new("Git", 2, 2)],
        [AppActions.NextChange] = [new("Git", 2, 3)],
        [AppActions.PreviousChange] = [new("Git", 2, 4)],
        [AppActions.ResolveConflicts] = [new("Git", 2, 5)],
        [AppActions.Stage] = [new("Git", 2, 6)],
        [AppActions.Unstage] = [new("Git", 2, 7)],
        [AppActions.AddToGitIgnore] = [new("Git", 2, 8)],
        [AppActions.GoToIgnoringLine] = [new("Git", 2, 9)],
        [AppActions.CreateRepository] = [new("Git", 3, 0)],
        [AppActions.CommitSigning] = [new("Git", 3, 1)],

        [AppActions.CheckForUpdates] = [new("Help", 1, 0)],
        [AppActions.RestartToUpdate] = [new("Help", 1, 1)],
        [AppActions.FindAction] = [new("Help", 0, 0)],
    };

    /// <summary>Where the main menu lists an action; nowhere for one the table does not name.</summary>
    public static IReadOnlyList<MenuPlace> Of(string id) => Table.TryGetValue(id, out MenuPlace[]? places) ? places : [];
}
