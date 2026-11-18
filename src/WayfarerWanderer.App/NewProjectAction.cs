using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.Core;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

public sealed class NewProjectAction
{
    public async Task<ProjectGenerationResult?> RunAsync(EditorWindow owner, CancellationToken cancellationToken = default)
    {
        ProjectOptionsWindow window = new();
        ProjectGenerationOptions? options = await window.ShowDialog<ProjectGenerationOptions?>(owner);
        if (options is null)
        {
            return null;
        }
        ProjectGenerationResult result = await ProjectGenerator.GenerateAsync(options, cancellationToken);
        await owner.OpenWorkspaceAsync(result.Destination, cancellationToken);
        return result;
    }

    private sealed class ProjectOptionsWindow : Window
    {
        private readonly TextBox projectName = new() { PlaceholderText = "Project name" };
        private readonly TextBox destination = new() { PlaceholderText = "Destination" };
        private readonly TextBox group = new() { Text = "com.example", PlaceholderText = "Group" };
        private readonly TextBox artifact = new() { Text = "wanderer", PlaceholderText = "Artifact" };
        private readonly TextBox package = new() { Text = "com.example.wanderer", PlaceholderText = "Package" };
        private readonly TextBox jdk = new()
        {
            Text = Environment.GetEnvironmentVariable("JAVA_HOME") ?? "",
            PlaceholderText = "JDK home"
        };
        private readonly ComboBox kind = Choices(Enum.GetNames<NewProjectKind>());
        private readonly ComboBox language = Choices(Enum.GetNames<NewProjectLanguage>());
        private readonly ComboBox build = Choices(Enum.GetNames<NewProjectBuild>());
        private readonly TextBlock error = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        private readonly Button browseJdk = new() { Content = "Browse JDK" };
        private readonly Button browseDestination = new() { Content = "Browse" };
        private readonly Button create = new() { Content = "Create" };
        private readonly Button cancel = new() { Content = "Cancel" };

        public ProjectOptionsWindow()
        {
            Title = "New Project";
            Width = 560;
            Height = 540;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            browseJdk.Click += (_, _) => UnawaitedWork.Observe(BrowseAsync(jdk));
            browseDestination.Click += (_, _) => UnawaitedWork.Observe(BrowseDestinationAsync());
            create.Click += (_, _) => Create();
            cancel.Click += (_, _) => Close(null);
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 8,
                Children =
                {
                    Field("Project", projectName),
                    Field("Kind", kind),
                    Field("Language", language),
                    Field("Build", build),
                    Field("Destination", Browse(destination, browseDestination)),
                    Field("Group", group),
                    Field("Artifact", artifact),
                    Field("Package", package),
                    Field("JDK", Browse(jdk, browseJdk)),
                    error,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, create }
                    }
                }
            };
        }

        private void Create()
        {
            try
            {
                ProjectGenerationOptions options = new()
                {
                    ProjectName = projectName.Text ?? "",
                    Destination = destination.Text ?? "",
                    GroupId = group.Text ?? "",
                    ArtifactId = artifact.Text ?? "",
                    PackageName = package.Text ?? "",
                    JdkHome = jdk.Text ?? "",
                    Kind = (NewProjectKind)(kind.SelectedIndex < 0 ? 0 : kind.SelectedIndex),
                    Language = (NewProjectLanguage)(language.SelectedIndex < 0 ? 0 : language.SelectedIndex),
                    Build = (NewProjectBuild)(build.SelectedIndex < 0 ? 0 : build.SelectedIndex),
                    LaunchName = "Run " + projectName.Text
                };
                Close(ProjectGenerator.Validate(options));
            }
            catch (ProjectGenerationException failure)
            {
                error.Text = failure.Message;
            }
        }

        private async Task BrowseAsync(TextBox target)
        {
            IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
                new Avalonia.Platform.Storage.FolderPickerOpenOptions { AllowMultiple = false });
            if (folders.Count > 0)
            {
                target.Text = folders[0].Path.LocalPath;
            }
        }

        private async Task BrowseDestinationAsync()
        {
            IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
                new Avalonia.Platform.Storage.FolderPickerOpenOptions { AllowMultiple = false });
            if (folders.Count > 0)
            {
                string name = string.IsNullOrWhiteSpace(projectName.Text) ? "project" : projectName.Text.Trim();
                destination.Text = Path.Combine(folders[0].Path.LocalPath, name.Replace(' ', '-').ToLowerInvariant());
            }
        }

        private static ComboBox Choices(string[] values) =>
            new() { ItemsSource = values, SelectedIndex = 0 };

        private static Control Browse(TextBox field, Button button) =>
            new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("1* Auto"),
                Children =
                {
                    FieldAt(field, 0),
                    FieldAt(button, 1)
                }
            };

        private static Control FieldAt(Control control, int column)
        {
            Grid.SetColumn(control, column);
            return control;
        }

        private static Control Field(string label, Control editor) =>
            new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = label },
                    editor
                }
            };
    }
}
