using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TarkovMusicPause
{
    internal sealed class MainForm : Form
    {
        private readonly CheckBox _chkResume;
        private readonly CheckBox _chkAutoStart;
        private readonly CheckBox _chkStartMinimised;
        private readonly Button _btnStart;
        private readonly Button _btnStop;
        private readonly Label _lblLogsPath;
        private readonly Label _lblStatus;
        private readonly RichTextBox _rtbLog;
        private readonly NotifyIcon _notifyIcon;

        private string _logsDir;
        private CancellationTokenSource _cts;
        private bool _reallyClosing;

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MINIMIZE = 0xF020;

        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TarkovMusicPause");
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TarkovMusicPause", "logpath.txt");
        private static readonly string AutoStartPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TarkovMusicPause", "autostart.txt");
        private static readonly string StartMinimisedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TarkovMusicPause", "startminimised.txt");

        public MainForm()
        {
            _logsDir = LoadSavedPath();

            SuspendLayout();

            Text = "Tarkov Music Pause";
            MinimumSize = new Size(440, 320);
            Size = new Size(580, 440);
            Icon = MakeProgramIcon();

            // Tray icon
            _notifyIcon = new NotifyIcon
            {
                Text = "Tarkov Music Pause",
                Icon = MakeProgramIcon(),
                Visible = false,
            };
            var trayMenu = new ContextMenuStrip();
            var miShow = new ToolStripMenuItem("Show");
            miShow.Font = new Font(miShow.Font, FontStyle.Bold);
            miShow.Click += delegate { ShowFromTray(); };
            var miQuit = new ToolStripMenuItem("Quit");
            miQuit.Click += delegate { QuitApp(); };
            trayMenu.Items.Add(miShow);
            trayMenu.Items.Add(miQuit);
            _notifyIcon.ContextMenuStrip = trayMenu;
            _notifyIcon.DoubleClick += delegate { ShowFromTray(); };

            // Layout
            var tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                ColumnCount = 1,
                RowCount = 5,
                AutoSize = false,
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // Row 0 - current path display
            _lblLogsPath = new Label
            {
                Text = "Logs: " + _logsDir,
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Font = new Font("Segoe UI", 8f),
                Padding = new Padding(0, 0, 0, 0),
            };
            tbl.Controls.Add(_lblLogsPath, 0, 0);

            // Row 1 - checkboxes
            var chkPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Anchor = AnchorStyles.Left,
                Padding = new Padding(0, 6, 0, 0),
                Margin = new Padding(0),
                WrapContents = false,
            };
            _chkResume = new CheckBox { Text = "Resume after raid", Checked = true, AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
            _chkAutoStart = new CheckBox { Text = "Auto-start on launch", Checked = LoadAutoStart(), AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
            _chkAutoStart.CheckedChanged += delegate { SaveAutoStart(_chkAutoStart.Checked); };
            _chkStartMinimised = new CheckBox { Text = "Start minimised", Checked = LoadStartMinimised(), AutoSize = true };
            _chkStartMinimised.CheckedChanged += delegate { SaveStartMinimised(_chkStartMinimised.Checked); };
            chkPanel.Controls.Add(_chkResume);
            chkPanel.Controls.Add(_chkAutoStart);
            chkPanel.Controls.Add(_chkStartMinimised);
            tbl.Controls.Add(chkPanel, 0, 1);

            // Row 2 - buttons
            var btnPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Anchor = AnchorStyles.Left,
                Padding = new Padding(0, 10, 0, 0),
                Margin = new Padding(0),
                WrapContents = false,
            };
            _btnStart = new Button { Text = "Start", Width = 90, Height = 26 };
            _btnStart.Click += BtnStart_Click;
            _btnStop = new Button { Text = "Stop", Width = 90, Height = 26, Enabled = false };
            _btnStop.Click += delegate { _cts?.Cancel(); };
            var btnTest = new Button { Text = "Test media key", Width = 110, Height = 26 };
            btnTest.Click += BtnTest_Click;
            var btnSetPath = new Button { Text = "Set path", Width = 80, Height = 26 };
            btnSetPath.Click += BtnSetPath_Click;
            var btnTray = new Button { Text = "Minimise to tray", Width = 120, Height = 26 };
            btnTray.Click += delegate { MinimiseToTray(); };
            btnPanel.Controls.Add(_btnStart);
            btnPanel.Controls.Add(_btnStop);
            btnPanel.Controls.Add(btnTest);
            btnPanel.Controls.Add(btnSetPath);
            btnPanel.Controls.Add(btnTray);
            tbl.Controls.Add(btnPanel, 0, 2);

            // Row 3 - status
            _lblStatus = new Label { Text = "Idle", AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            tbl.Controls.Add(_lblStatus, 0, 3);

            // Row 4 - log
            _rtbLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9f),
                ReadOnly = true,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Margin = new Padding(0, 8, 0, 0),
                WordWrap = false,
            };
            tbl.Controls.Add(_rtbLog, 0, 4);

            Controls.Add(tbl);
            FormClosing += MainForm_FormClosing;
            Load += async (s, e) =>
            {
                if (_chkStartMinimised.Checked || _chkAutoStart.Checked) MinimiseToTray();
                if (_chkAutoStart.Checked) await StartWatcher();
            };
            ResumeLayout(false);
            PerformLayout();
        }

        private void BtnSetPath_Click(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select your EFT Logs folder (e.g. C:\\Games\\Logs)";
                dlg.SelectedPath = _logsDir;
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    _logsDir = dlg.SelectedPath;
                    _lblLogsPath.Text = "Logs: " + _logsDir;
                    SavePath(_logsDir);
                }
            }
        }

        private static string LoadSavedPath()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var saved = File.ReadAllText(ConfigPath).Trim();
                    if (saved.Length > 0) return saved;
                }
            }
            catch { }
            return LogWatcher.DefaultLogRoot;
        }

        private static void SavePath(string path)
        {
            try { Directory.CreateDirectory(ConfigDir); File.WriteAllText(ConfigPath, path); }
            catch { }
        }

        private static bool LoadAutoStart()
        {
            try { if (File.Exists(AutoStartPath)) return File.ReadAllText(AutoStartPath).Trim() == "1"; }
            catch { }
            return false;
        }

        private static void SaveAutoStart(bool value)
        {
            try { Directory.CreateDirectory(ConfigDir); File.WriteAllText(AutoStartPath, value ? "1" : "0"); }
            catch { }
        }

        private static bool LoadStartMinimised()
        {
            try { if (File.Exists(StartMinimisedPath)) return File.ReadAllText(StartMinimisedPath).Trim() == "1"; }
            catch { }
            return false;
        }

        private static void SaveStartMinimised(bool value)
        {
            try { Directory.CreateDirectory(ConfigDir); File.WriteAllText(StartMinimisedPath, value ? "1" : "0"); }
            catch { }
        }

        private void MinimiseToTray() { Hide(); _notifyIcon.Visible = true; }
        private void ShowFromTray() { _notifyIcon.Visible = false; Show(); WindowState = FormWindowState.Normal; Activate(); }
        private void QuitApp() { _cts?.Cancel(); _notifyIcon.Visible = false; _reallyClosing = true; Close(); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt32() & 0xFFF0) == SC_MINIMIZE)
            { MinimiseToTray(); return; }
            base.WndProc(ref m);
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_reallyClosing) { e.Cancel = true; MinimiseToTray(); }
        }

        private async void BtnStart_Click(object sender, EventArgs e) { await StartWatcher(); }

        private async Task StartWatcher()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            SetRunning(true);
            var watcher = new LogWatcher(_logsDir, _chkResume.Checked, false, AppendLog, MediaKey.Pause, MediaKey.Play);
            try { var token = _cts.Token; await Task.Run(() => watcher.Run(token)); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppendLog("Error: " + ex.Message); }
            finally { _cts = null; SetRunning(false); }
        }

        private void BtnTest_Click(object sender, EventArgs e)
        {
            AppendLog("Test: sending media Play/Pause (should toggle your player).");
            try { MediaKey.PlayPause(); AppendLog("Test: done."); }
            catch (Exception ex) { AppendLog("Test failed: " + ex.Message); }
        }

        private void SetRunning(bool running)
        {
            if (InvokeRequired) { Invoke((Action)(() => SetRunning(running))); return; }
            _lblStatus.Text = running ? "Running" : "Idle";
            _btnStart.Enabled = !running;
            _btnStop.Enabled = running;
        }

        internal void AppendLog(string message)
        {
            if (InvokeRequired) { Invoke((Action)(() => AppendLog(message))); return; }
            _rtbLog.AppendText(message + "\n");
            _rtbLog.ScrollToCaret();
        }

        private static Icon MakeProgramIcon()
        {
            const int size = 64;
            using (var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var bg = new SolidBrush(Color.FromArgb(255, 20, 20, 20)))
                    g.FillEllipse(bg, 0, 0, size - 1, size - 1);
                using (var white = new SolidBrush(Color.White))
                {
                    g.FillPolygon(white, new[] { new PointF(10, 16), new PointF(10, 48), new PointF(30, 32) });
                    g.FillRectangle(white, 34, 16, 10, 32);
                    g.FillRectangle(white, 47, 16, 10, 32);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _notifyIcon.Dispose(); _cts?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
