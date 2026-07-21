using System;
using System.Windows;

namespace Obstruo.Installer
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
        }

        private void LoadingComplete(object sender, EventArgs e)
        {
            var installer = new InstallerWindow();
            installer.Show();
            Close();
        }
    }
}