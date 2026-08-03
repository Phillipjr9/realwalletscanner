using System.Windows;
using System.Windows.Input;
using WalletRecoveryTool.Core;

namespace WalletRecoveryTool
{
    public partial class PasswordWindow : Window
    {
        public bool IsSetupMode { get; set; }

        public PasswordWindow(bool setupMode = false)
        {
            InitializeComponent();
            IsSetupMode = setupMode;

            if (IsSetupMode)
            {
                Title = "Setup Password";
                MessageTextBlock.Text = "Create a password to protect this application";
                OkButton.Content = "CREATE";
                SetupPanel.Visibility = Visibility.Visible;
            }

            PasswordBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            ErrorTextBlock.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(PasswordBox.Password))
            {
                ErrorTextBlock.Text = "Password cannot be empty";
                ErrorTextBlock.Visibility = Visibility.Visible;
                return;
            }

            if (IsSetupMode)
            {
                // Setup new password
                if (PasswordBox.Password != ConfirmPasswordBox.Password)
                {
                    ErrorTextBlock.Text = "Passwords do not match";
                    ErrorTextBlock.Visibility = Visibility.Visible;
                    return;
                }

                if (PasswordBox.Password.Length < 8)
                {
                    ErrorTextBlock.Text = "Password must be at least 8 characters";
                    ErrorTextBlock.Visibility = Visibility.Visible;
                    return;
                }

                if (SecurityManager.SetupPassword(PasswordBox.Password))
                {
                    MessageBox.Show(
                        "Password protection enabled!\n\n" +
                        "You will need this password to access the application.\n" +
                        "⚠️ There is NO recovery option if you forget it!",
                        "Password Created",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    ErrorTextBlock.Text = "Failed to setup password. Please try again.";
                    ErrorTextBlock.Visibility = Visibility.Visible;
                }
            }
            else
            {
                // Verify password
                if (SecurityManager.UnlockApplication(PasswordBox.Password))
                {
                    DialogResult = true;
                    Close();
                }
                else
                {
                    ErrorTextBlock.Text = "❌ Incorrect password. Try again.";
                    ErrorTextBlock.Visibility = Visibility.Visible;
                    PasswordBox.Clear();
                    PasswordBox.Focus();
                }
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OkButton_Click(sender, e);
            }
        }
    }
}
