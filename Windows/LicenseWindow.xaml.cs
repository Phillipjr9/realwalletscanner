using System;
using System.Windows;
using WalletRecoveryTool.Core;

namespace WalletRecoveryTool
{
    public partial class LicenseWindow : Window
    {
        public bool IsActivated { get; private set; }

        public LicenseWindow()
        {
            InitializeComponent();
            LicenseKeyTextBox.Focus();
            LicenseKeyTextBox.TextChanged += LicenseKeyTextBox_TextChanged;
        }

        private void LicenseKeyTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            StatusText.Text = "";
        }

        private void ActivateButton_Click(object sender, RoutedEventArgs e)
        {
            var key = LicenseKeyTextBox.Text.Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(key))
            {
                StatusText.Text = "Please enter a license key.";
                StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                return;
            }

            if (!LicenseManager.ValidateKey(key))
            {
                StatusText.Text = "Invalid license key. Please check and try again.";
                StatusText.Foreground = System.Windows.Media.Brushes.Red;
                return;
            }

            var (activated, activationMsg) = LicenseManager.ActivateLicense(key);
            if (activated)
            {
                IsActivated = true;
                StatusText.Text = "License activated successfully!";
                StatusText.Foreground = System.Windows.Media.Brushes.LimeGreen;

                // Close after brief delay so the user sees the success message
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                timer.Tick += (s, args) =>
                {
                    timer.Stop();
                    DialogResult = true;
                    Close();
                };
                timer.Start();
            }
            else
            {
                StatusText.Text = activationMsg;
                StatusText.Foreground = System.Windows.Media.Brushes.Red;
            }
        }
    }
}
