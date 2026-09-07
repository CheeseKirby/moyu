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
    internal sealed class MainForm : Form
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
        private string detectedMediaPath = "";

        public MainForm(bool startHidden, bool openSettings, EventWaitHandle wakeEvent)
        {
            this.startHidden = startHidden;
            this.openSettings = openSettings;
            this.wakeEvent = wakeEvent;

            Text = "魔芋";
            ClientSize = new Size(920, 760);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = PageColor;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Information;

            MoyuHeaderPanel header = new MoyuHeaderPanel { Dock = DockStyle.Top, Height = 124 };
            appStatusLabel = CreateLabel("正在准备", 740, 34, 146, 36, 9F, FontStyle.Bold, Color.White);
            appStatusLabel.TextAlign = ContentAlignment.MiddleCenter;
            appStatusLabel.BackColor = Color.FromArgb(8, 99, 164);
            MoyuDrawing.RoundControl(appStatusLabel, 18);
            appStatusLabel.SizeChanged += delegate { MoyuDrawing.RoundControl(appStatusLabel, appStatusLabel.Height / 2); };
            header.Controls.Add(appStatusLabel);

            tabs = new MoyuTabControl { Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 10F), BackColor = PageColor };
            taskTab = new TabPage("字幕任务") { BackColor = PageColor, Padding = new Padding(18) };
            settingsTab = new TabPage("模型设置") { BackColor = PageColor, Padding = new Padding(18) };
            tabs.TabPages.Add(taskTab);
            tabs.TabPages.Add(settingsTab);

            taskCanvas = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = PageColor };
            taskTab.Controls.Add(taskCanvas);

            detectedCard = NewCard(0, 0, 820, 122, Color.FromArgb(255, 251, 232));
            detectedCard.BorderColor = Color.FromArgb(244, 205, 122);
            detectedCard.AccentColor = MoyuPalette.Yellow;
            detectedCard.Visible = false;
            detectedCard.Controls.Add(CreateLabel("PotPlayer 正在播放", 22, 14, 260, 23, 9F, FontStyle.Bold, WarningColor));
            detectedNameLabel = CreateLabel("", 20, 39, 540, 27, 11F, FontStyle.Bold, TextColor);
            detectedNameLabel.AutoEllipsis = true;
            detectedPathLabel = CreateLabel("", 20, 70, 540, 24, 8.5F, FontStyle.Regular, MutedColor);
            detectedPathLabel.AutoEllipsis = true;
            Button acceptDetected = CreateButton("开始生成", MoyuPalette.Red, Color.White, 660, 20, 140, 38);
            acceptDetected.Click += delegate { if (!string.IsNullOrEmpty(detectedMediaPath)) StartJob(detectedMediaPath); };
            Button dismissDetected = CreateButton("这次不需要", Color.White, TextColor, 660, 67, 140, 34);
            dismissDetected.FlatAppearance.BorderColor = Color.FromArgb(214, 220, 229);
            dismissDetected.FlatAppearance.BorderSize = 1;
            dismissDetected.Click += delegate { promptSession.Dismiss(detectedMediaPath); SetDetectedCardVisible(false); SetAppStatus("继续监控", Color.FromArgb(8, 99, 164)); };
            detectedCard.Controls.Add(detectedNameLabel);
            detectedCard.Controls.Add(detectedPathLabel);
            detectedCard.Controls.Add(acceptDetected);
            detectedCard.Controls.Add(dismissDetected);

            selectCard = NewCard(0, 0, 820, 154, Color.White);
            selectCard.AccentColor = AccentColor;
            selectCard.Controls.Add(CreateLabel("01  选择视频", 22, 16, 300, 28, 12F, FontStyle.Bold, TextColor));
            selectCard.Controls.Add(CreateLabel("打开 PotPlayer 会自动询问，也可以在这里直接选择文件。", 22, 47, 620, 22, 9F, FontStyle.Regular, MutedColor));
            selectedPathBox = new TextBox { Left = 22, Top = 77, Width = 536, Height = 30, ReadOnly = true, BackColor = Color.FromArgb(246, 251, 254), BorderStyle = BorderStyle.FixedSingle, ForeColor = TextColor };
            Button chooseButton = CreateButton("选择文件", AccentSoft, AccentColor, 570, 74, 106, 34);
            chooseButton.Click += ChooseVideoClicked;
            startButton = CreateButton("生成双语字幕", MoyuPalette.Red, Color.White, 688, 74, 112, 34);
            startButton.Click += delegate { StartJob(selectedPathBox.Text); };
            selectedInfoLabel = CreateLabel("尚未选择视频", 20, 117, 790, 22, 8.5F, FontStyle.Regular, MutedColor);
            selectCard.Controls.Add(selectedPathBox);
            selectCard.Controls.Add(chooseButton);
            selectCard.Controls.Add(startButton);
            selectCard.Controls.Add(selectedInfoLabel);

            progressCard = NewCard(0, 170, 820, 216, Color.White);
            progressCard.AccentColor = MoyuPalette.Yellow;
            progressCard.Controls.Add(CreateLabel("02  生成字幕", 22, 16, 180, 28, 12F, FontStyle.Bold, TextColor));
            progressStageLabel = CreateLabel("等待任务", 20, 54, 590, 27, 11F, FontStyle.Bold, TextColor);
            progressStageLabel.AutoEllipsis = true;
            progressPercentLabel = CreateLabel("0%", 720, 53, 70, 28, 11F, FontStyle.Bold, AccentColor);
            progressPercentLabel.TextAlign = ContentAlignment.MiddleRight;
            progressDetailLabel = CreateLabel("选择视频，或保持本工具在托盘运行后用 PotPlayer 打开视频。", 20, 85, 770, 44, 9F, FontStyle.Regular, MutedColor);
            progressDetailLabel.AutoEllipsis = true;
            progressBar = new MoyuProgressBar { Left = 20, Top = 136, Width = 780, Height = 18, Minimum = 0, Maximum = 100, FillColor = AccentColor };
            cancelButton = CreateButton("取消当前任务", Color.FromArgb(255, 238, 239), MoyuPalette.Red, 650, 169, 140, 32);
            cancelButton.Enabled = false;
            cancelButton.Click += CancelClicked;
            progressCard.Controls.Add(progressStageLabel);
            progressCard.Controls.Add(progressPercentLabel);
            progressCard.Controls.Add(progressDetailLabel);
            progressCard.Controls.Add(progressBar);
            progressCard.Controls.Add(cancelButton);
            progressCard.Controls.Add(CreateLabel("处理在播放器外进行，PotPlayer 可以继续正常播放。", 20, 174, 560, 24, 8.5F, FontStyle.Regular, SuccessColor));

            taskCanvas.Controls.Add(detectedCard);
            taskCanvas.Controls.Add(selectCard);
            taskCanvas.Controls.Add(progressCard);
            taskFooterLabel = CreateLabel("魔芋只负责安静地生成字幕；字幕发现与加载仍交给 PotPlayer。", 4, 404, 800, 28, 8.5F, FontStyle.Regular, MutedColor);
            taskCanvas.Controls.Add(taskFooterLabel);
            SetDetectedCardVisible(false);

            CardPanel settingsCard = new CardPanel { Dock = DockStyle.Top, Height = 535, BackColor = Color.White, AccentColor = AccentColor };
            settingsTab.Controls.Add(settingsCard);
            settingsCard.Controls.Add(CreateLabel("模型、密钥与保存位置", 24, 20, 420, 32, 13F, FontStyle.Bold, TextColor));
            settingsCard.Controls.Add(CreateLabel("API Key 只保存在 Windows 凭据管理器，不会写入 settings.json 或日志。", 24, 55, 700, 24, 9F, FontStyle.Regular, MutedColor));
            AddFieldLabel(settingsCard, "API 请求地址", 24, 96);
            apiUrlBox = CreateInput(24, 120, 772);
            settingsCard.Controls.Add(apiUrlBox);
            AddFieldLabel(settingsCard, "API Key", 24, 168);
            apiKeyBox = CreateInput(24, 192, 666);
            apiKeyBox.UseSystemPasswordChar = true;
            settingsCard.Controls.Add(apiKeyBox);
            Button showKeyButton = CreateButton("显示", AccentSoft, AccentColor, 702, 190, 94, 34);
            showKeyButton.Click += delegate { apiKeyBox.UseSystemPasswordChar = !apiKeyBox.UseSystemPasswordChar; showKeyButton.Text = apiKeyBox.UseSystemPasswordChar ? "显示" : "隐藏"; };
            settingsCard.Controls.Add(showKeyButton);
            apiStoredLabel = CreateLabel("", 24, 230, 600, 22, 8.5F, FontStyle.Regular, MutedColor);
            settingsCard.Controls.Add(apiStoredLabel);
            AddFieldLabel(settingsCard, "模型名称", 24, 260);
            modelBox = CreateInput(24, 284, 772);
            settingsCard.Controls.Add(modelBox);

            AddFieldLabel(settingsCard, "字幕库目录", 24, 328);
            hubPathBox = CreateInput(24, 352, 666);
            settingsCard.Controls.Add(hubPathBox);
            Button chooseHubButton = CreateButton("选择目录", AccentSoft, AccentColor, 702, 350, 94, 34);
            chooseHubButton.Click += ChooseHubClicked;
            settingsCard.Controls.Add(chooseHubButton);

            monitorCheck = new CheckBox { Left = 24, Top = 402, Width = 420, Height = 28, Text = "监控 PotPlayer 打开的视频并在工具内询问", ForeColor = TextColor };
            startupCheck = new CheckBox { Left = 24, Top = 435, Width = 420, Height = 28, Text = "使用 PotPlayer 时自动启动魔芋（推荐）", ForeColor = TextColor };
            settingsCard.Controls.Add(monitorCheck);
            settingsCard.Controls.Add(startupCheck);
            settingsStatusLabel = CreateLabel("", 24, 477, 560, 45, 9F, FontStyle.Regular, MutedColor);
            settingsCard.Controls.Add(settingsStatusLabel);
            testButton = CreateButton("测试连接", AccentSoft, AccentColor, 584, 473, 100, 36);
            testButton.Click += TestConnectionClicked;
            Button saveButton = CreateButton("保存设置", MoyuPalette.Red, Color.White, 696, 473, 100, 36);
            saveButton.Click += SaveSettingsClicked;
            settingsCard.Controls.Add(testButton);
            settingsCard.Controls.Add(saveButton);

            Controls.Add(tabs);
            Controls.Add(header);

            trayIcon = new NotifyIcon { Icon = Icon, Text = "魔芋 · AI 字幕", Visible = true };
            trayIcon.DoubleClick += delegate { ShowFromTray(); };
            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("打开魔芋", null, delegate { ShowFromTray(); });
            trayMenu.Items.Add("选择视频", null, delegate { ShowFromTray(); tabs.SelectedTab = taskTab; ChooseVideoClicked(null, EventArgs.Empty); });
            trayMenu.Items.Add("模型设置", null, delegate { ShowFromTray(); tabs.SelectedTab = settingsTab; });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出", null, delegate { allowClose = true; Close(); });
            trayIcon.ContextMenuStrip = trayMenu;

            LoadSettingsIntoUi();
            Shown += FormShown;
            FormClosing += OnFormClosing;
        }

        private static CardPanel NewCard(int left, int top, int width, int height, Color color)
        {
            return new CardPanel { Left = left, Top = top, Width = width, Height = height, BackColor = color };
        }

        private static Label CreateLabel(string text, int left, int top, int width, int height, float size, FontStyle style, Color color)
        {
            return new Label { Text = text, Left = left, Top = top, Width = width, Height = height, Font = new Font("Microsoft YaHei UI", size, style), ForeColor = color };
        }


        private static Button CreateButton(string text, Color backColor, Color foreColor, int left, int top, int width, int height)
        {
            MoyuButton button = new MoyuButton { Text = text, Left = left, Top = top, Width = width, Height = height, BackColor = backColor, ForeColor = foreColor, Cursor = Cursors.Hand, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
            button.HoverBackColor = MoyuDrawing.Blend(backColor, Color.White, 0.14f);
            return button;
        }

        private static TextBox CreateInput(int left, int top, int width)
        {
            return new TextBox { Left = left, Top = top, Width = width, Height = 30, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(246, 251, 254), ForeColor = TextColor };
        }

        private static void AddFieldLabel(Control parent, string text, int left, int top)
        {
            parent.Controls.Add(CreateLabel(text, left, top, 300, 22, 9F, FontStyle.Bold, TextColor));
        }

        private void SetDetectedCardVisible(bool visible)
        {
            detectedCard.Visible = visible;
            int offset = visible ? 138 : 0;
            selectCard.Top = offset;
            progressCard.Top = offset + 170;
            taskFooterLabel.Top = offset + 404;
            taskCanvas.AutoScrollMinSize = new Size(0, taskFooterLabel.Bottom + 8);
        }
        private void FormShown(object sender, EventArgs e)
        {
            AppConfig config = AppConfig.Load();
            StartupManager.SetEnabled(config.StartWithWindows);
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
            selectedInfoLabel.Text = Path.GetFileName(fullPath) + "  ·  " + FormatSize(info.Length);
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
                lock (jobLock)
                {
                    if (!string.IsNullOrEmpty(activeMediaPath) && string.Equals(activeMediaPath, Path.GetFullPath(mediaPath), StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateProgress(new ProgressInfo("正在处理这个视频", "无需重复提交，请等待当前任务完成。", progressBar.Value));
                        return;
                    }
                }
                QueueManager.Notify(mediaPath);
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
                lock (jobLock) { jobCancellation = source; activeMediaPath = request.MediaPath; }
                SetProcessingState(request.MediaPath, true);
                try
                {
                    if (!File.Exists(request.MediaPath)) throw new FileNotFoundException("视频文件已不存在。", request.MediaPath);
                    IProgress<ProgressInfo> progress = new Progress<ProgressInfo>(UpdateProgress);
                    SubtitlePipelineRunner runner = new SubtitlePipelineRunner(AppConfig.Load(), progress, EnsureApiKey);
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
            if (InvokeRequired) { BeginInvoke(new Action<string, bool>(SetProcessingState), path, processing); return; }
            if (processing)
            {
                SelectMedia(path);
                startButton.Enabled = false;
                cancelButton.Enabled = true;
                SetAppStatus("正在处理", AccentColor);
            }
            else
            {
                startButton.Enabled = true;
                cancelButton.Enabled = false;
            }
        }

        private void UpdateProgress(ProgressInfo info)
        {
            if (InvokeRequired) { BeginInvoke(new Action<ProgressInfo>(UpdateProgress), info); return; }
            int percent = Math.Max(0, Math.Min(100, info.Percent));
            progressStageLabel.Text = info.Stage;
            progressDetailLabel.Text = info.Detail;
            progressBar.Value = percent;
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
            detectedCard.BringToFront();
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
            apiUrlBox.Text = config.ApiBaseUrl;
            modelBox.Text = config.Model;
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
                settingsStatusLabel.Text = "设置已保存。";
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
            WindowState = FormWindowState.Normal;
            TopMost = true;
            Activate();
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 600 };
            timer.Tick += delegate { timer.Stop(); timer.Dispose(); TopMost = false; };
            timer.Start();
        }

        private void SetAppStatus(string text, Color color)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string, Color>(SetAppStatus), text, color); return; }
            appStatusLabel.Text = text;
            appStatusLabel.BackColor = color;
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
            trayIcon.Dispose();
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

    internal static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "魔芋";
        private const string LegacyValueName = "PotPlayer AI Subtitle";

        internal static string BuildCommand(string executablePath)
        {
            return "\"" + executablePath + "\" --wait-for-potplayer";
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null) return;
                key.DeleteValue(LegacyValueName, false);
                if (enabled) key.SetValue(ValueName, BuildCommand(Application.ExecutablePath), RegistryValueKind.String);
                else key.DeleteValue(ValueName, false);
            }
        }
    }

    internal sealed class CardPanel : Panel
    {
        public Color BorderColor { get; set; }
        public Color AccentColor { get; set; }
        public int CornerRadius { get; set; }

        public CardPanel()
        {
            BorderColor = Color.FromArgb(205, 228, 240);
            AccentColor = Color.Transparent;
            CornerRadius = 18;
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

                if (!PotPlayerStartupWatcher.IsPotPlayerProcessName("PotPlayerMini64")
                    || !PotPlayerStartupWatcher.IsPotPlayerProcessName("potplayer")
                    || PotPlayerStartupWatcher.IsPotPlayerProcessName("AI-Subtitle-Worker"))
                    throw new Exception("PotPlayer 自动启动触发器的进程识别不正确。");
                string startupCommand = StartupManager.BuildCommand(@"C:\Program Files\AI Subtitle\AI-Subtitle-Worker.exe");
                if (!startupCommand.EndsWith(" --wait-for-potplayer", StringComparison.Ordinal)
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
