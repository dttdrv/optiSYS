using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace OptiSYS.Installer
{
    public partial class MainWindow : Window
    {
        private readonly string _installDir;
        private readonly string _exePath;
        private bool _isInstalling;

        public MainWindow()
        {
            InitializeComponent();
            
            // Set paths
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _installDir = Path.Combine(localAppData, "Programs", "optiSYS");
            _exePath = Path.Combine(_installDir, "OptiSYS.exe");
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Initial Fade-in & Slide-up of the Window Content
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            
            var slideUp = new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(600))
            {
                EasingFunction = new PowerEase { EasingMode = EasingMode.EaseOut, Power = 3 }
            };

            WelcomePanel.BeginAnimation(OpacityProperty, fadeIn);
            WelcomeTransform.BeginAnimation(TranslateTransform.YProperty, slideUp);

            if (App.IsUninstallMode)
            {
                ConfigureForUninstall();
            }
        }

        private void ConfigureForUninstall()
        {
            WelcomeTitle.Text = "Uninstall optiSYS";
            WelcomeSubtitle.Text = "Remove optiSYS active memory and runtime optimization suite from your system.";
            InstallButton.Content = "Uninstall";
            
            DesktopShortcutCheck.Visibility = Visibility.Collapsed;
            StartMenuShortcutCheck.Visibility = Visibility.Collapsed;
            StartWithWindowsCheck.Visibility = Visibility.Collapsed;
            
            ProgressTitle.Text = "Uninstalling optiSYS";
            ProgressSub.Text = "Removing files, configurations, and shortcuts...";
            
            FinishedTitle.Text = "Uninstall Complete";
            FinishedSub.Text = "optiSYS has been successfully removed from your computer.";
            LaunchAfterFinishCheck.Visibility = Visibility.Collapsed;
            FinishButton.Content = "Close";
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isInstalling && !App.IsUninstallMode)
            {
                var result = MessageBox.Show(this, "Are you sure you want to cancel the installation?", "optiSYS Setup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.No)
                {
                    return;
                }
            }
            Close();
        }

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            _isInstalling = true;

            // Animate transition to Installing screen (Fade out welcome, slide left)
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
            var slideLeft = new DoubleAnimation(0, -150, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            var welcomeCompleted = new TaskCompletionSource<bool>();
            fadeOut.Completed += (s, ev) => { welcomeCompleted.SetResult(true); };
            
            WelcomePanel.BeginAnimation(OpacityProperty, fadeOut);
            WelcomeTransform.BeginAnimation(TranslateTransform.XProperty, slideLeft);

            await welcomeCompleted.Task;
            WelcomePanel.Visibility = Visibility.Collapsed;

            // Show and animate in the Installing screen
            InstallingPanel.Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
            var slideIn = new DoubleAnimation(150, 0, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            InstallingPanel.BeginAnimation(OpacityProperty, fadeIn);
            InstallingTransform.BeginAnimation(TranslateTransform.XProperty, slideIn);

            if (App.IsUninstallMode)
            {
                await RunUninstallAsync();
            }
            else
            {
                await RunInstallAsync();
            }
        }

        private async Task RunInstallAsync()
        {
            try
            {
                UpdateProgress(10, "Creating installation directory...");
                await Task.Delay(300); // Visual pacing

                if (!Directory.Exists(_installDir))
                {
                    Directory.CreateDirectory(_installDir);
                }

                UpdateProgress(25, "Extracting application payload...");
                await Task.Delay(200);

                // Get embedded resource stream
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var resourceName = "OptiSYS.Installer.Resources.app.zip";
                
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        throw new FileNotFoundException("Embedded payload resource 'app.zip' not found.");
                    }

                    using (var archive = new ZipArchive(stream))
                    {
                        int total = archive.Entries.Count;
                        int count = 0;

                        foreach (var entry in archive.Entries)
                        {
                            // Resolve full path
                            var destPath = Path.GetFullPath(Path.Combine(_installDir, entry.FullName));
                            
                            // Prevent Zip Slip
                            if (!destPath.StartsWith(_installDir, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (string.IsNullOrEmpty(entry.Name))
                            {
                                Directory.CreateDirectory(destPath);
                                continue;
                            }

                            var parentDir = Path.GetDirectoryName(destPath);
                            if (parentDir != null && !Directory.Exists(parentDir))
                            {
                                Directory.CreateDirectory(parentDir);
                            }

                            // Overwrite existing files
                            entry.ExtractToFile(destPath, true);
                            
                            count++;
                            int percent = 25 + (int)(35.0 * count / total);
                            UpdateProgress(percent, $"Extracting: {entry.Name}...");
                            
                            // Pace extraction visually if it runs too fast
                            if (count % 10 == 0)
                            {
                                await Task.Delay(10);
                            }
                        }
                    }
                }

                UpdateProgress(65, "Writing application registration...");
                await Task.Delay(300);

                // Copy current running installer as uninstall.exe
                var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(currentExe))
                {
                    var uninstallDest = Path.Combine(_installDir, "uninstall.exe");
                    File.Copy(currentExe, uninstallDest, true);
                }

                UpdateProgress(75, "Creating shortcuts...");
                await Task.Delay(300);

                bool desktopShortcut = DesktopShortcutCheck.IsChecked == true;
                bool startMenuShortcut = StartMenuShortcutCheck.IsChecked == true;
                bool startWithWindows = StartWithWindowsCheck.IsChecked == true;

                if (desktopShortcut)
                {
                    var desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "optiSYS.lnk");
                    CreateShortcut(desktopPath, _exePath);
                }

                if (startMenuShortcut)
                {
                    var startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
                    if (!Directory.Exists(startMenuDir)) Directory.CreateDirectory(startMenuDir);
                    var startMenuPath = Path.Combine(startMenuDir, "optiSYS.lnk");
                    CreateShortcut(startMenuPath, _exePath);
                }

                UpdateProgress(85, "Creating registry configuration...");
                await Task.Delay(200);

                // Create Startup Registry entry if enabled
                if (startWithWindows)
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        key?.SetValue("optiSYS", $"\"{_exePath}\" --background");
                    }
                }

                // Register uninstaller in user registry
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\optiSYS"))
                {
                    if (key != null)
                    {
                        key.SetValue("DisplayName", "optiSYS");
                        key.SetValue("DisplayVersion", "0.0.3");
                        key.SetValue("Publisher", "Deyan Todorov");
                        key.SetValue("DisplayIcon", $"{_exePath},0");
                        key.SetValue("InstallLocation", _installDir);
                        key.SetValue("UninstallString", $"\"{Path.Combine(_installDir, "uninstall.exe")}\" --uninstall");
                        key.SetValue("NoModify", 1);
                        key.SetValue("NoRepair", 1);
                    }
                }

                UpdateProgress(100, "Completing configuration...");
                await Task.Delay(500);

                ShowFinishedScreen();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Installation failed: {ex.Message}", "optiSYS Setup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _isInstalling = false;
                Close();
            }
        }

        private async Task RunUninstallAsync()
        {
            try
            {
                UpdateProgress(20, "Stopping active processes...");
                await Task.Delay(400);

                // Stop running instances
                var running = Process.GetProcessesByName("OptiSYS");
                foreach (var p in running)
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                }

                UpdateProgress(40, "Removing shortcuts...");
                await Task.Delay(300);

                // Delete desktop shortcut
                var desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "optiSYS.lnk");
                if (File.Exists(desktopPath)) File.Delete(desktopPath);

                // Delete start menu shortcut
                var startMenuPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "optiSYS.lnk");
                if (File.Exists(startMenuPath)) File.Delete(startMenuPath);

                UpdateProgress(60, "Removing registry configurations...");
                await Task.Delay(300);

                // Delete startup registry run key
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    key?.DeleteValue("optiSYS", false);
                }

                // Remove the self-provisioned elevated logon task, if the app created one
                // (opt-in "Run with highest privileges"). Best-effort: the asInvoker installer
                // may lack rights to delete a HighestAvailable task, in which case it harmlessly
                // points at a now-deleted exe and no-ops on next logon.
                DeleteElevationTask();

                // Delete uninstaller registry keys
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\optiSYS", false);

                UpdateProgress(80, "Removing files...");
                await Task.Delay(400);

                // Delete directories/files (skip current running uninstall.exe)
                if (Directory.Exists(_installDir))
                {
                    foreach (var file in Directory.GetFiles(_installDir))
                    {
                        var name = Path.GetFileName(file);
                        if (!name.Equals("uninstall.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }

                    foreach (var dir in Directory.GetDirectories(_installDir))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }

                UpdateProgress(100, "Uninstall complete!");
                await Task.Delay(500);

                ShowFinishedScreen();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Uninstallation encountered an error: {ex.Message}", "optiSYS Setup Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                ShowFinishedScreen();
            }
        }

        private static void DeleteElevationTask()
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/Delete /TN \"OptiSYS\" /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                p?.WaitForExit(5000);
            }
            catch { /* no task, or insufficient rights — non-critical */ }
        }

        private void UpdateProgress(int percent, string message)
        {
            InstallProgress.Value = percent;
            ProgressStatus.Text = message;
        }

        private async void ShowFinishedScreen()
        {
            // Transition from Installing screen to Completed screen
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
            var slideLeft = new DoubleAnimation(0, -150, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            var transitionCompleted = new TaskCompletionSource<bool>();
            fadeOut.Completed += (s, ev) => { transitionCompleted.SetResult(true); };

            InstallingPanel.BeginAnimation(OpacityProperty, fadeOut);
            InstallingTransform.BeginAnimation(TranslateTransform.XProperty, slideLeft);

            await transitionCompleted.Task;
            InstallingPanel.Visibility = Visibility.Collapsed;

            // Show Completed Panel
            CompletedPanel.Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350));
            var slideIn = new DoubleAnimation(150, 0, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            CompletedPanel.BeginAnimation(OpacityProperty, fadeIn);
            CompletedTransform.BeginAnimation(TranslateTransform.XProperty, slideIn);
        }

        private void FinishButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.IsUninstallMode)
            {
                // Self-deletion of uninstall.exe using background cmd delay
                var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(currentExe))
                {
                    var parentDir = Path.GetDirectoryName(currentExe);
                    // Launch a cmd background process to delete the directory and uninstall.exe after we exit
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c timeout /t 1 && rmdir /s /q \"{parentDir}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    Process.Start(startInfo);
                }
                Close();
                return;
            }

            if (LaunchAfterFinishCheck.IsChecked == true && File.Exists(_exePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _exePath,
                    UseShellExecute = true
                });
            }
            Close();
        }

        private void CreateShortcut(string shortcutPath, string targetPath)
        {
            Type? t = Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B18B24F")); // Windows Script Host
            if (t != null)
            {
                dynamic? shell = Activator.CreateInstance(t);
                if (shell != null)
                {
                    try
                    {
                        var shortcut = shell.CreateShortcut(shortcutPath);
                        shortcut.TargetPath = targetPath;
                        shortcut.IconLocation = targetPath + ",0";
                        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
                        shortcut.Save();
                    }
                    finally
                    {
                        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
                    }
                }
            }
        }
    }
}
