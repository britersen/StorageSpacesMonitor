using System;
using System.Drawing;
using System.IO;
using System.Management;
using System.Reflection;
using System.Text;
using System.Timers;
using System.Windows.Forms;


namespace StorageSpacesMonitor
{
    class Program
    {
        const string Version = "v1.0.0";

        static NotifyIcon trayIcon;
        static System.Timers.Timer updateTimer;
        static Form statusForm;
        static TextBox statusTextBox;

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            trayIcon = new NotifyIcon()
            {
                Icon = LoadEmbeddedIcon("StorageSpacesMonitor.purple.ico"),
                Visible = true,
                Text = $"Storage Spaces Repair Monitor {Version}"
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Exit", null, (s, e) => Application.Exit());
            trayIcon.ContextMenuStrip = contextMenu;

            trayIcon.MouseClick += TrayIcon_MouseClick;

            updateTimer = new System.Timers.Timer(5000); // every 30 seconds
            updateTimer.Elapsed += UpdateStatus;
            updateTimer.AutoReset = true;
            updateTimer.Start();

            CreateStatusForm();
            statusForm.CreateControl(); // Ensures handle is created
            UpdateStatus(null, null);

            Application.Run();
            trayIcon.Visible = false;
        }

        static void CreateStatusForm()
        {
            statusForm = new Form()
            {
                Text = $"Storage Spaces Repair Status {Version}",
                Width = 20,
                Height = 20,
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedSingle,
                MaximizeBox = false,
                MinimizeBox = false
            };

            statusForm.FormClosing += (s, e) =>
            {
                e.Cancel = true;
                statusForm.Hide();
            };

            statusTextBox = new TextBox()
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new System.Drawing.Font("Consolas", 10)
            };

            statusForm.Controls.Add(statusTextBox);
        }

        static void TrayIcon_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (statusForm.Visible)
                    statusForm.Hide();
                else
                    statusForm.Show();
            }
        }

        static Icon LoadEmbeddedIcon(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException($"Resource '{resourceName}' not found.");
                return new Icon(stream);
            }
        }
        static string FormatBytes(ulong bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB", "EB", "ZB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return $"{size:0.##} {units[unit]}";
        }

        static string FormatTimeSpan(TimeSpan ts)
        {
            return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        }

        static TimeSpan ParseDmtfTimeInterval(string dmtf)
        {
            // Format: ddddddddddhhmmss.mmmmmms:000
            // Example: 00000000001230.000000:000 (12 minutes, 30 seconds)
            if (string.IsNullOrEmpty(dmtf) || dmtf.Length < 25)
                return TimeSpan.Zero;

            int days = int.Parse(dmtf.Substring(0, 8));
            int hours = int.Parse(dmtf.Substring(8, 2));
            int minutes = int.Parse(dmtf.Substring(10, 2));
            int seconds = int.Parse(dmtf.Substring(12, 2));
            int microseconds = int.Parse(dmtf.Substring(15, 6));

            return new TimeSpan(days, hours, minutes, seconds, microseconds / 1000);
        }

        static void UpdateStatus(object sender, ElapsedEventArgs e)
        {
            try
            {
                var searcher = new ManagementObjectSearcher("ROOT\\Microsoft\\Windows\\Storage", "SELECT * FROM MSFT_StorageJob");
                var results = searcher.Get();

                string text;
                if (results.Count > 0)
                {
                    var sb = new StringBuilder();

                    foreach (ManagementObject job in results)
                    {
                        string name = job["Name"]?.ToString() ?? "";
                        string percent = job["PercentComplete"]?.ToString() ?? "";
                        string processedStr = job["BytesProcessed"]?.ToString() ?? "0";
                        string totalStr = job["BytesTotal"]?.ToString() ?? "0";
                        string elapsedStr = job["ElapsedTime"]?.ToString() ?? "";

                        TimeSpan elapsed = ParseDmtfTimeInterval(elapsedStr);

                        if (ulong.TryParse(processedStr, out ulong processed) &&
                            ulong.TryParse(totalStr, out ulong total) &&
                            processed > 0 && elapsed.TotalSeconds > 0)
                        {
                            double rate = processed / elapsed.TotalSeconds;
                            double remainingSeconds = (total - processed) / rate;
                            TimeSpan etaSpan = TimeSpan.FromSeconds(remainingSeconds);
                            string etaFormatted = FormatTimeSpan(etaSpan);
                            string elapsedFormatted = FormatTimeSpan(elapsed);

                            sb.AppendLine($"{name}: {percent}% · {FormatBytes(processed)} / {FormatBytes(total)} · {elapsedFormatted} · ETA {etaFormatted}");
                        }
                        else
                        {
                            if (ulong.TryParse(processedStr, out ulong processed2) && ulong.TryParse(totalStr, out ulong total2) && TimeSpan.TryParse(elapsedStr, out TimeSpan elapsed2))
                            {
                                string elapsedFormatted = FormatTimeSpan(elapsed2);
                                sb.AppendLine($"{name}: {percent}% · {FormatBytes(processed2)} / {FormatBytes(total2)} · {elapsedFormatted} · ETA unknown");
                            }
                            else
                            {
                                sb.AppendLine($"{name}: {percent}% · {processedStr} / {totalStr} · {elapsedStr} · ETA unknown");
                            }
                        }
                    }

                    text = sb.ToString().Trim();
                }
                else
                {
                    text = "No repair or regeneration job running";
                }

                void UpdateUI()
                {
                    statusTextBox.Text = text;

                    // Measure each line for width and total height for all lines
                    int maxWidth = 0;
                    int totalHeight = 0;
                    using (Graphics g = statusTextBox.CreateGraphics())
                    {
                        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                        foreach (var line in lines)
                        {
                            Size size = TextRenderer.MeasureText(g, line, statusTextBox.Font);
                            if (size.Width > maxWidth)
                                maxWidth = size.Width;
                            totalHeight += size.Height;
                        }
                    }

                    // Add some padding for the TextBox border and form chrome
                    int desiredWidth = maxWidth + 40; // 40px padding for scroll bar, borders, etc.
                    int minWidth = 20; // Your default width
                    if (desiredWidth > statusForm.Width)
                    {
                        statusForm.Width = Math.Max(desiredWidth, minWidth);
                    }

                    // Calculate desired height
                    int chromeHeight = statusForm.Height - statusForm.ClientSize.Height;
                    int desiredHeight = totalHeight + chromeHeight + 20; // 20px padding
                    int minHeight = 20; // Your default height
                    if (desiredHeight > statusForm.Height)
                    {
                        statusForm.Height = Math.Max(desiredHeight, minHeight);
                    }
                }

                if (statusTextBox.IsHandleCreated)
                {
                    statusTextBox.Invoke((System.Windows.Forms.MethodInvoker)UpdateUI);
                }
                else
                {
                    UpdateUI();
                }
            }
            catch (Exception ex)
            {
                void UpdateError()
                {
                    statusTextBox.Text = $"Error: {ex.Message}";
                }
                if (statusTextBox.IsHandleCreated)
                {
                    statusTextBox.Invoke((System.Windows.Forms.MethodInvoker)UpdateError);
                }
                else
                {
                    UpdateError();
                }
            }
        }
    }

}