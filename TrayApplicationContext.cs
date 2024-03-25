using System;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Windows.Forms;
using TextCopy;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Linq;
using System.Windows.Forms.PropertyGridInternal;


namespace ClipboardTTS
{
    public static class ProcessHelper
    {
        public static void KillProcesses(string processName)
        {
            Process[] processes = Process.GetProcessesByName(processName);
            foreach (Process process in processes)
            {
                try
                {
                    process.Kill();
                }
                catch (Exception ex)
                {
                    // Log any exceptions that occur while killing the process
                    LogError(ex);
                }
            }
        }


        private static void LogError(Exception ex)
        {
            string logFilePath = "error.log";
            string errorMessage = $"[{DateTime.Now}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}";

            File.AppendAllText(logFilePath, errorMessage);
        }
    }
    public class HotkeyWindow : Form
    {
        private const int WM_HOTKEY = 0x0312;
        public const int HotKeyId = 1; // Make HotKeyId public
        private bool isHotkeyProcessing = false;


        private TrayApplicationContext context;

        public HotkeyWindow(TrayApplicationContext context)
        {
            this.context = context;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotKeyId)
            {
                if (!isHotkeyProcessing)
                {
                    isHotkeyProcessing = true;

                    // Stop the speech synthesis
                    context.StopSpeech();

                    // Reset the flag after processing is completed
                    isHotkeyProcessing = false;
                }
            }

