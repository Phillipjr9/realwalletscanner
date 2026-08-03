using System;
using System.Windows;

namespace WalletRecoveryTool
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
        }

        public void UpdateStatus(string message)
        {
            Dispatcher.Invoke(() => StatusLabel.Text = message);
        }
    }
}
