using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Velopack;
using Velopack.Sources;

namespace ImageArchiver.Wpf;

public partial class AboutWindow : Window
{
    // Public GitHub repo whose releases BuildRelease.ps1 uploads to
    private const string RepoUrl = "https://github.com/kreaton/ImageArchiver";

    private readonly UpdateManager _updateManager = new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));

    public AboutWindow()
    {
        InitializeComponent();

        // An .ico holds several sizes; take the largest so the icon stays sharp at 64 px
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/app.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        AppIcon.Source = decoder.Frames.MaxBy(f => f.PixelWidth);

        VersionText.Text = $"Version {typeof(AboutWindow).Assembly.GetName().Version?.ToString(3)}";
        RepoLink.NavigateUri = new Uri(RepoUrl);
        RepoLinkText.Text = RepoUrl.Replace("https://", "");
    }

    private void OnRepoLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        if (!_updateManager.IsInstalled)
        {
            MessageBox.Show(this, "Updates are only available in the installed app.", "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        UpdateButton.IsEnabled = false;
        try
        {
            var newVersion = await _updateManager.CheckForUpdatesAsync();
            if (newVersion is null)
            {
                MessageBox.Show(this, "You have the latest version.", "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(this,
                $"Version {newVersion.TargetFullRelease.Version} is available. Download and install it now?",
                "Check for updates", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
                return;

            await _updateManager.DownloadUpdatesAsync(newVersion);
            _updateManager.ApplyUpdatesAndRestart(newVersion);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Update failed: {ex.Message}", "Check for updates", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }
}
