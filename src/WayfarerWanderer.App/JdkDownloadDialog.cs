using System.Globalization;
using Avalonia.Controls;
using Passenger.Core;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.Jvm;

namespace WayfarerWanderer.App;

/// <summary>Where JDK downloads come from: one HTTP client for the catalogue and the vendors' hosts, through the system's
/// proxy as .NET's default handler takes it.</summary>
public static class JdkDownloads
{
    private static readonly Lazy<HttpClient> Client = new(() =>
    {
        HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WayfarerWanderer");
        return client;
    });

    public static HttpClient Http => Client.Value;

    public static JdkCatalogue Catalogue() => new(Http);

    public static JdkDownloader Downloader(string? folder = null) => new(Http, Catalogue(), JdkRegistry.Current, folder);
}

/// <summary>
/// Download JDK, as IntelliJ's: a feature release, a vendor (Temurin first) and a folder (<c>~/.jdks</c>), the package
/// the catalogue offers for this machine, then the download with its progress and Cancel. Closes with the JDK
/// registered, or null. Nothing is fetched but the catalogue until the person clicks Download.
/// </summary>
public sealed class JdkDownloadDialog : IdeWindow
{
    private readonly JdkRequirement? wanted;
    private readonly JdkPlatform platform = JdkPlatform.Current();
    private readonly ComboBox version = new() { Classes = { StyleClasses.Form }, MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox vendor = new() { Classes = { StyleClasses.Form }, MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox location = new() { Classes = { StyleClasses.Form }, Text = JdkDownloader.DefaultFolder };
    private readonly TextBlock details = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock problem = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
    private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4, IsVisible = false };
    private readonly Button download = DialogFooter.Default("Download");
    private readonly Button addFromDisk = new() { Content = "Add JDK from Disk…", IsVisible = false };
    private CancellationTokenSource? running;
    private JdkPackage? package;
    private int asked;

    public JdkDownloadDialog(JdkRequirement? wanted = null)
    {
        this.wanted = wanted;
        Title = "Download JDK";
        Width = 600;
        download.IsEnabled = false;
        download.Click += (_, _) => UnawaitedWork.Observe(DownloadAsync());
        addFromDisk.Click += (_, _) => UnawaitedWork.Observe(AddFromDiskAsync());
        version.SelectionChanged += (_, _) => UnawaitedWork.Observe(LookUpAsync());
        vendor.SelectionChanged += (_, _) => UnawaitedWork.Observe(LookUpAsync());
        Button cancel = DialogFooter.Cancel(() =>
        {
            if (running is { } download)
            {
                download.Cancel();
            }
            else
            {
                Close(null);
            }
        });
        StackPanel form = new()
        {
            Spacing = 8,
            Children =
            {
                FormRows.Of("Version:", version),
                FormRows.Of("Vendor:", vendor),
                FormRows.Of("Location:", location),
                details,
                bar,
                problem,
            },
        };
        FormRows.Scope(form);
        List<Control> paragraphs = [form];
        if (wanted is not null)
        {
            paragraphs.Insert(0, DialogFooter.Paragraph(wanted.Source == JdkRequirement.Tooling
                ? $"Wayfarer's project host needs {wanted.Describe()}."
                : $"This project needs {wanted.Describe()}."));
        }
        DialogFooter.Question(this, "Download JDK", paragraphs, DialogFooter.Of(download, cancel), addFromDisk);
        Opened += (_, _) => UnawaitedWork.Observe(LoadAsync());
    }

    private async Task LoadAsync()
    {
        details.Text = "Asking the JDK catalogue…";
        JdkCatalogue catalogue = JdkDownloads.Catalogue();
        try
        {
            Task<IReadOnlyList<int>> features = catalogue.FeaturesAsync();
            Task<IReadOnlyList<JdkDistribution>> vendors = catalogue.DistributionsAsync();
            List<int> offered = [.. await features];
            if (wanted?.Feature is int feature && !offered.Contains(feature))
            {
                offered.Insert(0, feature);
            }
            string chosenVendor = wanted?.Vendor is { Length: > 0 } v ? JdkRegistry.Distribution(v) : "temurin";
            if (chosenVendor == "jbr")
            {
                chosenVendor = "jetbrains";
            }
            // Native-image builds and JetBrains' IDE runtime are left out, unless the project asks for one.
            List<VendorRow> rows = [.. JdkCatalogue.Offered(await vendors, chosenVendor).Select(d => new VendorRow(d))];
            // Temurin first: free for production use and TCK-tested.
            rows.Sort((a, b) => (b.Distribution.Id == "temurin").CompareTo(a.Distribution.Id == "temurin"));
            version.ItemsSource = offered;
            vendor.ItemsSource = rows;
            vendor.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(VendorRow.Text));
            int chosenFeature = wanted?.Feature ?? (offered.Contains(ToolingJvms.Minimum) ? ToolingJvms.Minimum : offered.FirstOrDefault());
            version.SelectedItem = chosenFeature;
            vendor.SelectedItem = rows.FirstOrDefault(row => row.Distribution.Id == chosenVendor) ?? rows.FirstOrDefault();
        }
        catch (JdkDownloadFailed unreachable)
        {
            Say($"{unreachable.Message} Add a JDK from disk instead, or try again later.");
            details.Text = string.Empty;
            addFromDisk.IsVisible = true;
        }
    }

