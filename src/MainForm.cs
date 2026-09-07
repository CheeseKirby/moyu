using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PotPlayerAiSubtitle
{
    internal sealed partial class MainForm : Form
    {
        private static readonly Color PageColor = MoyuPalette.Page;
        private static readonly Color TextColor = MoyuPalette.Ink;
        private static readonly Color MutedColor = MoyuPalette.Muted;
        private static readonly Color AccentColor = MoyuPalette.Blue;
        private static readonly Color AccentSoft = MoyuPalette.BlueSoft;
        private static readonly Color SuccessColor = Color.FromArgb(42, 166, 122);
        private static readonly Color WarningColor = Color.FromArgb(217, 139, 36);

        private readonly bool startHidden;
        private readonly bool openSettings;
        private readonly EventWaitHandle wakeEvent;
        private readonly AutoResetEvent jobSignal = new AutoResetEvent(false);
        private readonly object jobLock = new object();
        private readonly PlaybackPromptSession promptSession = new PlaybackPromptSession();
        private readonly NotifyIcon trayIcon;
        private readonly TabControl tabs;
        private readonly TabPage taskTab;
        private readonly TabPage settingsTab;
        private readonly Label appStatusLabel;
        private readonly Panel taskCanvas;
        private readonly CardPanel detectedCard;
        private readonly CardPanel selectCard;
        private readonly CardPanel progressCard;
        private readonly Label taskFooterLabel;
        private readonly Label detectedNameLabel;
        private readonly Label detectedPathLabel;
        private readonly TextBox selectedPathBox;
        private readonly Label selectedInfoLabel;
        private readonly Button startButton;
        private readonly Label progressStageLabel;
        private readonly Label progressDetailLabel;
        private readonly Label progressPercentLabel;
        private readonly MoyuProgressBar progressBar;
        private readonly Button cancelButton;
        private readonly TextBox apiUrlBox;
        private readonly TextBox apiKeyBox;
        private readonly TextBox modelBox;
        private readonly SourceLanguageComboBox sourceLanguageBox;
        private readonly TextBox hubPathBox;
        private readonly Label apiStoredLabel;
        private readonly Label settingsStatusLabel;
        private readonly Button testButton;
        private readonly CheckBox monitorCheck;
        private readonly CheckBox startupCheck;

        private PotPlayerMonitor monitor;
        private Thread workerThread;
        private Thread wakeThread;
        private CancellationTokenSource jobCancellation;
        private volatile bool shuttingDown;
        private bool allowClose;
        private string activeMediaPath = "";
        private string activeSourceLanguage = "ja";
        private string detectedMediaPath = "";

        private static CardPanel NewCard(int left, int top, int width, int height, Color color)
        {
            return new CardPanel { Left = left, Top = top, Width = width, Height = height, BackColor = color };
        }

        private static Label CreateLabel(string text, int left, int top, int width, int height, float size, FontStyle style, Color color)
        {
            return new Label { AutoEllipsis = true, UseMnemonic = false, Text = text, Left = left, Top = top, Width = width, Height = height, Font = new Font("Microsoft YaHei UI", size, style), ForeColor = color };
        }


        private static Button CreateButton(string text, Color backColor, Color foreColor, int left, int top, int width, int height)
        {
            MoyuButton button = new MoyuButton { Text = text, Left = left, Top = top, Width = width, Height = height, BackColor = backColor, ForeColor = foreColor, Cursor = Cursors.Hand, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
            button.HoverBackColor = MoyuDrawing.Blend(backColor, Color.White, 0.14f);
            return button;
        }

        private static void AddFieldLabel(Control parent, string text, int left, int top)
        {
            parent.Controls.Add(CreateLabel(text, left, top, 300, 22, 9F, FontStyle.Bold, TextColor));
        }

        private void SetDetectedCardVisible(bool visible)
        {
            detectedCard.Visible = visible;
            taskCanvas.PerformLayout();
        }
        private void FormShown(object sender, EventArgs e)
        {
            AppConfig config = AppConfig.Load();
            try { StartupManager.SetEnabled(config.StartWithWindows); }
            catch (Exception ex)
            {
                Logger.Write("Watcher setup failed: " + ex.Message);
                settingsStatusLabel.Text = "后台检测器设置失败：" + ex.Message;
                settingsStatusLabel.ForeColor = WarningColor;
            }
            ApplyMonitorSetting(config.MonitorPotPlayer);
            StartWorkerLoop();
            StartWakeListener();
            if (openSettings) tabs.SelectedTab = settingsTab;
            string externalMedia = DetectionInbox.Take();
            if (!string.IsNullOrEmpty(externalMedia)) HandleDetectedMedia(externalMedia);
            else if (startHidden && !openSettings) BeginInvoke((MethodInvoker)delegate { Hide(); });
        }

        private void ChooseVideoClicked(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择需要识别并翻译字幕的视频";
                dialog.Filter = "视频文件|*.mkv;*.mp4;*.avi;*.mov;*.m4v;*.ts;*.m2ts;*.wmv;*.webm;*.flv;*.mpg;*.mpeg;*.vob;*.rmvb|所有文件|*.*";
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog(this) == DialogResult.OK) SelectMedia(dialog.FileName);
            }
        }

        private void SelectMedia(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            string fullPath = Path.GetFullPath(path);
            selectedPathBox.Text = fullPath;
            FileInfo info = new FileInfo(fullPath);
            selectedFileLabel.Text = Path.GetFileName(fullPath);
            toolTips.SetToolTip(selectedFileLabel, fullPath);
            selectedInfoLabel.Text = FormatSize(info.Length) + (processingVisible
                ? (string.Equals(fullPath, processingMediaPath, StringComparison.OrdinalIgnoreCase) ? "  ·  正在后台处理" : "  ·  当前任务结束后可生成")
                : "  ·  已就绪，可生成双语字幕");
            startButton.Enabled = !processingVisible;
            selectedInfoLabel.ForeColor = TextColor;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L) return (bytes / (1024d * 1024d * 1024d)).ToString("0.00") + " GB";
            if (bytes >= 1024L * 1024L) return (bytes / (1024d * 1024d)).ToString("0.0") + " MB";
            return (bytes / 1024d).ToString("0") + " KB";
        }

        private void StartJob(string mediaPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath)) throw new InvalidOperationException("请先选择一个存在的视频文件。");
                if (!PotPlayerMonitor.IsMediaFile(mediaPath)) throw new InvalidOperationException("所选文件不是受支持的视频格式。");
                if (string.IsNullOrWhiteSpace(CredentialStore.ReadApiKey()))
                {
                    tabs.SelectedTab = settingsTab;
                    settingsStatusLabel.ForeColor = WarningColor;
                    settingsStatusLabel.Text = "请先填写并保存 API Key，再开始字幕任务。";
                    return;
                }
                string sourceLanguage = AppConfig.Load().SourceLanguage;
                lock (jobLock)
                {
                    if (!string.IsNullOrEmpty(activeMediaPath) && string.Equals(activeMediaPath, Path.GetFullPath(mediaPath), StringComparison.OrdinalIgnoreCase)
                        && activeSourceLanguage == sourceLanguage)
                    {
                        UpdateProgress(new ProgressInfo("正在处理这个视频", "无需重复提交，请等待当前任务完成。", progressBar.Value));
                        return;
                    }
                }
                QueueManager.Notify(mediaPath, sourceLanguage);
                SelectMedia(mediaPath);
                SetDetectedCardVisible(false);
                tabs.SelectedTab = taskTab;
                UpdateProgress(new ProgressInfo("任务已加入", "后台即将开始识别字幕，PotPlayer 可以继续播放。", 1));
                SetAppStatus("等待处理", AccentColor);
                jobSignal.Set();
            }
            catch (Exception ex)
            {
                Logger.Write("Unable to start job: " + ex);
                tabs.SelectedTab = taskTab;
                UpdateProgress(new ProgressInfo("无法开始任务", ex.Message, 0));
                SetAppStatus("需要处理", Color.FromArgb(179, 67, 67));
            }
        }

        private void StartWorkerLoop()
        {
            if (workerThread != null && workerThread.IsAlive) return;
            workerThread = new Thread(WorkerLoop) { IsBackground = true, Name = "AI subtitle pipeline" };
            workerThread.Start();
            jobSignal.Set();
        }

        private void WorkerLoop()
        {
            while (!shuttingDown)
            {
                KeyValuePair<string, JobRequest>? queued = QueueManager.PeekNext();
                if (!queued.HasValue) { jobSignal.WaitOne(1000); continue; }
                string requestPath = queued.Value.Key;
                JobRequest request = queued.Value.Value;
                CancellationTokenSource source = new CancellationTokenSource();
                lock (jobLock) { jobCancellation = source; activeMediaPath = request.MediaPath; activeSourceLanguage = request.SourceLanguage; }
                SetProcessingState(request.MediaPath, true);
                try
                {
                    if (!File.Exists(request.MediaPath)) throw new FileNotFoundException("视频文件已不存在。", request.MediaPath);
                    IProgress<ProgressInfo> progress = new Progress<ProgressInfo>(UpdateProgress);
                    AppConfig jobConfig = AppConfig.Load();
                    jobConfig.SourceLanguage = request.SourceLanguage;
                    SubtitlePipelineRunner runner = new SubtitlePipelineRunner(jobConfig, progress, EnsureApiKey);
                    PipelineResult result = runner.Process(request.MediaPath, source.Token);
                    string completionDetail = "双语字幕已保存到视频旁，三种版本已归档到字幕库；字幕加载由 PotPlayer 负责。";
                    string warning = CombineWarnings(result.QualityWarning, result.PublishWarning);
                    if (!string.IsNullOrWhiteSpace(warning))
                    {
                        UpdateProgress(new ProgressInfo("双语字幕已完成（含提醒）", warning + " " + completionDetail, 100));
                        SetAppStatus("已完成 · 有提醒", WarningColor);
                    }
                    else
                    {
                        UpdateProgress(new ProgressInfo("双语字幕已完成", completionDetail, 100));
                        SetAppStatus("已完成", SuccessColor);
                    }
                    PlayCompletionSound();
                }
                catch (OperationCanceledException)
                {
                    UpdateProgress(new ProgressInfo("任务已取消", "已完成的识别和翻译断点仍然保留，下次可以继续。", 0));
                    SetAppStatus("已取消", WarningColor);
                }
                catch (Exception ex)
                {
                    Logger.Write("Job failed: " + ex);
                    UpdateProgress(new ProgressInfo("字幕任务未完成", ex.Message, 0));
                    SetAppStatus("需要处理", Color.FromArgb(179, 67, 67));
                }
                finally
                {
                    QueueManager.Complete(requestPath);
                    source.Dispose();
                    lock (jobLock) { jobCancellation = null; activeMediaPath = ""; }
                    SetProcessingState(request.MediaPath, false);
                }
            }
        }

        private bool EnsureApiKey()
        {
            if (!string.IsNullOrWhiteSpace(CredentialStore.ReadApiKey())) return true;
            if (InvokeRequired) return (bool)Invoke(new Func<bool>(EnsureApiKey));
            tabs.SelectedTab = settingsTab;
            settingsStatusLabel.ForeColor = WarningColor;
            settingsStatusLabel.Text = "任务需要 API Key，请在此保存后重新开始。";
            ShowAndActivate();
            return false;
        }

        private void SetProcessingState(string path, bool processing)
        {
            if (IsDisposed || shuttingDown) return;
            if (InvokeRequired) { BeginInvoke(new Action<string, bool>(SetProcessingState), path, processing); return; }
            if (processing)
            {
                processingMediaPath = path;
                toolTips.SetToolTip(elapsedLabel, "当前任务：" + path);
                processingStarted = DateTime.UtcNow;
                processingVisible = true;
                SelectMedia(path);
                UpdateProgress(new ProgressInfo("准备视频", Path.GetFileName(path), 0));
                SetAppStatus("正在处理", AccentColor);
                UpdateElapsed();
            }
            else
            {
                UpdateElapsed();
                processingVisible = false;
                if (File.Exists(selectedPathBox.Text)) SelectMedia(selectedPathBox.Text);
                if (tabs.SelectedTab == libraryTab) RefreshLibrary();
            }
            stageStrip.Running = processing; stageStrip.Invalidate();
            startButton.Enabled = !processing && File.Exists(selectedPathBox.Text);
            cancelButton.Enabled = processing;
        }

        private void UpdateProgress(ProgressInfo info)
        {
            if (IsDisposed || shuttingDown) return;
            if (InvokeRequired) { BeginInvoke(new Action<ProgressInfo>(UpdateProgress), info); return; }
            int percent = Math.Max(0, Math.Min(100, info.Percent));
            progressStageLabel.Text = info.Stage;
            progressDetailLabel.Text = info.Detail;
            progressBar.Value = percent;
            progressBar.FillColor = percent == 100 ? SuccessColor : AccentColor;
            stageStrip.Percent = percent; stageStrip.Invalidate();
            toolTips.SetToolTip(progressDetailLabel, info.Detail);
            progressPercentLabel.Text = percent + "%";
            trayIcon.Text = Truncate(info.Stage, 63);
        }

        private void CancelClicked(object sender, EventArgs e)
        {
            CancellationTokenSource source;
            lock (jobLock) source = jobCancellation;
            if (source != null) source.Cancel();
            cancelButton.Enabled = false;
            progressDetailLabel.Text = "正在安全停止当前任务……";
        }

        private void HandleMonitorChange(string path)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(HandleMonitorChange), path); return; }
            if (path == null)
            {
                promptSession.Reset();
                detectedMediaPath = "";
                SetDetectedCardVisible(false);
                if (string.IsNullOrEmpty(activeMediaPath)) SetAppStatus("正在监控", Color.FromArgb(8, 99, 164));
                return;
            }
            HandleDetectedMedia(path);
        }

        private void HandleDetectedMedia(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !promptSession.Observe(path)) return;
            detectedMediaPath = Path.GetFullPath(path);
            SelectMedia(detectedMediaPath);
            detectedNameLabel.Text = Path.GetFileName(detectedMediaPath);
            detectedPathLabel.Text = detectedMediaPath;
            SetDetectedCardVisible(true);
            tabs.SelectedTab = taskTab;
            SetAppStatus("发现新视频", WarningColor);
            ShowAndActivate();
        }

        private void ApplyMonitorSetting(bool enabled)
        {
            if (enabled)
            {
                if (monitor == null) monitor = new PotPlayerMonitor(HandleMonitorChange);
                monitor.Start();
                if (string.IsNullOrEmpty(activeMediaPath)) SetAppStatus("正在监控", Color.FromArgb(8, 99, 164));
            }
            else
            {
                if (monitor != null) monitor.Stop();
                SetAppStatus("监控已关闭", Color.FromArgb(100, 113, 132));
            }
        }

        private void LoadSettingsIntoUi()
        {
            AppConfig config = AppConfig.Load();
            UpdateConfigurationSummary(config);
            apiUrlBox.Text = config.ApiBaseUrl;
            modelBox.Text = config.Model;
            sourceLanguageBox.SourceLanguage = config.SourceLanguage;
            hubPathBox.Text = config.SubtitleHubPath;
            monitorCheck.Checked = config.MonitorPotPlayer;
            startupCheck.Checked = config.StartWithWindows;
            bool stored = !string.IsNullOrWhiteSpace(CredentialStore.ReadApiKey());
            apiStoredLabel.Text = stored ? "已安全保存 API Key；不修改时可留空。" : "尚未保存 API Key。";
            apiStoredLabel.ForeColor = stored ? SuccessColor : WarningColor;
        }

        private AppConfig ReadSettingsFromUi()
        {
            AppConfig config = AppConfig.Load();
            config.ApiBaseUrl = apiUrlBox.Text.Trim();
            config.Model = modelBox.Text.Trim();
            config.SourceLanguage = sourceLanguageBox.SourceLanguage;
            if (string.IsNullOrWhiteSpace(hubPathBox.Text)) throw new InvalidOperationException("字幕库目录不能为空。");
            config.SubtitleHubPath = Path.GetFullPath(hubPathBox.Text.Trim());
            Directory.CreateDirectory(config.SubtitleHubPath);
            config.MonitorPotPlayer = monitorCheck.Checked;
            config.StartWithWindows = startupCheck.Checked;
            config.UiSettingsVersion = 2;
            ApiEndpoint.ChatCompletions(config.ApiBaseUrl);
            if (string.IsNullOrWhiteSpace(config.Model)) throw new InvalidOperationException("模型名称不能为空。");
            return config;
        }

        private void ChooseHubClicked(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择字幕库目录";
                dialog.ShowNewFolderButton = true;
                if (Directory.Exists(hubPathBox.Text)) dialog.SelectedPath = hubPathBox.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) hubPathBox.Text = dialog.SelectedPath;
            }
        }

        private void SaveSettingsClicked(object sender, EventArgs e)
        {
            try
            {
                AppConfig config = ReadSettingsFromUi();
                if (!string.IsNullOrWhiteSpace(apiKeyBox.Text)) { CredentialStore.SaveApiKey(apiKeyBox.Text); apiKeyBox.Clear(); }
                AppConfig.Save(config);
                StartupManager.SetEnabled(config.StartWithWindows);
                ApplyMonitorSetting(config.MonitorPotPlayer);
                bool stored = !string.IsNullOrWhiteSpace(CredentialStore.ReadApiKey());
                apiStoredLabel.Text = stored ? "已安全保存 API Key；不修改时可留空。" : "尚未保存 API Key。";
                apiStoredLabel.ForeColor = stored ? SuccessColor : WarningColor;
                settingsStatusLabel.ForeColor = SuccessColor;
                settingsStatusLabel.Text = "设置已保存，对新提交的任务生效。";
                UpdateConfigurationSummary(config);
            }
            catch (Exception ex)
            {
                settingsStatusLabel.ForeColor = Color.FromArgb(179, 67, 67);
                settingsStatusLabel.Text = ex.Message;
            }
        }

        private void TestConnectionClicked(object sender, EventArgs e)
        {
            AppConfig config;
            string key;
            try
            {
                config = ReadSettingsFromUi();
                key = string.IsNullOrWhiteSpace(apiKeyBox.Text) ? CredentialStore.ReadApiKey() : apiKeyBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("请先填写或保存 API Key。");
            }
            catch (Exception ex)
            {
                settingsStatusLabel.ForeColor = Color.FromArgb(179, 67, 67);
                settingsStatusLabel.Text = ex.Message;
                return;
            }

            testButton.Enabled = false;
            settingsStatusLabel.ForeColor = AccentColor;
            settingsStatusLabel.Text = "正在测试连接……";
            Thread thread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    string result = ModelConnectionTester.Test(config, key, CancellationToken.None);
                    BeginInvoke((MethodInvoker)delegate { settingsStatusLabel.ForeColor = SuccessColor; settingsStatusLabel.Text = result; testButton.Enabled = true; });
                }
                catch (Exception ex)
                {
                    BeginInvoke((MethodInvoker)delegate { settingsStatusLabel.ForeColor = Color.FromArgb(179, 67, 67); settingsStatusLabel.Text = Truncate(ex.Message, 110); testButton.Enabled = true; });
                }
            }));
            thread.IsBackground = true;
            thread.Name = "API connection test";
            thread.Start();
        }

        private void StartWakeListener()
        {
            if (wakeEvent == null || (wakeThread != null && wakeThread.IsAlive)) return;
            wakeThread = new Thread(new ThreadStart(delegate
            {
                while (!shuttingDown)
                {
                    if (!wakeEvent.WaitOne(1000)) continue;
                    if (shuttingDown) break;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            string media = DetectionInbox.Take();
                            if (!string.IsNullOrEmpty(media)) HandleDetectedMedia(media);
                            else ShowAndActivate();
                        });
                    }
                    catch { }
                }
            }));
            wakeThread.IsBackground = true;
            wakeThread.Name = "Application wake listener";
            wakeThread.Start();
        }

        private void ShowFromTray() { ShowAndActivate(); }

        private void ShowAndActivate()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            TopMost = true;
            Activate();
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 600 };
            timer.Tick += delegate { timer.Stop(); timer.Dispose(); TopMost = false; };
            timer.Start();
        }

        private void SetAppStatus(string text, Color color)
        {
            if (IsDisposed || shuttingDown) return;
            if (InvokeRequired) { BeginInvoke(new Action<string, Color>(SetAppStatus), text, color); return; }
            appStatusLabel.Text = "●  " + text;
            appStatusLabel.ForeColor = color;
            appStatusLabel.BackColor = MoyuDrawing.Blend(Color.White, color, 0.10f);
            appStatusLabel.Invalidate();
        }

        private static void PlayCompletionSound()
        {
            try { SystemSounds.Asterisk.Play(); } catch { }
        }

        private static string CombineWarnings(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first)) return second;
            if (string.IsNullOrWhiteSpace(second)) return first;
            return first.Trim() + "；" + second.Trim();
        }

        private static string Truncate(string text, int length)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= length) return text ?? "";
            return text.Substring(0, length - 1) + "…";
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            shuttingDown = true;
            if (monitor != null) monitor.Dispose();
            CancellationTokenSource source;
            lock (jobLock) source = jobCancellation;
            if (source != null) source.Cancel();
            jobSignal.Set();
            trayIcon.Visible = false;
        }
    }

    internal sealed class PlaybackPromptSession
    {
        private string currentPath = "";
        private string dismissedPath = "";

        public bool Observe(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string fullPath = Path.GetFullPath(path);
            if (string.Equals(currentPath, fullPath, StringComparison.OrdinalIgnoreCase)) return false;
            currentPath = fullPath;
            dismissedPath = "";
            return true;
        }

        public void Dismiss(string path) { if (!string.IsNullOrWhiteSpace(path)) dismissedPath = Path.GetFullPath(path); }
        public bool IsDismissed(string path) { return !string.IsNullOrWhiteSpace(path) && string.Equals(dismissedPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase); }
        public void Reset() { currentPath = ""; dismissedPath = ""; }
    }

    internal sealed class CardPanel : Panel
    {
        public Color BorderColor { get; set; }
        public Color AccentColor { get; set; }
        public int CornerRadius { get; set; }

        public CardPanel()
        {
            BorderColor = MoyuPalette.Border;
            AccentColor = Color.Transparent;
            CornerRadius = 12;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color outside = Parent == null ? MoyuPalette.Page : Parent.BackColor;
            e.Graphics.Clear(outside);
            Rectangle card = new Rectangle(1, 1, Math.Max(1, Width - 4), Math.Max(1, Height - 4));
            using (GraphicsPath path = MoyuDrawing.RoundedRectangle(card, CornerRadius))
            using (SolidBrush brush = new SolidBrush(BackColor)) e.Graphics.FillPath(brush, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle card = new Rectangle(1, 1, Math.Max(1, Width - 4), Math.Max(1, Height - 4));
            using (GraphicsPath path = MoyuDrawing.RoundedRectangle(card, CornerRadius))
            using (Pen pen = new Pen(BorderColor)) e.Graphics.DrawPath(pen, path);
            if (AccentColor.A > 0)
            {
                Rectangle accent = new Rectangle(1, 18, 5, Math.Max(12, Height - 40));
                using (GraphicsPath path = MoyuDrawing.RoundedRectangle(accent, 3))
                using (SolidBrush brush = new SolidBrush(AccentColor)) e.Graphics.FillPath(brush, path);
            }
            base.OnPaint(e);
        }
    }

    internal sealed class SourceLanguageComboBox : ComboBox
    {
        public SourceLanguageComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (SourceLanguageOption option in SourceLanguages.Options) Items.Add(option);
            SourceLanguage = "ja";
        }

        public string SourceLanguage
        {
            get
            {
                SourceLanguageOption option = SelectedItem as SourceLanguageOption;
                return option == null ? "ja" : option.Code;
            }
            set { SelectedItem = SourceLanguages.Get(value); }
        }
    }

    internal static class UiSelfTest
    {
        public static int Run()
        {
            List<string> results = new List<string>();
            string resultPath = Path.Combine(StoragePaths.Logs, "ui-self-test-result.txt");
            string tempRoot = Path.Combine(Path.GetTempPath(), "PotPlayerAiSubtitleUiTest-" + Guid.NewGuid().ToString("N"));
            byte[] previousCurrent = File.Exists(StoragePaths.CurrentMediaFile) ? File.ReadAllBytes(StoragePaths.CurrentMediaFile) : null;
            List<string> queueBefore = Directory.GetFiles(StoragePaths.Queue, "request-*.json").ToList();
            bool success = false;
            try
            {
                Directory.CreateDirectory(tempRoot);
                string media = Path.Combine(tempRoot, "manual-selection-test.mkv");
                File.WriteAllBytes(media, new byte[] { 1, 2, 3, 4, 5 });
                QueueManager.Notify(media);
                List<string> createdRequests = Directory.GetFiles(StoragePaths.Queue, "request-*.json").Except(queueBefore, StringComparer.OrdinalIgnoreCase).ToList();
                if (createdRequests.Count != 1) throw new Exception("手动选择文件未正确加入队列。");
                results.Add("PASS manual file can be queued");

                int beforeDetection = Directory.GetFiles(StoragePaths.Queue, "request-*.json").Length;
                PlaybackPromptSession session = new PlaybackPromptSession();
                if (!session.Observe(media)) throw new Exception("自动检测没有生成询问状态。");
                if (beforeDetection != Directory.GetFiles(StoragePaths.Queue, "request-*.json").Length) throw new Exception("自动检测不应直接开始任务。");
                results.Add("PASS automatic detection asks before queueing");

                session.Dismiss(media);
                if (!session.IsDismissed(media) || session.Observe(media)) throw new Exception("拒绝后同次播放仍会重复询问。");
                session.Reset();
                if (!session.Observe(media)) throw new Exception("新播放会话未恢复询问。");
                results.Add("PASS dismissed video is not prompted again in the same session");

                string serialized = AtomicJson.Serialize(AppConfig.CreateDefault());
                if (serialized.IndexOf("ApiKey", StringComparison.OrdinalIgnoreCase) >= 0) throw new Exception("API Key 不应进入普通配置。");
                results.Add("PASS API key is absent from JSON settings");

                using (SourceLanguageComboBox languageBox = new SourceLanguageComboBox())
                {
                    if (languageBox.SourceLanguage != "ja") throw new Exception("源语言控件未默认日语。");
                    foreach (SourceLanguageOption language in SourceLanguages.Options)
                    {
                        languageBox.SourceLanguage = language.Code;
                        if (languageBox.SourceLanguage != language.Code) throw new Exception("源语言选择未生效。");
                    }
                    languageBox.SelectedIndex = -1;
                    if (languageBox.SourceLanguage != "ja") throw new Exception("未选择语言时未回退日语。");
                }
                results.Add("PASS language selector supports every option and defaults to Japanese when unselected");

                string languageMedia = Path.Combine(tempRoot, "language-queued.mkv");
                File.WriteAllBytes(languageMedia, new byte[] { 6, 7, 8 });
                QueueManager.Notify(languageMedia, "en");
                QueueManager.Notify(languageMedia, "ko");
                List<JobRequest> languageRequests = Directory.GetFiles(StoragePaths.Queue, "request-*.json")
                    .Select(delegate(string path) { return AtomicJson.Read<JobRequest>(path, null); })
                    .Where(delegate(JobRequest request) { return request != null && request.MediaPath == languageMedia; }).ToList();
                if (languageRequests.Count != 2 || !languageRequests.Any(delegate(JobRequest request) { return request.SourceLanguage == "en"; })
                    || !languageRequests.Any(delegate(JobRequest request) { return request.SourceLanguage == "ko"; }))
                    throw new Exception("同一视频不同源语言的队列任务互相覆盖。");
                results.Add("PASS queued jobs retain their own source language without overwriting each other");

                if (!WatcherContract.IsPlayerName("PotPlayerMini64")
                    || !WatcherContract.IsPlayerName("potplayer")
                    || WatcherContract.IsPlayerName("AI-Subtitle-Worker"))
                    throw new Exception("PotPlayer 自动启动触发器的进程识别不正确。");
                string startupCommand = StartupManager.BuildCommand(@"C:\Program Files\AI Subtitle\AI-Subtitle-Worker.exe");
                if (!startupCommand.EndsWith("Moyu-Watcher.exe\"", StringComparison.Ordinal)
                    || !startupCommand.StartsWith("\"", StringComparison.Ordinal))
                    throw new Exception("PotPlayer 自动启动命令不正确。");
                results.Add("PASS PotPlayer-triggered startup command and process detection");

                string extensionRoot = Path.Combine(Directory.GetParent(StoragePaths.Root).FullName, "Extension");
                string[] activeScripts = Directory.Exists(extensionRoot) ? Directory.GetFiles(extensionRoot, "*.as", SearchOption.AllDirectories) : new string[0];
                if (activeScripts.Length != 0) throw new Exception("PotPlayer 扩展目录仍有活动 .as 插件。");
                results.Add("PASS no active PotPlayer AngelScript plugins");
                success = true;
            }
            catch (Exception ex)
            {
                results.Add("FAIL " + ex);
                Logger.Write("UI self-test failed: " + ex);
            }
            finally
            {
                foreach (string path in Directory.GetFiles(StoragePaths.Queue, "request-*.json").Except(queueBefore, StringComparer.OrdinalIgnoreCase))
                {
                    try { File.Delete(path); } catch { }
                }
                try
                {
                    if (previousCurrent == null) { if (File.Exists(StoragePaths.CurrentMediaFile)) File.Delete(StoragePaths.CurrentMediaFile); }
                    else File.WriteAllBytes(StoragePaths.CurrentMediaFile, previousCurrent);
                }
                catch { }
                try
                {
                    string resolved = Path.GetFullPath(tempRoot);
                    string expectedBase = Path.GetFullPath(Path.GetTempPath());
                    if (resolved.StartsWith(expectedBase, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
                }
                catch { }
                File.WriteAllLines(resultPath, results.ToArray(), new System.Text.UTF8Encoding(false));
            }
            return success ? 0 : 1;
        }
    }
}
