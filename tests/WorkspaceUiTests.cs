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
    private static void Capture(MainForm form, string name)
    {
        form.PerformLayout(); Application.DoEvents();
        using (Bitmap image = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save(Path.Combine(StoragePaths.Root, name + ".png"), ImageFormat.Png);
        }
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
    [STAThread]
    private static int Main()
    {
        try
        {
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
            using (MainForm form = new MainForm(true, false, null))
            {
                form.Shown -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, typeof(MainForm).GetMethod("FormShown", Flags));
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000); form.Show();
                Call(form, "SetAppStatus", "待命", MoyuPalette.Blue);
                TabControl tabs = (TabControl)Field(form, "tabs");
                CheckNavigation(form, tabs, 0); Capture(form, "task-empty");
                Button start = (Button)Field(form, "startButton"), cancel = (Button)Field(form, "cancelButton");
                Check(!start.Enabled && !cancel.Enabled, "Empty task buttons wrong");
                Call(form, "SelectMedia", video); Check(start.Enabled, "Selected video cannot start");
                Call(form, "SetProcessingState", video, true);
                Call(form, "UpdateProgress", new ProgressInfo("正在翻译对白", "正在处理第 8 / 12 个场景；视频可以继续播放。", 68));
                Check(!start.Enabled && cancel.Enabled, "Processing buttons wrong");
                Capture(form, "task-progress");
                Call(form, "SelectMedia", other);
                Check(((Label)Field(form, "elapsedLabel")).Text.Contains(Path.GetFileName(video)), "Active task confused with newly selected media");
                Call(form, "UpdateProgress", new ProgressInfo("双语字幕已完成", "三个版本已归档。", 100));
                Call(form, "SetProcessingState", video, false);
                Check(start.Enabled && !cancel.Enabled && !((MoyuStageStrip)Field(form, "stageStrip")).Running, "Completion state wrong");
                Console.WriteLine("PASS task selection, running/completion states and active-media identity");
                CheckNavigation(form, tabs, 1); CheckSettingsBounds(form); Capture(form, "settings");
                SourceLanguageComboBox language = (SourceLanguageComboBox)Field(form, "sourceLanguageBox");
                Check(language.SourceLanguage == "ja", "Default source language changed");
                language.SourceLanguage = "en";
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
