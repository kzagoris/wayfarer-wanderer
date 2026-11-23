// From Passenger 8a1df7e4 src/Passenger.App/Explorer/FileIcons.cs, MIT (notices/Passenger/LICENSE): the solution and project
// kinds cut, a JVM module's folder drawn filled in their place.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WayfarerWanderer.App.Shell.Explorer;

/// <summary>The icon and tint a file or folder is drawn with: <see cref="FileIconSet"/>'s shape and colour for its name, a
/// folder the set draws nothing apart for as a folder, and a module's folder as a filled one, so it stands out from the
/// folders it holds.</summary>
public static class FileIcons
{
    public static Geometry For(ExplorerNode node) => Look(node) is { } look
        ? Find<Geometry>(Shaped(look))
        : Find<Geometry>(node.JvmFolder == JvmFolder.Module
            ? node.IsExpanded ? IconKeys.SolutionFolderOpen : IconKeys.SolutionFolder
            : node.IsExpanded ? IconKeys.FolderOpen : IconKeys.Explorer);

    public static IBrush BrushFor(ExplorerNode node) => Look(node) is { } look
        ? Tint(look)
        : Find<IBrush>(node.JvmFolder == JvmFolder.Module ? TokenKeys.File_SolutionFolder : TokenKeys.File_Folder);

    /// <summary>The resource of a shape of the file icons.</summary>
    private static string Shaped(FileIconLook look) => $"FileIcon.{look.Shape}";

    /// <summary>A folder's icon by its name, as the Explorer draws a folder on disk.</summary>
    public static Geometry ForFolder(string name, bool open) => FileIconSet.ForFolder(name) is { } look
        ? Find<Geometry>(Shaped(look))
        : Find<Geometry>(open ? IconKeys.FolderOpen : IconKeys.Explorer);

    public static IBrush BrushForFolder(string name) => FileIconSet.ForFolder(name) is { } look ? Tint(look) : Find<IBrush>(TokenKeys.File_Folder);

    public static Geometry ForFile(string path) => Find<Geometry>(Shaped(FileIconSet.ForFile(Path.GetFileName(path))));

    public static IBrush BrushForFile(string path) => Tint(FileIconSet.ForFile(Path.GetFileName(path)));

    /// <summary>A row's look in the set, or null for what it draws as a folder: a plain folder or a module's.</summary>
    private static FileIconLook? Look(ExplorerNode node) => node.Kind switch
    {
        NodeKind.Folder => node.JvmFolder == JvmFolder.Module ? null : FileIconSet.ForFolder(node.Name),
        _ => FileIconSet.ForFile(node.Name),
    };

    private static IBrush Tint(FileIconLook look) => Find<IBrush>(look.Colour is { } colour ? $"Passenger.FileIcon.{colour}" : TokenKeys.File_Plain);

    private static T Find<T>(string key) where T : class =>
        Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out object? value) && value is T found
            ? found
            : throw new InvalidOperationException($"The theme has no {key}.");
}
