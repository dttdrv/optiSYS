using System;
using System.Windows;

namespace OptiSYS.Installer
{
    public partial class App : Application
    {
        public static bool IsUninstallMode { get; private set; }

        private void OnStartup(object sender, StartupEventArgs e)
        {
            foreach (var arg in e.Args)
            {
                if (arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) || 
                    arg.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    IsUninstallMode = true;
                    break;
                }
            }

            var mainWindow = new MainWindow();
            mainWindow.Show();
        }
    }
}
