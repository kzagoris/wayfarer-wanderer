using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;
using WayfarerWanderer.App.Shell;

namespace WayfarerWanderer.App;

/// <summary>The trust banner over an untrusted folder: a lock, what stays off until the folder is trusted, and the one
/// action that trusts it.</summary>
public sealed partial class EditorWindow
{
    public Button TrustWorkspaceButton { get; } = new()
    {
        Content = "Trust Workspace",
        Classes = { "primary" },
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Border TrustWorkspaceBanner()
    {
        TrustWorkspaceButton.Click += (_, _) => UnawaitedWork.Observe(TrustWorkspaceAsync());

        PathIcon lockIcon = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        lockIcon.Bind(PathIcon.DataProperty, lockIcon.GetResourceObservable(IconKeys.Lock));
        lockIcon.Bind(PathIcon.ForegroundProperty, lockIcon.GetResourceObservable(TokenKeys.Accent));

        TextBlock title = new() { Text = "Restricted mode", FontWeight = FontWeight.SemiBold };
        TextBlock detail = new()
        {
            Text = "Build scripts and code in this folder won't run until you trust it. Trust only folders whose source you know.",
            TextWrapping = TextWrapping.Wrap,
        };
        detail.Bind(TextBlock.ForegroundProperty, detail.GetResourceObservable(TokenKeys.TextMuted));

        Grid layout = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 10,
            Children =
            {
                lockIcon,
                new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { title, detail } },
                TrustWorkspaceButton,
            },
        };
        Grid.SetColumn(layout.Children[1], 1);
        Grid.SetColumn(TrustWorkspaceButton, 2);

        Border banner = new()
        {
            IsVisible = false,
            Padding = new Thickness(12, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = layout,
        };
        banner.Bind(Border.BackgroundProperty, banner.GetResourceObservable(TokenKeys.Banner));
        banner.Bind(Border.BorderBrushProperty, banner.GetResourceObservable(TokenKeys.Separator));
        return banner;
    }
}
