using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using PotPlayerAiSubtitle;

// Compiled as a separate executable in an isolated directory; never starts the worker or monitor.
internal static class WorkspaceUiTests
{
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Field(MainForm form, string name) { return typeof(MainForm).GetField(name, Flags).GetValue(form); }
    private static object Call(MainForm form, string name, params object[] args) { return typeof(MainForm).GetMethod(name, Flags).Invoke(form, args); }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Pump(int milliseconds)
    {
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        do { Application.DoEvents(); System.Threading.Thread.Sleep(5); } while (watch.ElapsedMilliseconds < milliseconds);
    }
    private sealed class ProbeButton : MoyuButton
    {
        internal float HoverValue { get { return HoverAmount; } }
        internal float PressValue { get { return PressAmount; } }
        internal bool Animating { get { return Motion.IsRunning; } }
        internal void HoverEnter() { OnMouseEnter(EventArgs.Empty); }
        internal void HoverLeave() { OnMouseLeave(EventArgs.Empty); }
        internal void Down(MouseButtons button) { OnMouseDown(new MouseEventArgs(button, 1, 12, 12, 0)); }
        internal void Up() { OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, 12, 12, 0)); }
        internal void Key(bool down) { if (down) OnKeyDown(new KeyEventArgs(Keys.Space)); else OnKeyUp(new KeyEventArgs(Keys.Space)); }
    }
    private static void CheckEffects()
    {
        using (Form host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ClientSize = new Size(640, 200) })
        using (ProbeButton button = new ProbeButton { Text = "生成双语字幕  →", BackColor = MoyuPalette.Yellow, ForeColor = MoyuPalette.Ink, Bounds = new Rectangle(24, 40, 230, 48) })
        using (MoyuNavButton nav = new MoyuNavButton { Text = "字幕任务", GlyphKind = "task", Bounds = new Rectangle(300, 40, 190, 48) })
        using (MoyuProgressBar bar = new MoyuProgressBar { Bounds = new Rectangle(24, 122, 550, 16) })
        {
            host.Controls.AddRange(new Control[] { button, nav, bar }); host.Show(); Pump(20);
            using (MoyuMotion motion = new MoyuMotion(button, delegate { return true; }))
            {
                motion.To(0, 1, 160); Check(motion.IsRunning && motion[0] < 1, "Motion did not start asynchronously");
                Pump(45); Check(motion[0] > 0 && motion[0] < 1, "Motion has no intermediate frames");
                motion.To(0, 0, 90); motion.To(1, 1, 70); Pump(180);
                Check(motion[0] == 0 && motion[1] == 1 && !motion.IsRunning, "Retargeted channels failed to settle");
                motion.To(0, 1, 160); host.Hide(); Pump(45); Check(motion[0] == 1 && !motion.IsRunning, "Hidden parent kept animation running: visible=" + button.Visible + ", value=" + motion[0] + ", timer=" + motion.IsRunning);
                host.Show(); motion.To(0, 0, 160); button.Enabled = false;
                Check(motion[0] == 0 && !motion.IsRunning, "Disabled control kept animation running"); button.Enabled = true;
            }
            bool allow = false;
            using (MoyuMotion reduced = new MoyuMotion(button, delegate { return allow; }))
            {
                reduced.To(0, 1, 160); Check(reduced[0] == 1 && !reduced.IsRunning, "Reduced motion must be immediate");
                allow = true; reduced.To(0, 0, 160); allow = false; Pump(45);
                Check(reduced[0] == 0 && !reduced.IsRunning, "Preference change did not stop animation");
            }
            using (Control disposable = new Control())
            {
                host.Controls.Add(disposable);
                MoyuMotion lifetime = new MoyuMotion(disposable, delegate { return true; });
                lifetime.To(0, 1, 160); disposable.Dispose();
                Check(!lifetime.IsRunning, "Disposed owner kept its animation timer"); lifetime.Dispose(); lifetime.Dispose();
            }
            Console.WriteLine("PASS finite easing, rapid retargeting, reduced motion, hidden/disabled/disposed cleanup");
            button.HoverEnter(); Pump(190); Check(button.HoverValue == 1 && !button.Animating, "Hover failed to settle");
            button.Down(MouseButtons.Right); Pump(100); Check(button.PressValue == 0, "Right click depressed the primary action");
            button.Down(MouseButtons.Left); Pump(100); Check(button.PressValue == 1, "Mouse press feedback missing");
            button.Up(); button.HoverLeave(); Pump(190); Check(button.HoverValue == 0 && button.PressValue == 0 && !button.Animating, "Mouse release remained pressed");
            int clicks = 0; button.Click += delegate { clicks++; };
            button.Key(true); Pump(100); Check(button.PressValue == 1, "Keyboard press feedback missing");
            button.Key(false); Check(clicks == 1, "Keyboard activation was delayed or duplicated"); Pump(190);
            Check(button.PressValue == 0 && !button.Animating, "Keyboard release remained pressed");
            button.HoverEnter(); button.Enabled = false; Check(!button.Animating && button.HoverValue == 0, "Disabled hover remained active"); button.Enabled = true;
            nav.Selected = true; Check(nav.Selected, "Navigation state was delayed"); Pump(230);
            nav.Selected = false; nav.Selected = true; Pump(230);
            Console.WriteLine("PASS mouse/keyboard feedback, immediate activation and navigation transitions");
            bar.Value = 65; Check(bar.Value == 65 && bar.DisplayedValue <= 65, "Animation changed real progress");
            Pump(290); Check(bar.DisplayedValue == 65 && !bar.IsAnimating, "Progress did not settle");
            bar.Value = 90; bar.Value = 12; Check(bar.Value == 12 && bar.DisplayedValue == 12 && !bar.IsAnimating, "Progress rollback was animated");
            bar.Value = 100; Check(bar.DisplayedValue == 100 && !bar.IsAnimating, "Completion lagged actual state");
            bar.Value = 0; bar.Value = 75; host.Hide(); Pump(45); Check(bar.DisplayedValue == 75 && !bar.IsAnimating, "Hidden progress continued animating"); host.Show();
            Console.WriteLine("PASS truthful progress, immediate reset/completion and hidden progress cleanup");
            using (MoyuSurfacePanel surface = new MoyuSurfacePanel { BackColor = MoyuPalette.Page, Size = new Size(192, 192) })
            using (MoyuSurfacePanel child = new MoyuSurfacePanel { BackColor = MoyuPalette.Page, Bounds = new Rectangle(31, 29, 100, 100) })
            using (Bitmap first = new Bitmap(192, 192))
            using (Bitmap second = new Bitmap(192, 192))
            {
                host.Controls.Add(surface); surface.DrawToBitmap(first, surface.ClientRectangle); surface.DrawToBitmap(second, surface.ClientRectangle);
                int changed = 0;
                for (int y = 0; y < 192; y++) for (int x = 0; x < 192; x++)
                {
                    Color color = first.GetPixel(x, y);
                    Check(color == second.GetPixel(x, y), "Texture changes between repaints");
                    if (color.ToArgb() != MoyuPalette.Page.ToArgb()) changed++;
                    Check(Math.Abs(color.R - MoyuPalette.Page.R) <= 16 && Math.Abs(color.G - MoyuPalette.Page.G) <= 20 && Math.Abs(color.B - MoyuPalette.Page.B) <= 24, "Paper texture competes with content");
                }
                if (MoyuEffects.TextureEnabled) Check(changed > 1000 && changed < 6000, "Paper texture density wrong");
                surface.Controls.Add(child); surface.DrawToBitmap(second, surface.ClientRectangle);
                for (int y = 30; y < 127; y++) for (int x = 32; x < 129; x++)
                    Check(first.GetPixel(x, y) == second.GetPixel(x, y), "Child texture seam / duplicate grain");
            }
            using (Control edge = new Control { Size = new Size(40, 30) })
            using (Bitmap bitmap = new Bitmap(40, 30))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                MoyuSurface.Background(graphics, edge, Color.White);
                Check(graphics.SmoothingMode == System.Drawing.Drawing2D.SmoothingMode.AntiAlias, "Background changed foreground smoothing");
                for (int x = 0; x < 40; x++) Check(bitmap.GetPixel(x, 0).ToArgb() == Color.White.ToArgb(), "Antialiased top edge leaks buffer pixels");
                for (int y = 0; y < 30; y++) Check(bitmap.GetPixel(0, y).ToArgb() == Color.White.ToArgb(), "Antialiased left edge leaks buffer pixels");
            }
            Console.WriteLine("PASS deterministic low-contrast texture, seamless child surfaces and opaque buffer edges");
        }
    }
    private static void Capture(MainForm form, string name)
    {
        form.PerformLayout(); Pump(260);
        using (Bitmap image = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save(Path.Combine(StoragePaths.Root, name + ".png"), ImageFormat.Png);
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    private static void CheckChrome(MainForm form)
    {
        Check(form.ClientSize.Width == 1280, "Approved window width is not 1280");
        Check(form.FormBorderStyle == FormBorderStyle.None, "Legacy titlebar still present");
        Check(((Control)Field(form, "titlebar")).Height == 48, "Integrated caption height wrong");
        Check(form.WindowHitTest(new Point(350, 24)) == 2, "Caption cannot drag/double-click");
        Check(form.WindowHitTest(new Point(form.ClientSize.Width - 22, 24)) == 1, "Caption steals close-button input");
        Check(form.WindowHitTest(new Point(1, 1)) == 13 && form.WindowHitTest(new Point(form.ClientSize.Width - 1, form.ClientSize.Height - 1)) == 17, "Corner resizing lost");
        Point caption = form.PointToScreen(new Point(350, 24));
        IntPtr packed = new IntPtr(unchecked((int)(((uint)(ushort)caption.Y << 16) | (ushort)caption.X)));
        Check(SendMessage(form.Handle, 0x84, IntPtr.Zero, packed).ToInt32() == 2, "Native caption hit test failed on negative coordinates");
        Check(((CardPanel)Field(form, "selectCard")).PaperTape, "Approved tape missing");
        Size original = form.Size;
        SendMessage(form.Handle, 0xA3, new IntPtr(2), packed); Pump(80);
        Check(form.WindowState == FormWindowState.Maximized, "Native caption double-click did not maximize");
        SendMessage(form.Handle, 0xA3, new IntPtr(2), packed); Pump(80);
        Check(form.WindowState == FormWindowState.Normal && form.Size == original, "Native caption double-click restore failed");
        ((Button)Field(form, "maximizeWindowButton")).PerformClick(); Pump(80);
        Check(form.WindowState == FormWindowState.Maximized, "Maximize button failed");
        Check(form.RectangleToScreen(form.ClientRectangle) == Screen.FromHandle(form.Handle).WorkingArea, "Maximized client " + form.RectangleToScreen(form.ClientRectangle) + " vs work area " + Screen.FromHandle(form.Handle).WorkingArea);
        Check(((Control)Field(form, "maximizeWindowButton")).AccessibleName == "还原窗口", "Restore accessible label wrong");
        ((Button)Field(form, "maximizeWindowButton")).PerformClick(); Pump(80);
        Check(form.WindowState == FormWindowState.Normal && form.Size == original, "Restore changed original size " + original + " => " + form.Size + " state " + form.WindowState);
        ((Button)Field(form, "minimizeWindowButton")).PerformClick(); Pump(40);
        Check(form.WindowState == FormWindowState.Minimized, "Minimize button failed");
        SendMessage(form.Handle, 0x112, new IntPtr(0xF120), IntPtr.Zero);
        ((Button)Field(form, "closeWindowButton")).PerformClick(); Pump(40);
        Check(!form.Visible && !form.IsDisposed && ((NotifyIcon)Field(form, "trayIcon")).Visible, "Close no longer hides to tray");
        form.Show(); Pump(40);
        Console.WriteLine("PASS 1280 width, native caption hit test, resizing, maximize work area, restore, minimize and close-to-tray");
    }
    private static void CheckNavigation(MainForm form, TabControl tabs, int index)
    {
        tabs.SelectedIndex = index; Application.DoEvents();
        List<MoyuNavButton> buttons = (List<MoyuNavButton>)Field(form, "navigation");
        Check(buttons.Count(delegate(MoyuNavButton button) { return button.Selected; }) == 1, "Navigation needs exactly one active item");
        Check(buttons.Single(delegate(MoyuNavButton button) { return button.Selected; }).Tag == tabs.SelectedTab, "Wrong navigation item selected");
    }
    private static void CheckSettingsBounds(MainForm form)
    {
        Control model = ((TextBox)Field(form, "modelBox")).Parent;
        Control key = ((TextBox)Field(form, "apiKeyBox")).Parent;
        Check(model.Right + 8 <= key.Left, "Model and API key fields overlap");
        Check(key.Right <= key.Parent.ClientSize.Width - 20, "API key field exceeds card");
        Control status = (Control)Field(form, "settingsStatusLabel");
        Control test = (Control)Field(form, "testButton");
        Check(status.Right + 8 <= test.Left, "Settings feedback overlaps actions");
        Control language = (Control)Field(form, "sourceLanguageBox");
        Check(language.Width >= 200, "Language selector collapsed");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
    private static int CheckQualityDpi()
    {
        SetProcessDPIAware(); StoragePaths.Ensure();
        var config=AppConfig.CreateDefault(); config.MonitorPotPlayer=false; config.StartWithWindows=false; config.TranslationQuality="quality"; config.QualityMode="intensive"; AppConfig.Save(config);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using(var form=new MainForm(true,false,null))
        {
            form.Shown -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, typeof(MainForm).GetMethod("FormShown", Flags));
            form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-32000,-32000); form.Show();
            var tabs=(TabControl)Field(form,"tabs"); tabs.SelectedIndex=1;
            var mode=(ComboBox)Field(form,"qualityModeBox"); var limits=new[]{"intensiveMinutesBox","intensiveRequestsBox","intensiveTokensBox","searchLimitBox"};
            foreach(bool minimum in new[]{false,true})
            {
                if(minimum) form.Size=form.MinimumSize;
                ((FlowLayoutPanel)tabs.SelectedTab.Controls[0]).ScrollControlIntoView(mode.Parent); Pump(200);
                int edge=0;
                foreach(string name in limits) {var c=(Control)Field(form,name); Check(c.Left>=edge && c.Right<=c.Parent.ClientSize.Width-12,"DPI budget overlap: "+name); edge=c.Right+4;}
                Check(((CheckBox)Field(form,"thinkingCheck")).Checked,"DPI intensive preference missing");
                Capture(form,minimum?"quality-dpi-min":"quality-dpi");
            }
            using(var graphics=form.CreateGraphics()) Console.WriteLine("PASS quality options at actual desktop DPI "+graphics.DpiX+", normal/minimum bounds and scroll");
            typeof(MainForm).GetField("allowClose",Flags).SetValue(form,true); form.Close();
        }
        return 0;
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--quality-dpi") return CheckQualityDpi();
            StoragePaths.Ensure();
            AppConfig config = AppConfig.CreateDefault();
            config.MonitorPotPlayer = false; config.StartWithWindows = false;
            config.SubtitleHubPath = Path.Combine(StoragePaths.Root, "test-hub"); AppConfig.Save(config);
            string video = Path.Combine(StoragePaths.Root, "雨后的车站 · Episode 01.mkv");
            File.WriteAllBytes(video, new byte[4096]);
            string other = Path.Combine(StoragePaths.Root, "Episode 02.mp4"); File.WriteAllBytes(other, new byte[32]);
            string text = Path.Combine(StoragePaths.Root, "not-video.txt"); File.WriteAllText(text, "test");
            Check(MainForm.IsSupportedDrop(new DataObject(DataFormats.FileDrop, new[] { video })), "Valid video rejected");
            Check(!MainForm.IsSupportedDrop(null)
                && !MainForm.IsSupportedDrop(new DataObject(DataFormats.FileDrop, new[] { video, other }))
                && !MainForm.IsSupportedDrop(new DataObject(DataFormats.FileDrop, new[] { text }))
                && !MainForm.IsSupportedDrop(new DataObject(DataFormats.FileDrop, new[] { video + ".missing" })), "Invalid drop accepted");
            Console.WriteLine("PASS single-video drop validation and invalid input rejection");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            CheckEffects();
            using (MainForm form = new MainForm(true, false, null))
            {
                form.Shown -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, typeof(MainForm).GetMethod("FormShown", Flags));
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000); form.Show();
                Call(form, "SetAppStatus", "待命", MoyuPalette.Blue);
                TabControl tabs = (TabControl)Field(form, "tabs");
                CheckChrome(form);
                CheckNavigation(form, tabs, 0); Capture(form, "task-empty");
                MoyuHeroPanel hero = (MoyuHeroPanel)Field(form, "taskHero");
                Check(!hero.Compact && !((Control)Field(form, "progressCard")).Visible, "Ready screen must feature artwork, not empty progress");
                Check(MoyuArtwork.Hero.Width >= 1080 && MoyuArtwork.Logo.Width >= 256, "Embedded artwork missing or undersized");
                MoyuNavButton selectedNav = ((List<MoyuNavButton>)Field(form, "navigation")).Single(delegate(MoyuNavButton b) { return b.Selected; });
                using (Bitmap navImage = new Bitmap(selectedNav.Width, selectedNav.Height))
                {
                    selectedNav.DrawToBitmap(navImage, selectedNav.ClientRectangle);
                    Color sample = navImage.GetPixel(selectedNav.Width - 20, selectedNav.Height / 2);
                    Check(Math.Abs(sample.R - MoyuPalette.Yellow.R) < 16 && Math.Abs(sample.G - MoyuPalette.Yellow.G) < 16 && Math.Abs(sample.B - MoyuPalette.Yellow.B) < 16, "Selected navigation must retain the approved yellow fill under subtle grain");
                }
                Console.WriteLine("PASS approved artwork, ready-state hierarchy and blue/yellow navigation");
                Size normalSize = form.Size;
                form.Size = form.MinimumSize; Capture(form, "task-empty-min");
                FlowLayoutPanel taskFlow = (FlowLayoutPanel)Field(form, "taskCanvas");
                Check(!taskFlow.HorizontalScroll.Visible, "Minimum ready layout has horizontal scrolling");
                form.Size = normalSize;
                Button start = (Button)Field(form, "startButton"), cancel = (Button)Field(form, "cancelButton");
                Check(!start.Enabled && !cancel.Enabled, "Empty task buttons wrong");
                Call(form, "SelectMedia", video); Check(start.Enabled, "Selected video cannot start");
                Call(form, "SetProcessingState", video, true);
                Call(form, "UpdateProgress", new ProgressInfo("正在翻译对白", "正在处理第 8 / 12 个场景；视频可以继续播放。", 68));
                Check(!start.Enabled && cancel.Enabled, "Processing buttons wrong");
                Check(hero.Compact && ((Control)Field(form, "progressCard")).Visible, "Active task must compact the hero and reveal real progress");
                Check(((MoyuProgressBar)Field(form, "progressBar")).FillColor == MoyuPalette.Yellow, "Running progress palette wrong");
                Capture(form, "task-progress");
                Call(form, "SelectMedia", other);
                Check(((Label)Field(form, "elapsedLabel")).Text.Contains(Path.GetFileName(video)), "Active task confused with newly selected media");
                Call(form, "UpdateProgress", new ProgressInfo("双语字幕已完成", "三个版本已归档。", 100));
                Call(form, "SetProcessingState", video, false);
                Check(start.Enabled && !cancel.Enabled && !((MoyuStageStrip)Field(form, "stageStrip")).Running, "Completion state wrong");
                Capture(form, "task-complete");
                Check(((MoyuProgressBar)Field(form, "progressBar")).Value == 100, "Real completion progress lost");
                Call(form, "SetProcessingState", video, true);
                Call(form, "UpdateProgress", new ProgressInfo("生成失败", "测试错误反馈，不访问翻译服务。", 12));
                Call(form, "SetProcessingState", video, false);
                Check(((Label)Field(form, "progressStageLabel")).Text == "生成失败" && ((MoyuProgressBar)Field(form, "progressBar")).Value == 12, "Theme masks failed task as completion");
                Capture(form, "task-error");
                Console.WriteLine("PASS task selection, running/completion/error states and active-media identity");
                CheckNavigation(form, tabs, 1); CheckSettingsBounds(form); Capture(form, "settings");
                SourceLanguageComboBox language = (SourceLanguageComboBox)Field(form, "sourceLanguageBox");
                Check(language.SourceLanguage == "ja", "Default source language changed");
                language.SourceLanguage = "en";
                TranslationQualityComboBox quality = (TranslationQualityComboBox)Field(form, "translationQualityBox");
                CheckBox thinking = (CheckBox)Field(form, "thinkingCheck");
                Check(quality.TranslationQuality == "fast" && !thinking.Enabled && !thinking.Checked, "Fast/default thinking state wrong");
                quality.TranslationQuality = "quality";
                Check(thinking.Enabled, "Quality thinking switch is not selectable");
                thinking.Checked = true;
                AppConfig qualitySettings = (AppConfig)Call(form, "ReadSettingsFromUi");
                AppConfig.Save(qualitySettings); Call(form, "LoadSettingsIntoUi");
                Check(thinking.Checked && thinking.Enabled, "Quality thinking preference did not persist");
                ((FlowLayoutPanel)thinking.Parent.Parent).ScrollControlIntoView(thinking.Parent);
                Check(thinking.Bottom < ((Control)Field(form, "hubPathBox")).Parent.Top, "Thinking switch overlaps subtitle directory");
                Capture(form, "settings-quality-thinking");
                quality.TranslationQuality = "fast";
                Check(!thinking.Enabled && thinking.Checked, "Fast tier should disable, not forget, quality preference");
                AppConfig.Save((AppConfig)Call(form, "ReadSettingsFromUi")); Call(form, "LoadSettingsIntoUi");
                Check(!thinking.Enabled && thinking.Checked, "Legacy fast/true settings were not safely preserved");
                quality.TranslationQuality = "quality";
                Check(thinking.Enabled && thinking.Checked, "Returning to quality lost the thinking selection");
                thinking.Checked = false;
                AppConfig.Save((AppConfig)Call(form, "ReadSettingsFromUi")); Call(form, "LoadSettingsIntoUi");
                Check(thinking.Enabled && !thinking.Checked, "Quality thinking cannot be disabled persistently");
                quality.TranslationQuality = "fast";
                Console.WriteLine("PASS quality-only thinking switch, on/off persistence and preference preservation across tiers");
                quality.TranslationQuality = "quality";
                var mode = (ComboBox)Field(form, "qualityModeBox");
                var model = (TextBox)Field(form, "intensiveModelBox");
                var web = (CheckBox)Field(form, "webReferenceCheck");
                mode.SelectedIndex = 1; Check(thinking.Checked && model.Enabled, "Intensive thinking default missing");
                thinking.Checked = false; mode.SelectedIndex = 0; Check(!thinking.Checked && !model.Enabled, "Standard preference lost");
                thinking.Checked = true; mode.SelectedIndex = 1; Check(!thinking.Checked, "Intensive preference lost");
                model.Text = "review-test-model"; ((NumericUpDown)Field(form, "intensiveRequestsBox")).Value = 17;
                AppConfig.Save((AppConfig)Call(form, "ReadSettingsFromUi")); Call(form, "LoadSettingsIntoUi");
                Check(mode.SelectedIndex == 1 && model.Text == "review-test-model" && !thinking.Checked, "Intensive options failed roundtrip");
                Check(((NumericUpDown)Field(form, "intensiveRequestsBox")).Value == 17, "Limit lost");
                ((FlowLayoutPanel)tabs.SelectedTab.Controls[0]).ScrollControlIntoView(mode.Parent); Capture(form, "settings-intensive");
                quality.TranslationQuality = "fast"; Check(!mode.Enabled && !model.Enabled && !web.Enabled, "Fast tier permits quality settings");
                quality.TranslationQuality = "quality"; mode.SelectedIndex = 0; Check(thinking.Checked, "Separate thinking preferences not preserved");
                quality.TranslationQuality = "fast";
                Console.WriteLine("PASS quality schemes, separate thinking, limits, model persistence and fast-tier isolation");
                AppConfig saved = (AppConfig)Call(form, "ReadSettingsFromUi");
                // Use a fresh missing hub below; ReadSettingsFromUi legitimately creates the configured output directory.
                saved.SubtitleHubPath = Path.Combine(StoragePaths.Root, "missing-hub"); AppConfig.Save(saved);
                Call(form, "LoadSettingsIntoUi");
                Check(language.SourceLanguage == "en" && ((Button)Field(form, "languageSummaryButton")).Text.Contains("英语"), "Saved language summary wrong");
                language.SelectedIndex = -1; Check(language.SourceLanguage == "ja", "Unselected language does not default to Japanese");
                Console.WriteLine("PASS source-language default, persistence and task summary");
                CheckNavigation(form, tabs, 2); Capture(form, "library-empty");
                ListView list = (ListView)Field(form, "libraryList");
                Check(list.Items.Count == 0 && !Directory.Exists(saved.SubtitleHubPath), "Empty library browsing wrote to disk");
                for (int i = 1; i <= 3; i++)
                {
                    string directory = Path.Combine(saved.SubtitleHubPath, "雨后的车站 · Episode 0" + i + " 字幕"); Directory.CreateDirectory(directory);
                    foreach (string version in new[] { "双语", "日语", "中文" }) File.WriteAllText(Path.Combine(directory, version + ".srt"), "1\n00:00:00,000 --> 00:00:01,000\n示例\n");
                }
                Call(form, "RefreshLibrary"); Check(list.Items.Count == 3 && list.Items[0].SubItems[1].Text == "3 份", "Archive contents not reflected");
                list.Items[0].Selected = true; Application.DoEvents(); string selected = list.Items[0].Tag as string;
                Call(form, "RefreshLibrary");
                Check(list.SelectedItems.Count == 1 && (string)list.SelectedItems[0].Tag == selected
                    && ((Button)Field(form, "openArchiveButton")).Enabled, "Refresh lost selection");
                Capture(form, "library");
                Console.WriteLine("PASS read-only empty library, archive counts and selection preservation");
                form.Size = form.MinimumSize; Capture(form, "library-min");
                Check(list.Columns.Cast<ColumnHeader>().Sum(delegate(ColumnHeader column) { return column.Width; }) <= list.ClientSize.Width, "Library requires horizontal scrolling");
                CheckNavigation(form, tabs, 1); CheckSettingsBounds(form); Capture(form, "settings-min");
                FlowLayoutPanel settingsFlow = (FlowLayoutPanel)tabs.SelectedTab.Controls[0];
                settingsFlow.ScrollControlIntoView(((ComboBox)Field(form, "qualityModeBox")).Parent); Capture(form, "settings-intensive-min");
                var lastLimit = (NumericUpDown)Field(form, "searchLimitBox");
                Check(lastLimit.Right <= lastLimit.Parent.ClientSize.Width - 20, "Quality budgets overflow minimum window");
                settingsFlow.ScrollControlIntoView(((CheckBox)Field(form, "startupCheck")).Parent); Capture(form, "settings-min-bottom");
                CheckNavigation(form, tabs, 0);
                ((Label)Field(form, "detectedNameLabel")).Text = Path.GetFileName(other);
                Call(form, "SetDetectedCardVisible", true); Capture(form, "task-detected-min");
                Control detected = (Control)Field(form, "detectedCard"), select = (Control)Field(form, "selectCard"), progress = (Control)Field(form, "progressCard");
                Check(detected.Bottom < select.Top && select.Bottom < progress.Top, "Detected video overlaps task cards");
                Call(form, "SetDetectedCardVisible", false);
                Check(select.Bottom < progress.Top, "Task cards overlap after dismissal");
                Console.WriteLine("PASS three-page navigation, minimum-size layout, scrolling and detection-card flow");
                for (int i = 0; i < 101; i++) Directory.CreateDirectory(Path.Combine(saved.SubtitleHubPath, "archive-" + i));
                Call(form, "RefreshLibrary"); Check(list.Items.Count == 100, "Library display limit ignored");
                Console.WriteLine("PASS library display is limited to 100 recent archives");
                Check(Field(form, "workerThread") == null && Field(form, "monitor") == null, "UI tests started background processing");
                typeof(MainForm).GetField("allowClose", Flags).SetValue(form, true);
                form.Close();
                Check(form.IsDisposed, "Explicit exit did not dispose the window");
                Console.WriteLine("PASS explicit exit and repeated disposal");
            }
            Console.WriteLine("PASS no worker, monitor, network or startup registration used");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
