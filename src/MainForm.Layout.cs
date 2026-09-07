using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PotPlayerAiSubtitle
{
    internal sealed partial class MainForm
    {
        private readonly List<MoyuNavButton> navigation = new List<MoyuNavButton>();
        private TabPage libraryTab;
        private Label breadcrumbLabel;
        private Label selectedFileLabel;
        private Label modelSummaryLabel;
        private Label elapsedLabel;
        private Button languageSummaryButton;
        private MoyuStageStrip stageStrip;
        private System.Windows.Forms.Timer uiTimer;
        private DateTime? processingStarted;
        private bool processingVisible;
        private string processingMediaPath = "";
        private ToolTip toolTips;
        private bool uiResourcesDisposed;

        public MainForm(bool startHidden, bool openSettings, EventWaitHandle wakeEvent)
        {
            this.startHidden = startHidden; this.openSettings = openSettings; this.wakeEvent = wakeEvent;
            SuspendLayout();
            Text = "魔芋 · 双语字幕工作台";
            AutoScaleDimensions = new SizeF(96f, 96f); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1180, 820); MinimumSize = new Size(1040, 740);
            StartPosition = FormStartPosition.CenterScreen; BackColor = PageColor;
            Font = new Font("Microsoft YaHei UI", 9F); KeyPreview = true;
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Information;
            toolTips = new ToolTip { AutoPopDelay = 10000, InitialDelay = 500 };

            Panel sidebar = new Panel { Dock = DockStyle.Left, Width = 204, BackColor = MoyuPalette.Sidebar };
            sidebar.Controls.Add(new MoyuBrandPanel { Dock = DockStyle.Top, Height = 110, BrandIcon = Icon });
            sidebar.Controls.Add(CreateLabel("工作空间", 28, 116, 148, 22, 8F, FontStyle.Regular, Color.FromArgb(115, 134, 159)));
            Label sidebarNote = CreateLabel("安静生成，继续播放。\n为你的每一段对白。", 24, 12, 160, 52, 9F, FontStyle.Regular, Color.FromArgb(155, 173, 197));
            Panel sidebarBottom = new Panel { Dock = DockStyle.Bottom, Height = 96, BackColor = sidebar.BackColor };
            sidebarBottom.Controls.Add(sidebarNote);
            sidebarBottom.Controls.Add(CreateLabel("MOYU  /  DESKTOP", 24, 66, 164, 18, 7.5F, FontStyle.Regular, Color.FromArgb(99, 120, 149)));
            sidebar.Controls.Add(sidebarBottom);

            Panel workspace = new Panel { Dock = DockStyle.Fill, BackColor = PageColor };
            Panel header = new Panel { Width = 976, Dock = DockStyle.Top, Height = 70, BackColor = Color.White };
            breadcrumbLabel = CreateLabel("工作空间   /   字幕任务", 30, 24, 480, 24, 9F, FontStyle.Regular, MutedColor);
            header.Controls.Add(breadcrumbLabel);
            appStatusLabel = CreateLabel("●  正在准备", 756, 19, 180, 32, 8.5F, FontStyle.Bold, AccentColor);
            appStatusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right; appStatusLabel.TextAlign = ContentAlignment.MiddleCenter;
            appStatusLabel.BackColor = AccentSoft; header.Controls.Add(appStatusLabel);
            header.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = MoyuPalette.Border });
            Panel footer = new Panel { Width = 976, Dock = DockStyle.Bottom, Height = 34, BackColor = Color.White };
            footer.Controls.Add(CreateLabel("本地识别  /  联网翻译  /  双语归档", 30, 8, 400, 20, 8F, FontStyle.Regular, MutedColor));
            modelSummaryLabel = CreateLabel("", 476, 8, 460, 20, 8F, FontStyle.Regular, MutedColor);
            modelSummaryLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right; modelSummaryLabel.TextAlign = ContentAlignment.MiddleRight;
            footer.Controls.Add(modelSummaryLabel);
            footer.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = MoyuPalette.Border });

            tabs = new MoyuPageHost { Dock = DockStyle.Fill };
            taskTab = new TabPage("字幕任务") { BackColor = PageColor };
            settingsTab = new TabPage("模型设置") { BackColor = PageColor };
            libraryTab = new TabPage("字幕库") { BackColor = PageColor };
            tabs.TabPages.AddRange(new[] { taskTab, settingsTab, libraryTab });
            tabs.SelectedIndex = 0;
            AddNavigation(sidebar, taskTab, "task", 154);
            AddNavigation(sidebar, libraryTab, "library", 210);
            AddNavigation(sidebar, settingsTab, "settings", 266);
            tabs.SelectedIndexChanged += delegate { UpdateNavigation(); if (tabs.SelectedTab == libraryTab) RefreshLibrary(); };

            FlowLayoutPanel taskFlow = NewPageFlow(); taskCanvas = taskFlow; taskTab.Controls.Add(taskFlow);
            taskFlow.Controls.Add(PageHeading("字幕任务", "把视频交给魔芋，让每一句对白都有清晰的表达。"));
            detectedCard = NewCard(0, 0, 860, 114, Color.FromArgb(255, 251, 240));
            detectedCard.BorderColor = Color.FromArgb(239, 222, 186); detectedCard.Visible = false;
            detectedCard.Controls.Add(CreateLabel("●  PotPlayer 发现新视频", 24, 15, 470, 24, 9F, FontStyle.Bold, WarningColor));
            detectedNameLabel = CreateLabel("", 24, 44, 600, 26, 11F, FontStyle.Bold, TextColor); Stretch(detectedNameLabel);
            detectedPathLabel = CreateLabel("", 24, 76, 600, 20, 8F, FontStyle.Regular, MutedColor); Stretch(detectedPathLabel);
            Button acceptDetected = CreateButton("生成字幕", AccentColor, Color.White, 704, 16, 130, 36); AnchorRight(acceptDetected);
            acceptDetected.Click += delegate { if (!string.IsNullOrEmpty(detectedMediaPath)) StartJob(detectedMediaPath); };
            Button dismissDetected = CreateButton("这次不需要", Color.FromArgb(255, 251, 240), MutedColor, 704, 61, 130, 32); AnchorRight(dismissDetected);
            dismissDetected.Click += delegate { promptSession.Dismiss(detectedMediaPath); SetDetectedCardVisible(false); SetAppStatus("继续监控", AccentColor); };
            detectedCard.Controls.AddRange(new Control[] { detectedNameLabel, detectedPathLabel, acceptDetected, dismissDetected });
            taskFlow.Controls.Add(detectedCard);

            selectCard = NewCard(0, 0, 860, 232, Color.White);
            selectCard.Controls.Add(CreateLabel("01   添加视频", 24, 18, 380, 25, 11F, FontStyle.Bold, TextColor));
            CardPanel dropZone = NewCard(24, 55, 810, 107, PageColor); dropZone.CornerRadius = 10; Stretch(dropZone);
            Label videoMark = CreateLabel("CC", 18, 23, 46, 46, 15F, FontStyle.Bold, AccentColor);
            videoMark.BackColor = AccentSoft; videoMark.TextAlign = ContentAlignment.MiddleCenter; dropZone.Controls.Add(videoMark);
            selectedFileLabel = CreateLabel("把视频拖到这里", 82, 13, 548, 29, 12F, FontStyle.Bold, TextColor); Stretch(selectedFileLabel);
            selectedInfoLabel = CreateLabel("也可以选择文件，或等待 PotPlayer 自动发现", 82, 44, 548, 23, 9F, FontStyle.Regular, MutedColor); Stretch(selectedInfoLabel);
            selectedPathBox = new TextBox { Left = 82, Top = 72, Width = 548, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = PageColor, ForeColor = MutedColor, TabStop = false, AccessibleName = "已选择的视频路径" }; Stretch(selectedPathBox);
            Button chooseButton = CreateButton("选择视频", Color.White, AccentColor, 651, 33, 137, 40); AnchorRight(chooseButton);
            chooseButton.FlatAppearance.BorderSize = 1; chooseButton.FlatAppearance.BorderColor = MoyuPalette.Border;
            chooseButton.Click += ChooseVideoClicked;
            dropZone.Controls.AddRange(new Control[] { selectedFileLabel, selectedInfoLabel, selectedPathBox, chooseButton });
            EnableVideoDrop(dropZone);
            selectCard.Controls.Add(dropZone);
            languageSummaryButton = CreateButton("日语 → 简体中文   更改", AccentSoft, AccentColor, 24, 179, 248, 36);
            languageSummaryButton.Click += delegate { tabs.SelectedTab = settingsTab; sourceLanguageBox.Focus(); };
            startButton = CreateButton("生成双语字幕  →", AccentColor, Color.White, 664, 175, 170, 42); AnchorRight(startButton); startButton.Enabled = false;
            startButton.Click += delegate { StartJob(selectedPathBox.Text); };
            selectCard.Controls.AddRange(new Control[] { languageSummaryButton, startButton });
            taskFlow.Controls.Add(selectCard);

            progressCard = NewCard(0, 0, 860, 222, Color.White);
            progressCard.Controls.Add(CreateLabel("02   处理进度", 24, 18, 210, 25, 11F, FontStyle.Bold, TextColor));
            elapsedLabel = CreateLabel("等待开始", 260, 21, 320, 22, 8.5F, FontStyle.Regular, MutedColor);
            cancelButton = CreateButton("取消任务", PageColor, MutedColor, 724, 15, 110, 34); AnchorRight(cancelButton); cancelButton.Enabled = false; cancelButton.Click += CancelClicked;
            progressStageLabel = CreateLabel("准备好，就开始吧", 24, 59, 654, 30, 14F, FontStyle.Bold, TextColor); Stretch(progressStageLabel);
            progressPercentLabel = CreateLabel("0%", 708, 54, 126, 38, 23F, FontStyle.Bold, AccentColor); AnchorRight(progressPercentLabel); progressPercentLabel.TextAlign = ContentAlignment.MiddleRight;
            progressDetailLabel = CreateLabel("识别、翻译与保存都在后台完成，不打断你的观看。", 24, 99, 810, 35, 9F, FontStyle.Regular, MutedColor); Stretch(progressDetailLabel);
            progressBar = new MoyuProgressBar { Left = 24, Top = 145, Width = 810, Height = 7, Minimum = 0, Maximum = 100 }; Stretch(progressBar);
            stageStrip = new MoyuStageStrip { Left = 24, Top = 174, Width = 810, Height = 30, Font = Font }; Stretch(stageStrip);
            progressCard.Controls.AddRange(new Control[] { elapsedLabel, cancelButton, progressStageLabel, progressPercentLabel, progressDetailLabel, progressBar, stageStrip });
            taskFlow.Controls.Add(progressCard);
            taskFooterLabel = CreateLabel("字幕生成后保存在视频旁，并将双语、源语言和中文三个版本归档到字幕库。", 0, 0, 860, 30, 8.5F, FontStyle.Regular, MutedColor);
            taskFlow.Controls.Add(taskFooterLabel);

            FlowLayoutPanel settingsFlow = NewPageFlow();
            settingsFlow.Controls.Add(PageHeading("模型设置", "连接你的翻译模型，选择语言与字幕保存方式。"));
            CardPanel serviceCard = NewCard(0, 0, 860, 266, Color.White);
            serviceCard.Controls.Add(CreateLabel("翻译服务", 24, 18, 600, 27, 11F, FontStyle.Bold, TextColor));
            AddFieldLabel(serviceCard, "API 请求地址", 24, 57);
            apiUrlBox = new TextBox { AccessibleName = "API 请求地址" }; FrameInput(serviceCard, apiUrlBox, 24, 82, 810, true);
            AddFieldLabel(serviceCard, "模型名称", 24, 140);
            modelBox = new TextBox { AccessibleName = "模型名称" }; FrameInput(serviceCard, modelBox, 24, 165, 388, true);
            Label keyLabel = CreateLabel("API Key", 440, 140, 380, 22, 9F, FontStyle.Bold, TextColor); AnchorRight(keyLabel); serviceCard.Controls.Add(keyLabel);
            apiKeyBox = new TextBox { UseSystemPasswordChar = true, AccessibleName = "API Key" }; FrameInput(serviceCard, apiKeyBox, 440, 165, 298, false);
            Button showKeyButton = CreateButton("显示", PageColor, MutedColor, 750, 165, 84, 42); AnchorRight(showKeyButton);
            showKeyButton.Click += delegate { apiKeyBox.UseSystemPasswordChar = !apiKeyBox.UseSystemPasswordChar; showKeyButton.Text = apiKeyBox.UseSystemPasswordChar ? "显示" : "隐藏"; };
            apiStoredLabel = CreateLabel("", 24, 226, 810, 23, 8.5F, FontStyle.Regular, MutedColor); Stretch(apiStoredLabel);
            serviceCard.Controls.AddRange(new Control[] { showKeyButton, apiStoredLabel }); settingsFlow.Controls.Add(serviceCard);

            CardPanel outputCard = NewCard(0, 0, 860, 216, Color.White);
            outputCard.Controls.Add(CreateLabel("语言与输出", 24, 18, 600, 27, 11F, FontStyle.Bold, TextColor));
            AddFieldLabel(outputCard, "视频源语言", 24, 59);
            sourceLanguageBox = new SourceLanguageComboBox { Left = 24, Top = 85, Width = 250, Font = new Font(Font.FontFamily, 10F), AccessibleName = "视频源语言，未选择时默认日语" };
            outputCard.Controls.Add(sourceLanguageBox);
            Label languageHint = CreateLabel("→   简体中文     /     不选择时默认日语", 302, 87, 510, 25, 9F, FontStyle.Regular, MutedColor); Stretch(languageHint); outputCard.Controls.Add(languageHint);
            AddFieldLabel(outputCard, "字幕库目录", 24, 129);
            hubPathBox = new TextBox { AccessibleName = "字幕库目录" }; FrameInput(outputCard, hubPathBox, 24, 154, 672, true);
            Button chooseHubButton = CreateButton("选择目录", AccentSoft, AccentColor, 710, 154, 124, 42); AnchorRight(chooseHubButton); chooseHubButton.Click += ChooseHubClicked;
            outputCard.Controls.Add(chooseHubButton); settingsFlow.Controls.Add(outputCard);

            CardPanel backgroundCard = NewCard(0, 0, 860, 175, Color.White);
            backgroundCard.Controls.Add(CreateLabel("后台与隐私", 24, 18, 600, 27, 11F, FontStyle.Bold, TextColor));
            monitorCheck = new CheckBox { Left = 24, Top = 55, Width = 810, Height = 26, Text = "监控 PotPlayer 打开的视频，开始生成前先询问我", ForeColor = TextColor }; Stretch(monitorCheck);
            startupCheck = new CheckBox { Left = 24, Top = 84, Width = 810, Height = 26, Text = "使用 PotPlayer 时自动启动魔芋（独立检测器，无窗口、无托盘）", ForeColor = TextColor }; Stretch(startupCheck);
            backgroundCard.Controls.AddRange(new Control[] { monitorCheck, startupCheck });
            Label watcherHint = CreateLabel("登录后仅运行小型检测器；退出魔芋后仍有效。取消勾选并保存即可停止。", 24, 113, 810, 22, 8F, FontStyle.Regular, MutedColor); Stretch(watcherHint); backgroundCard.Controls.Add(watcherHint);
            Label privacyHint = CreateLabel("密钥仅存入 Windows 凭据管理器；配置与日志中不保存 API Key。", 24, 140, 810, 22, 8F, FontStyle.Regular, MutedColor); Stretch(privacyHint); backgroundCard.Controls.Add(privacyHint);
            settingsFlow.Controls.Add(backgroundCard);
            Panel settingsActions = new Panel { Width = 976, Dock = DockStyle.Bottom, Height = 78, BackColor = Color.White };
            settingsStatusLabel = CreateLabel("设置保存后，对新提交的任务生效。", 30, 16, 560, 49, 8.5F, FontStyle.Regular, MutedColor); Stretch(settingsStatusLabel);
            testButton = CreateButton("测试连接", AccentSoft, AccentColor, 658, 18, 120, 42); AnchorRight(testButton); testButton.Click += TestConnectionClicked;
            Button saveButton = CreateButton("保存设置", AccentColor, Color.White, 790, 18, 146, 42); AnchorRight(saveButton); saveButton.Click += SaveSettingsClicked;
            settingsActions.Controls.AddRange(new Control[] { settingsStatusLabel, testButton, saveButton });
            settingsActions.Layout += delegate { settingsStatusLabel.Width = Math.Max(100, testButton.Left - settingsStatusLabel.Left - 24); };
            settingsActions.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = MoyuPalette.Border });
            settingsTab.Controls.Add(settingsFlow); settingsTab.Controls.Add(settingsActions);

            BuildLibraryPage();
            workspace.Controls.Add(tabs); workspace.Controls.Add(header); workspace.Controls.Add(footer);
            Controls.Add(workspace); Controls.Add(sidebar);
            trayIcon = new NotifyIcon { Icon = Icon, Text = "魔芋 · 双语字幕工作台", Visible = true };
            trayIcon.DoubleClick += delegate { ShowFromTray(); };
            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("打开魔芋", null, delegate { ShowFromTray(); });
            trayMenu.Items.Add("选择视频", null, delegate { ShowFromTray(); tabs.SelectedTab = taskTab; ChooseVideoClicked(null, EventArgs.Empty); });
            trayMenu.Items.Add("字幕库", null, delegate { ShowFromTray(); tabs.SelectedTab = libraryTab; });
            trayMenu.Items.Add("模型设置", null, delegate { ShowFromTray(); tabs.SelectedTab = settingsTab; });
            trayMenu.Items.Add(new ToolStripSeparator()); trayMenu.Items.Add("退出", null, delegate { allowClose = true; Close(); });
            trayIcon.ContextMenuStrip = trayMenu;
            uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            uiTimer.Tick += delegate { UpdateElapsed(); }; uiTimer.Start();
            LoadSettingsIntoUi(); UpdateNavigation();
            Shown += delegate { UpdateNavigation(); };
            Shown += FormShown; FormClosing += OnFormClosing;
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.O) { ChooseVideoClicked(null, EventArgs.Empty); e.SuppressKeyPress = true; }
                if (e.Control && e.KeyCode == Keys.S && tabs.SelectedTab == settingsTab) { SaveSettingsClicked(null, EventArgs.Empty); e.SuppressKeyPress = true; }
            };
            ResumeLayout(true);
        }

        private static void Stretch(Control control) { control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; }
        private static void AnchorRight(Control control) { control.Anchor = AnchorStyles.Top | AnchorStyles.Right; }
        private static Panel FrameInput(Control parent, TextBox box, int x, int y, int width, bool stretch)
        {
            MoyuInputFrame frame = new MoyuInputFrame(box) { Left = x, Top = y, Width = width, Height = 42 };
            if (stretch) Stretch(frame); else AnchorRight(frame); parent.Controls.Add(frame); return frame;
        }
        private static FlowLayoutPanel NewPageFlow()
        {
            FlowLayoutPanel flow = new FlowLayoutPanel { Size = new Size(976, 716), Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(28, 24, 28, 16), BackColor = PageColor };
            flow.SizeChanged += delegate { FitPage(flow); };
            flow.ControlAdded += delegate { FitPage(flow); };
            return flow;
        }
        private static void FitPage(FlowLayoutPanel flow)
        {
            // Ignore transient undocked sizes so anchored children never collapse to zero width.
            if (flow.ClientSize.Width < 700) return;
            int width = flow.Width - flow.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2;
            foreach (Control control in flow.Controls)
            {
                control.Width = width; control.Margin = new Padding(0, 0, 0, 16);
            }
        }
        private static Panel PageHeading(string title, string detail)
        {
            Panel heading = new Panel { Width = 860, Height = 73, BackColor = PageColor };
            Label titleLabel = CreateLabel(title, 0, 1, 840, 36, 21F, FontStyle.Bold, TextColor); Stretch(titleLabel);
            Label detailLabel = CreateLabel(detail, 1, 45, 840, 25, 9F, FontStyle.Regular, MutedColor); Stretch(detailLabel);
            heading.Controls.AddRange(new Control[] { titleLabel, detailLabel }); return heading;
        }
        private void AddNavigation(Panel sidebar, TabPage page, string glyph, int y)
        {
            MoyuNavButton button = new MoyuNavButton { Left = 16, Top = y, Width = 172, Height = 46, Text = page.Text, Tag = page, GlyphKind = glyph, Font = new Font(Font.FontFamily, 10F, FontStyle.Bold), Cursor = Cursors.Hand };
            button.Click += delegate { tabs.SelectedTab = page; }; navigation.Add(button); sidebar.Controls.Add(button);
        }
        private void UpdateNavigation()
        {
            foreach (MoyuNavButton button in navigation) { button.Selected = button.Tag == tabs.SelectedTab; button.Invalidate(); }
            breadcrumbLabel.Text = "工作空间   /   " + (tabs.SelectedTab == null ? "字幕任务" : tabs.SelectedTab.Text);
        }
        private void UpdateConfigurationSummary(AppConfig config)
        {
            languageSummaryButton.Text = SourceLanguages.Get(config.SourceLanguage).Name + " → 简体中文   更改";
            modelSummaryLabel.Text = "翻译模型  " + config.Model; toolTips.SetToolTip(modelSummaryLabel, config.Model);
        }
        private void EnableVideoDrop(Control control)
        {
            control.AllowDrop = true;
            control.DragEnter += delegate(object sender, DragEventArgs e) { e.Effect = IsSupportedDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None; };
            control.DragDrop += delegate(object sender, DragEventArgs e)
            {
                if (IsSupportedDrop(e.Data)) { string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop); SelectMedia(paths[0]); tabs.SelectedTab = taskTab; }
            };
            foreach (Control child in control.Controls) EnableVideoDrop(child);
        }
        internal static bool IsSupportedDrop(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
            string[] paths = data.GetData(DataFormats.FileDrop) as string[];
            return paths != null && paths.Length == 1 && File.Exists(paths[0]) && PotPlayerMonitor.IsMediaFile(paths[0]);
        }
        private void UpdateElapsed()
        {
            if (!processingVisible || !processingStarted.HasValue) return;
            TimeSpan elapsed = DateTime.UtcNow - processingStarted.Value;
            elapsedLabel.Text = "已用时 " + ((int)elapsed.TotalMinutes).ToString("00") + ":" + elapsed.Seconds.ToString("00") + "  ·  " + Path.GetFileName(processingMediaPath);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !uiResourcesDisposed)
            {
                uiResourcesDisposed = true;
                if (uiTimer != null) uiTimer.Dispose();
                if (toolTips != null) toolTips.Dispose();
                if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
}