    /// <summary>The package the catalogue offers for the chosen version and vendor on this machine.</summary>
    private async Task LookUpAsync()
    {
        if (version.SelectedItem is not int feature || vendor.SelectedItem is not VendorRow row)
        {
            return;
        }
        int ask = ++asked;
        package = null;
        download.IsEnabled = false;
        problem.IsVisible = false;
        details.Text = $"Looking for {row.Distribution.Name} {feature}…";
        try
        {
            IReadOnlyList<JdkPackage> packages = await JdkDownloads.Catalogue().PackagesAsync(feature, row.Distribution.Id, platform);
            if (ask != asked)
            {
                return;
            }
            if (packages.FirstOrDefault() is not { } found)
            {
                details.Text = $"{row.Distribution.Name} has no JDK {feature} for {platform.OperatingSystem} {platform.Architecture}. Choose another vendor.";
                return;
            }
            package = found;
            string size = found.Size > 0 ? $", {found.Size / (1024 * 1024)} MB" : string.Empty;
            string licence = found.FreeInProduction ? "Free for production use." : "Check the vendor's licence before using it in production.";
            details.Text = $"{found.FileName} ({found.JavaVersion}{size}). {licence}";
            download.IsEnabled = true;
        }
        catch (JdkDownloadFailed unreachable) when (ask == asked)
        {
            details.Text = string.Empty;
            Say(unreachable.Message);
            addFromDisk.IsVisible = true;
        }
    }

    private async Task DownloadAsync()
    {
        if (package is not { } chosen || string.IsNullOrWhiteSpace(location.Text))
        {
            return;
        }
        running = new CancellationTokenSource();
        download.IsEnabled = false;
        version.IsEnabled = vendor.IsEnabled = location.IsEnabled = false;
        problem.IsVisible = false;
        bar.IsVisible = true;
        bar.Value = 0;
        Progress<JdkDownloadProgress> progress = new(p =>
        {
            bar.IsIndeterminate = p.Total <= 0 || p.Stage != "Downloading";
            bar.Value = p.Total > 0 ? p.Received * 100.0 / p.Total : 0;
            details.Text = p.Stage == "Downloading" && p.Total > 0
                ? string.Create(CultureInfo.CurrentCulture, $"Downloading {chosen.FileName}: {p.Received / (1024 * 1024)} of {p.Total / (1024 * 1024)} MB")
                : $"{p.Stage} {chosen.FileName}…";
        });
        try
        {
            Jdk jdk = await JdkDownloads.Downloader(location.Text).DownloadAsync(chosen, progress, running.Token);
            Close(jdk);
        }
        catch (OperationCanceledException)
        {
            details.Text = "The download was cancelled.";
        }
        catch (JdkDownloadFailed failed)
        {
            Say(failed.Message);
        }
        finally
        {
            running = null;
            bar.IsVisible = false;
            download.IsEnabled = package is not null;
            version.IsEnabled = vendor.IsEnabled = location.IsEnabled = true;
        }
    }

    private async Task AddFromDiskAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add JDK from Disk" });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } home)
        {
            return;
        }
        try
        {
            Close(JdkRegistry.Current.Add(home));
        }
        catch (ArgumentException refused)
        {
            Say(refused.Message);
        }
    }

    private void Say(string text)
    {
        problem.Text = text;
        problem.IsVisible = true;
    }

    private sealed record VendorRow(JdkDistribution Distribution)
    {
        public string Text => Distribution.Name;
    }
}