            base.WndProc(ref m);
        }
    }

    public class TrayApplicationContext : ApplicationContext
    {
        private bool _isFirstClipboardChange = true;
        private bool _isFirstClipboardChangeAfterMonitoring = true;

        private const string TempFile = "clipboard_temp.txt";
        private const string SoxPath = "sox.exe";
        private const string PiperPath = "piper.exe";
        private string PiperArgs;
        private const string SoxArgs = "-t raw -b 16 -e signed-integer -r 22050 -c 1 - -t waveaudio pad 0 3";
        private const string SettingsFile = "settings.conf";

        private NotifyIcon trayIcon;
        private bool isRunning = true;
        private bool EnableLogging { get; set; }
        private bool isMonitoringEnabled = true;
        private static Mutex mutex = null;


        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern short VkKeyScan(char ch);

        private const uint MOD_ALT = 0x0001;
        private const uint VK_Q = 0x51; // Virtual key code for 'Q' key

        private HotkeyWindow hotkeyWindow;

        private Icon idleIcon;
        private Icon activeIcon;
        private NotifyIcon notifyIcon;
        private Thread monitoringThread;
        private System.Threading.SynchronizationContext _syncContext;

        private void ShowAboutWindow()
        {
            string version = "1.1.4";
            string message = $"Piper Tray\n\nVersion: {version}\n\nDeveloped by jame25";
            string url = "https://github.com/jame25/Piper-Tray";

            using (Form aboutForm = new Form())
            {
                aboutForm.Text = "About Piper Tray";
                aboutForm.FormBorderStyle = FormBorderStyle.FixedSingle;
                aboutForm.MaximizeBox = false;
                aboutForm.MinimizeBox = false;
                aboutForm.StartPosition = FormStartPosition.CenterScreen;
                aboutForm.ClientSize = new Size(350, 180);

                PictureBox logoPictureBox = new PictureBox();
                logoPictureBox.Image = new Icon("idle.ico").ToBitmap();
                logoPictureBox.SizeMode = PictureBoxSizeMode.Zoom;
                logoPictureBox.Location = new Point(10, 10);
                logoPictureBox.Size = new Size(64, 64);
                aboutForm.Controls.Add(logoPictureBox);

                Label messageLabel = new Label();
                messageLabel.Text = message;
                messageLabel.Location = new Point(90, 10);
                messageLabel.AutoSize = true;
                aboutForm.Controls.Add(messageLabel);

                LinkLabel linkLabel = new LinkLabel();
                linkLabel.Text = url;
                linkLabel.Location = new Point(90, messageLabel.Bottom + 10);
                linkLabel.AutoSize = true;
                linkLabel.LinkClicked += (sender, e) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                aboutForm.Controls.Add(linkLabel);

                Button okButton = new Button();
                okButton.Text = "OK";
                okButton.DialogResult = DialogResult.OK;
                okButton.Location = new Point(aboutForm.ClientSize.Width - 80, aboutForm.ClientSize.Height - 40);
                aboutForm.Controls.Add(okButton);

                aboutForm.AcceptButton = okButton;

                aboutForm.ShowDialog();
            }
        }

        public TrayApplicationContext()
        {
            try
            {
                const string appName = "PiperTray";
                bool createdNew;

                mutex = new Mutex(true, appName, out createdNew);

                if (!createdNew)
                {
                    // Another instance of the application is already running
                    MessageBox.Show("Another instance of the application is already running.", "Piper Tray", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Application.Exit();
                    return;
                }

                // Check if model exists in the application directory
                string[] onnxFiles = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.onnx");
                if (onnxFiles.Length == 0)
                {
                    MessageBox.Show("No speech model files found. Please make sure you place the .onnx file and .json is in the same directory as the application.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Application.Exit();
                    return;
                }

                LoadSettings();

                // Clear the clipboard_temp.txt file at startup
                try
                {
                    if (File.Exists(TempFile))
                    {
                        File.WriteAllText(TempFile, string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    LogError(ex);
                }

                idleIcon = new Icon("idle.ico");
                activeIcon = new Icon("active.ico");

                trayIcon = new NotifyIcon();
                trayIcon.Icon = idleIcon;
                trayIcon.Text = "Piper Tray";
                trayIcon.Visible = true;

                ContextMenuStrip contextMenu = new ContextMenuStrip();
                ToolStripMenuItem monitoringItem = new ToolStripMenuItem("Disable Monitoring", null, MonitoringItem_Click);
                monitoringItem.Checked = isMonitoringEnabled;
                contextMenu.Items.Add(monitoringItem);
                contextMenu.Items.Add("Stop Speech", null, StopItem_Click);
                contextMenu.Items.Add("About", null, AboutItem_Click);
                contextMenu.Items.Add("Exit", null, ExitItem_Click);

                trayIcon.ContextMenuStrip = contextMenu;

                // hotkey registration code
                hotkeyWindow = new HotkeyWindow(this);
                RegisterHotKey(hotkeyWindow.Handle, HotkeyWindow.HotKeyId, MOD_ALT, VK_Q);

                _syncContext = System.Threading.SynchronizationContext.Current;

                // Start monitoring the clipboard automatically
                monitoringThread = new Thread(StartMonitoring);
                monitoringThread.Start();
            }
            catch (Exception ex)
            {
                LogError(ex);
                throw;
            }
        }
        public void RestartMonitoring()
        {
            // Stop the current monitoring thread
            isRunning = false;
            if (monitoringThread != null && monitoringThread.IsAlive)
            {
                monitoringThread.Join();
            }

            // Clear the temporary file
            try
            {
                File.WriteAllText(TempFile, string.Empty);
            }
            catch (IOException ex)
            {
                // Handle the exception if the file is in use or cannot be accessed
                LogError(ex);
                // You can choose to display an error message to the user or take appropriate action
            }

            // Start a new monitoring thread
            isRunning = true;
            monitoringThread = new Thread(StartMonitoring);
            monitoringThread.Start();
        }


        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UnregisterHotKey(hotkeyWindow.Handle, HotkeyWindow.HotKeyId);
                hotkeyWindow.Dispose();
                trayIcon.Dispose();
                notifyIcon.Dispose();
                idleIcon.Dispose();
                activeIcon.Dispose();
            }

            base.Dispose(disposing);
        }
        private void UpdateTrayIcon(bool isActive)
        {
            if (isActive)
            {
                trayIcon.Icon = activeIcon;
            }
            else
            {
                trayIcon.Icon = idleIcon;
            }
        }

        private void LoadSettings()
        {
            bool enableLogging = false; // Default value (logging disabled)

            if (File.Exists(SettingsFile))
            {
                string[] lines = File.ReadAllLines(SettingsFile);
                string model = "";
                string speed = "";

                foreach (string line in lines)
                {
                    if (line.StartsWith("model="))
                    {
                        model = line.Substring("model=".Length).Trim();
                    }
                    else if (line.StartsWith("speed="))
                    {
                        speed = line.Substring("speed=".Length).Trim();
                    }
                    else if (line.StartsWith("logging="))
                    {
                        string loggingValue = line.Substring("logging=".Length).Trim();

                        if (bool.TryParse(loggingValue, out bool value))
                        {
                            enableLogging = value;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(model) && !string.IsNullOrEmpty(speed))
                {
                    PiperArgs = $"--model {model} --length_scale {speed} --output-raw";
                }
                else
                {
                    // Use default model and speed if either is missing in the settings file
                    PiperArgs = "--model en_US-libritts_r-medium.onnx --length_scale 1 --output-raw";
                }
            }
            else
            {
                // Use default model and speed if settings file doesn't exist
                PiperArgs = "--model en_US-libritts_r-medium.onnx --length_scale 1 --output-raw";
            }

            // Store the logging setting in a class-level variable
            EnableLogging = enableLogging;
        }

        private void MonitoringItem_Click(object sender, EventArgs e)
        {
            isMonitoringEnabled = !isMonitoringEnabled;
            ToolStripMenuItem monitoringItem = (ToolStripMenuItem)sender;
            monitoringItem.Text = isMonitoringEnabled ? "Disable Monitoring" : "Enable Monitoring";
            monitoringItem.Checked = isMonitoringEnabled;

            if (isMonitoringEnabled)
            {
                _isFirstClipboardChangeAfterMonitoring = true;
            }
        }





        private void StopItem_Click(object sender, EventArgs e)
        {
            {
                StopSpeech();
            }

            // Update the tray icon to indicate idle state
            _syncContext.Post(_ =>
            {
                UpdateTrayIcon(ActivityState.Idle);
            }, null);
        }

        private void AboutItem_Click(object sender, EventArgs e)
        {
            ShowAboutWindow();
        }


        private void ExitItem_Click(object sender, EventArgs e)
        {
            isRunning = false;

            // Kill the sox.exe and piper.exe processes if they are running
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/C taskkill /F /IM sox.exe /IM piper.exe",
                CreateNoWindow = true,
                UseShellExecute = false
            }).WaitForExit();

            // Clear the temporary file when exiting
            try
            {
                File.WriteAllText(TempFile, string.Empty);
            }
            catch (IOException ex)
            {
                // Handle the exception if the file is in use or cannot be accessed
                LogError(ex);
                // You can choose to display an error message to the user or take appropriate action
            }

            trayIcon.Visible = false;
            Application.Exit();
        }

        // Insert the StopSpeech method here
        public void StopSpeech()
        {
            // Kill the sox.exe and piper.exe processes if they are running
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/C taskkill /F /IM sox.exe /IM piper.exe",
                CreateNoWindow = true,
                UseShellExecute = false
            }).WaitForExit();

            // Update the tray icon to indicate idle state
            _syncContext.Post(_ =>
            {
                UpdateTrayIcon(ActivityState.Idle);
            }, null);
        }

        // Add the LogError method here
        private void LogError(Exception ex)
        {
            string logFilePath = "error.log";
            string errorMessage = $"[{DateTime.Now}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}";

            File.AppendAllText(logFilePath, errorMessage);
        }


        private SemaphoreSlim _monitoringSemaphore = new SemaphoreSlim(1, 1);

        private async void StartMonitoring()
        {
            try
            {
                string prevClipboardText = string.Empty;

                // Read the ignore dictionary file
                string[] ignoreWords = File.Exists("ignore.dict") ? File.ReadAllLines("ignore.dict") : new string[0];

                while (isRunning)
                {
                    if (isMonitoringEnabled)
                    {
                        // Acquire the semaphore to ensure only one monitoring process is running
                        await _monitoringSemaphore.WaitAsync();

                        try
                        {
                            // Get the current clipboard text
                            string clipboardText = ClipboardService.GetText();

                            // Check if the clipboard text is null, empty, or unchanged
                            if (string.IsNullOrEmpty(clipboardText) || clipboardText == prevClipboardText)
                            {
                                await Task.Delay(100);
                                continue;
                            }

                            // Update the previous clipboard text
                            prevClipboardText = clipboardText;

                            // Skip processing the first clipboard change after monitoring is enabled
                            if (_isFirstClipboardChangeAfterMonitoring)
                            {
                                _isFirstClipboardChangeAfterMonitoring = false;
                                await Task.Delay(100);
                                continue;
                            }

                            // Split the clipboard text into words
                            string[] words = Regex.Split(clipboardText, @"\s+");

                            // Filter out the ignored words
                            string filteredText = string.Join(" ", words.Where(word => !ignoreWords.Contains(word, StringComparer.OrdinalIgnoreCase)));

                            // Write the filtered text to the temporary file
                            try
                            {
                                File.WriteAllText(TempFile, filteredText);
                            }
                            catch (IOException ex)
                            {
                                // Handle the exception if the file is in use or cannot be accessed
                                LogError(ex);
                                continue;
                            }

                            // Update the tray icon to indicate active state
                            _syncContext.Post(_ =>
                            {
                                UpdateTrayIcon(ActivityState.Active);
                            }, null);

                            // Kill any existing instances of sox.exe and piper.exe
                            ProcessHelper.KillProcesses("sox");
                            ProcessHelper.KillProcesses("piper");

                            // Use Piper TTS to convert the text from the temporary file to raw audio and pipe it to SoX
                            await Task.Run(() =>
                            {
                                string piperCommand = $"{PiperPath} {PiperArgs} < \"{TempFile}\"";
                                string soxCommand = $"{SoxPath} {SoxArgs}";
                                Process piperProcess = new Process();
                                piperProcess.StartInfo.FileName = "cmd.exe";
                                piperProcess.StartInfo.Arguments = $"/C {piperCommand} | {soxCommand}";
                                piperProcess.StartInfo.UseShellExecute = false;
                                piperProcess.StartInfo.CreateNoWindow = true;
                                piperProcess.Start();
                                piperProcess.WaitForExit();
                            });

                            // Clear the temporary file after processing
                            try
                            {
                                File.WriteAllText(TempFile, string.Empty);
                            }
                            catch (IOException ex)
                            {
                                // Handle the exception if the file is in use or cannot be accessed
                                LogError(ex);
                            }

                            // Update the tray icon to indicate idle state
                            _syncContext.Post(_ =>
                            {
                                UpdateTrayIcon(ActivityState.Idle);
                            }, null);
                        }
                        finally
                        {
                            // Release the semaphore
                            _monitoringSemaphore.Release();
                        }
                    }

                    await Task.Delay(100);
                }
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        private void UpdateTrayIcon(ActivityState state)
        {
            if (trayIcon == null)
            {
                // Log an error or handle the case when trayIcon is null
                System.Diagnostics.Debug.WriteLine("trayIcon is null");
                return;
            }

            if (Resources.ActiveIcon == null || Resources.IdleIcon == null)
            {
                // Log an error or handle the case when icon resources are missing
                System.Diagnostics.Debug.WriteLine("Icon resources are missing");
                return;
            }

            if (state == ActivityState.Active)
            {
                // Set the active icon
                trayIcon.Icon = Resources.ActiveIcon;
            }
            else
            {
                // Set the idle icon
                trayIcon.Icon = Resources.IdleIcon;
            }
        }



        private enum ActivityState
        {
            Active,
            Idle
        }
    }
}
