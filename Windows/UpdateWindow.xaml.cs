using System;
using System.Diagnostics;
using System.Windows;
using WalletRecoveryTool.Core;

namespace WalletRecoveryTool
{
    public partial class UpdateWindow : Window
    {
        private UpdateChecker.UpdateInfo _updateInfo;

        public UpdateWindow(UpdateChecker.UpdateInfo updateInfo)
        {
            InitializeComponent();
            _updateInfo = updateInfo;
            LoadUpdateInfo();
        }

        private void LoadUpdateInfo()
        {
            if (_updateInfo == null) return;

            VersionTextBlock.Text = $"Version {_updateInfo.LatestVersion} is now available (you have {_updateInfo.CurrentVersion})";
            ReleaseNotesTextBlock.Text = _updateInfo.ReleaseNotes ?? "No release notes available.";
            PublishDateTextBlock.Text = $"Published: {_updateInfo.PublishedAt:MMMM dd, yyyy}";
        }

        private void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_updateInfo?.DownloadUrl))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _updateInfo.DownloadUrl,
                        UseShellExecute = true
                    });
                    
                    MessageBox.Show(
                        "Your browser will open to download the update.\n\n" +
                        "After downloading, close this application and run the installer.",
                        "Download Started",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to open download link:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            DialogResult = true;
            Close();
        }

        private void RemindLaterButton_Click(object sender, RoutedEventArgs e)
        {
            // Save preference to check again later
            var settings = AppSettings.Load();
            settings.LastUpdateCheck = DateTime.Now;
            settings.Save();

            DialogResult = false;
            Close();
        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            // Save skipped version
            var settings = AppSettings.Load();
            settings.SkippedVersion = _updateInfo?.LatestVersion;
            settings.LastUpdateCheck = DateTime.Now;
            settings.Save();

            MessageBox.Show(
                "You won't be notified about this version again.\n\n" +
                "You can manually check for updates from the About menu.",
                "Version Skipped",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            DialogResult = false;
            Close();
        }
    }
}
