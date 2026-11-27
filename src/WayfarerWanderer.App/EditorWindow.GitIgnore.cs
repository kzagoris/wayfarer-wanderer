// From Passenger 494009d7 src/Passenger.App/MainWindow.GitIgnore.cs, MainWindow.RepositoryFiles.cs and MainWindow.Vcs.cs
// (IgnoreAsync), MIT (notices/Passenger/LICENSE): written without Passenger's command history and Local History, and the
// repository files made from Wayfarer's own texts.
using Passenger.Core;
using Passenger.Text;
using Passenger.Vcs;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Vcs;
using WayfarerWanderer.App.Shell.Workspace;
using A = WayfarerWanderer.App.Shell.AppActions;

namespace WayfarerWanderer.App;

/// <summary>A file or folder added to the repository's .gitignore from the Explorer or the file shown, as the Commit window
/// adds an unversioned one; the ignore file's line deciding whether git ignores one, as git answers, gone to; and a
/// folder's .editorconfig, .gitignore or .gitattributes made from Wayfarer's one text of it (<see cref="RepositoryFiles"/>),
/// or the one there opened.</summary>
public sealed partial class EditorWindow
{
    private const string NoIgnoreTarget = "Choose a file or folder in the Project tool window, or open a file";

    private void AddGitIgnoreActions()
    {
        Actions.Registry.Add(new IdeAction(A.AddToGitIgnore, "Add to .gitignore", "Git", c => IdeErrors.Observe(IgnoreAsync(IgnoreTarget(c)!)))
        {
            WhyNot = c => WhyNotIgnoreTarget(c) ?? (Unversioned(IgnoreTarget(c)!) ? null : Refusals.NotUnversioned),
        });
        Actions.Registry.Add(new IdeAction(A.GoToIgnoringLine, "Go to Ignoring Line", "Git", c => IdeErrors.Observe(GoToIgnoringLineAsync(IgnoreTarget(c)!)))
        {
            WhyNot = WhyNotIgnoreTarget,
            Words = ["Why ignored", "check-ignore", ".gitignore"],
        });
        foreach ((string id, RepositoryFile file) in (ReadOnlySpan<(string, RepositoryFile)>)[(A.NewEditorConfig, RepositoryFile.EditorConfig),
            (A.NewGitIgnore, RepositoryFile.GitIgnore), (A.NewGitAttributes, RepositoryFile.GitAttributes)])
        {
            string name = RepositoryFiles.NameOf(file);
            Actions.Registry.Add(new IdeAction(id, $"{name} File", ExplorerGroup, c => AddRepositoryFile(RowOf(c)!.Into!, file))
            {
                WhyNot = c => RowOf(c) is not { } row ? NoRow : row.Into is null ? $"Nothing new goes in {row.Name}" : null,
                TitleIn = c => RowOf(c)?.Into is { } into && File.Exists(Path.Combine(into, name)) ? $"Open {name}" : $"{name} File",
            });
        }

        // Unversioned as the Commit window lists it: the path itself, or a file under the folder.
        bool Unversioned(string path) => Vcs.Changes.Any(c => c.Kind == ChangeKind.Unversioned && (PathNames.SameFile(c.Path, path) || PathNames.IsUnder(c.Path, path)));
    }

    /// <summary>What Add to .gitignore and Go to Ignoring Line act on: the Explorer's chosen row while its menu asks or its
    /// rows have the keys, else the file shown.</summary>
    private string? IgnoreTarget(ActionContext context) => InExplorer(context) ? RowOf(context)?.Path : Editor.IsVisible ? SourcePath : null;

    private string? WhyNotIgnoreTarget(ActionContext context) =>
        IgnoreTarget(context) is not { } path ? NoIgnoreTarget
        : Vcs.Repository is not { } git ? Vcs.NoRepository
        : git.IsInside(path) && !PathNames.SameFile(path, git.Root) ? null
        : $"{Path.GetFileName(path)} is not in the repository's working tree";

    /// <summary>Add to .gitignore: a line of the repository's top .gitignore matching that file or folder alone (<see
    /// cref="Git.IgnoreLine"/>), which keeps the file's encoding and line breaks; refused while the .gitignore is open with
    /// edits not saved.</summary>
    private async Task IgnoreAsync(string path)
    {
        if (Vcs.Repository is not { } repository)
        {
            return;
        }
        string ignore = Path.Join(repository.Root, Git.IgnoreFileName);
        string name = repository.Relative(path).TrimEnd('/');
        if (repository.IgnoreLine(path, Directory.Exists(path)) is not { } line)
        {
            StatusText = $".gitignore was not changed: {name} holds a line break, which no line of it can";
            return;
        }
        if (documents.Values.FirstOrDefault(d => PathNames.SameFile(d.Path, ignore)) is { IsModified: true })
        {
            StatusText = ".gitignore was not changed: it is open with edits not saved";
            return;
        }
        try
        {
            if (!File.Exists(ignore))
            {
                FileText.SaveNew(ignore, line + "\n");
            }
            else if (FileText.Load(ignore) is not { IsBinary: false } file)
            {
                StatusText = ".gitignore was not changed: it is not a text file";
                return;
            }
            else
            {
                FileText.SaveOver(ignore, file, file.Text + (file.Text.Length > 0 && !file.Text.EndsWith('\n') ? file.Break : string.Empty) + line + file.Break);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText = $".gitignore could not be written: {error.Message}";
            return;
        }
        StatusText = $"Added {name} to .gitignore";
        await Vcs.RefreshAsync();
    }

    /// <summary>Go to Ignoring Line: the ignore file's line git says decides a path opened at that line, and what it decides
    /// said: ignored, kept by a <c>!</c> line, matched though tracked, or matched by no line.</summary>
    public async Task GoToIgnoringLineAsync(string path)
    {
        if (Vcs.Repository is not { } git)
        {
            return;
        }
        string root = Session?.Root ?? git.Root;
        string name = PathNames.Shown(path, root);
        StatusText = $"Asking git which line decides {name}…";
        IgnoringLine? line = await git.IgnoringAsync(path);
        string said = line is null ? $"Git ignores nothing of {name}: no line of an ignore file matches it"
            : $"{name} {(line.Tracked ? "matches" : line.Ignored ? "is ignored by" : "is kept by")} line {line.Line} of {PathNames.Shown(line.File, root)} ({line.Pattern})"
                + (line.Tracked ? ", but git tracks it, so it is not ignored" : string.Empty);
        if (line is not null && File.Exists(line.File))
        {
            NavigateTo(line.File, new TextPosition(line.Line - 1, 0));
        }
        StatusText = said;
    }

    /// <summary>A folder's .editorconfig, .gitignore or .gitattributes made and opened, or the one there opened; what it came
    /// to said.</summary>
    public void AddRepositoryFile(string folder, RepositoryFile file)
    {
        string name = RepositoryFiles.NameOf(file);
        string path = Path.Combine(folder, name);
        string shown = PathNames.Shown(folder, Session?.Root);
        if (File.Exists(path))
        {
            OpenFromExplorer(path);
            Explorer.Reveal(path);
            StatusText = $"{name} is already in {shown}: opened it";
            return;
        }
        StatusText = CreateFile(folder, name, RepositoryFiles.TextOf(file)) is { } why ? $"{name} was not made: {why}" : $"New {name} in {shown}";
    }
}
