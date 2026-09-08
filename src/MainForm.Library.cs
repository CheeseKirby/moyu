using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PotPlayerAiSubtitle
{
    internal sealed partial class MainForm
    {
        private ListView libraryList;
        private Label libraryStatusLabel;
        private Label libraryEmptyLabel;
        private Label libraryPathLabel;
        private Button openArchiveButton;

        private void BuildLibraryPage()
        {
            Panel canvas = new MoyuSurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(28, 24, 28 + SystemInformation.VerticalScrollBarWidth + 2, 24), BackColor = PageColor };
            Panel heading = PageHeading("好故事，值得再看一次。", "字幕库  /  双语、源语言与中文版本，集中归档。");
            heading.Dock = DockStyle.Top; heading.Height = 90;
            Panel actions = new MoyuSurfacePanel { Dock = DockStyle.Top, Height = 64, Width = 860 };
            Button refresh = CreateButton("刷新列表", AccentSoft, AccentColor, 0, 0, 116, 38);
            refresh.Click += delegate { RefreshLibrary(); };
            Button openRoot = CreateButton("打开字幕库目录", Color.White, AccentColor, 128, 0, 164, 38);
            openRoot.Click += delegate { OpenLibraryFolder(AppConfig.Load().SubtitleHubPath); };
            actions.Controls.AddRange(new Control[] { refresh, openRoot });
            Panel bottom = new MoyuSurfacePanel { Dock = DockStyle.Bottom, Height = 105, Width = 860 };
            libraryStatusLabel = CreateLabel("", 0, 15, 620, 27, 9F, FontStyle.Regular, MutedColor); Stretch(libraryStatusLabel);
            libraryPathLabel = CreateLabel("", 0, 52, 620, 44, 8F, FontStyle.Regular, MutedColor); Stretch(libraryPathLabel);
            openArchiveButton = CreateButton("打开所选归档", AccentColor, Color.White, 690, 15, 170, 42); AnchorRight(openArchiveButton);
            openArchiveButton.Enabled = false; openArchiveButton.Click += delegate { OpenSelectedArchive(); };
            bottom.Controls.AddRange(new Control[] { libraryStatusLabel, libraryPathLabel, openArchiveButton });
            bottom.Layout += delegate { libraryStatusLabel.Width = Math.Max(100, openArchiveButton.Left - 24); libraryPathLabel.Width = bottom.ClientSize.Width; };
            CardPanel listCard = NewCard(0, 0, 860, 400, Color.White); listCard.Dock = DockStyle.Fill; listCard.Padding = new Padding(16);
            libraryList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
                HideSelection = false, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = TextColor,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, AccessibleName = "已归档的字幕", Font = new Font(Font.FontFamily, 10F) };
            ImageList rowSpacing = new ImageList { ImageSize = new Size(1, 42) };
            libraryList.SmallImageList = rowSpacing;
            libraryList.Disposed += delegate { rowSpacing.Dispose(); };
            libraryList.OwnerDraw = true;
            libraryList.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e)
            {
                using (Brush brush = new SolidBrush(PageColor)) e.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, libraryList.Font, Rectangle.Inflate(e.Bounds, -10, 0), MutedColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            libraryList.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e)
            {
                Color background = e.Item.Selected ? AccentSoft : Color.White;
                using (Brush brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, libraryList.Font, Rectangle.Inflate(e.Bounds, -10, 0),
                    e.Item.Selected ? AccentColor : e.ColumnIndex == 0 ? TextColor : MutedColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                using (Pen line = new Pen(MoyuPalette.Border)) e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                if (e.Item.Focused && libraryList.Focused && e.ColumnIndex == 0) e.DrawFocusRectangle(e.Bounds);
            };
            libraryList.Columns.Add("视频归档", 440); libraryList.Columns.Add("字幕文件", 100); libraryList.Columns.Add("更新时间", 180);
            libraryList.SizeChanged += delegate { FitLibraryColumns(); };
            libraryList.HandleCreated += delegate { FitLibraryColumns(); };
            libraryList.SelectedIndexChanged += delegate { openArchiveButton.Enabled = libraryList.SelectedItems.Count > 0; };
            libraryList.DoubleClick += delegate { OpenSelectedArchive(); };
            libraryList.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { OpenSelectedArchive(); e.Handled = true; } };
            libraryEmptyLabel = CreateLabel("", 0, 0, 600, 220, 11F, FontStyle.Regular, MutedColor);
            libraryEmptyLabel.Dock = DockStyle.Fill; libraryEmptyLabel.TextAlign = ContentAlignment.MiddleCenter;
            listCard.Controls.Add(libraryList); listCard.Controls.Add(libraryEmptyLabel);
            canvas.Controls.Add(listCard); canvas.Controls.Add(bottom); canvas.Controls.Add(actions); canvas.Controls.Add(heading);
            libraryTab.Controls.Add(canvas);
        }

        private void RefreshLibrary()
        {
            if (libraryList == null) return;
            string selected = libraryList.SelectedItems.Count == 0 ? null : libraryList.SelectedItems[0].Tag as string;
            libraryList.BeginUpdate(); libraryList.Items.Clear(); openArchiveButton.Enabled = false;
            try
            {
                string hub = AppConfig.Load().SubtitleHubPath;
                libraryPathLabel.Text = hub; toolTips.SetToolTip(libraryPathLabel, hub);
                if (!Directory.Exists(hub))
                {
                    libraryStatusLabel.Text = "尚无归档";
                    libraryEmptyLabel.Text = "字幕库还在等待第一部视频\n\n完成字幕任务后，归档会出现在这里。";
                    return;
                }
                // Only inspect direct folders and file names, never subtitle contents or nested media directories.
                DirectoryInfo[] folders = new DirectoryInfo(hub).EnumerateDirectories()
                    .Where(delegate(DirectoryInfo folder) { return (folder.Attributes & FileAttributes.ReparsePoint) == 0; })
                    .OrderByDescending(delegate(DirectoryInfo folder) { return folder.LastWriteTimeUtc; }).Take(101).ToArray();
                int inaccessible = 0;
                foreach (DirectoryInfo folder in folders.Take(100))
                {
                    string count;
                    try { count = folder.EnumerateFiles("*.srt", SearchOption.TopDirectoryOnly).Count().ToString() + " 份"; }
                    catch (IOException) { count = "无法读取"; inaccessible++; }
                    catch (UnauthorizedAccessException) { count = "无法读取"; inaccessible++; }
                    ListViewItem item = new ListViewItem(folder.Name) { Tag = folder.FullName };
                    item.SubItems.Add(count); item.SubItems.Add(folder.LastWriteTime.ToString("yyyy-MM-dd  HH:mm"));
                    libraryList.Items.Add(item);
                    if (string.Equals(selected, folder.FullName, StringComparison.OrdinalIgnoreCase)) item.Selected = true;
                }
                libraryStatusLabel.Text = folders.Length > 100 ? "显示最近 100 个归档 · 更多内容可打开目录查看" : libraryList.Items.Count + " 个视频归档";
                if (inaccessible > 0) libraryStatusLabel.Text += " · 部分目录无法读取";
                libraryEmptyLabel.Text = "还没有字幕归档\n\n先去「字幕任务」添加一部视频吧。";
            }
            catch (Exception ex)
            {
                libraryStatusLabel.Text = "暂时无法读取字幕库";
                libraryEmptyLabel.Text = "请检查字幕库目录和访问权限\n\n" + ex.Message;
            }
            finally
            {
                libraryList.EndUpdate(); libraryEmptyLabel.Visible = libraryList.Items.Count == 0;
                libraryList.Visible = libraryList.Items.Count > 0;
                FitLibraryColumns();
            }
        }

        private void FitLibraryColumns()
        {
            if (libraryList.Columns.Count != 3) return;
            libraryList.Columns[1].Width = 100;
            libraryList.Columns[2].Width = 180;
            libraryList.Columns[0].Width = Math.Max(180, libraryList.ClientSize.Width - 284);
        }

        private void OpenSelectedArchive()
        {
            if (libraryList.SelectedItems.Count > 0) OpenLibraryFolder(libraryList.SelectedItems[0].Tag as string);
        }
        private void OpenLibraryFolder(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) throw new DirectoryNotFoundException("目录尚不存在；字幕完成后会自动建立归档。");
                Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true });
            }
            catch (Exception ex) { libraryStatusLabel.Text = ex.Message; }
        }
    }
}
