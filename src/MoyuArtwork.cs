using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace PotPlayerAiSubtitle
{
    // Shared immutable embedded images live for the application lifetime; no deployment-side file dependency.
    internal static class MoyuArtwork
    {
        internal static readonly Bitmap Hero = Load("Hero");
        internal static readonly Bitmap Logo = Load("Logo");
        private static Bitmap Load(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Moyu.Brand." + name + ".png"))
            {
                if (stream == null) throw new InvalidOperationException("Missing embedded Moyu brand asset: " + name);
                using (Image image = Image.FromStream(stream)) return new Bitmap(image);
            }
        }
    }

    internal sealed class MoyuHeroPanel : MoyuSurfacePanel
    {
        private bool compact;
        private bool running;
        public bool Compact { get { return compact; } }
        public MoyuHeroPanel()
        {
            DoubleBuffered = true; BackColor = MoyuPalette.Page;
            AccessibleName = "听懂世界，只差一块魔芋。双语字幕、播放器联动、自动归档。";
            SetStyle(ControlStyles.ResizeRedraw, true);
        }
        public void SetTaskState(bool hasTask, bool processing)
        {
            compact = hasTask; running = processing;
            using (Graphics g = CreateGraphics()) Height = (int)((compact ? 102 : 304) * g.DpiY / 96f);
            AccessibleName = compact ? "字幕工作台，任务进度显示在下方。" : "听懂世界，只差一块魔芋。双语字幕、播放器联动、自动归档。";
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            float scale = g.DpiX / 96f, width = Width / scale;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            // Shapes and artwork are drawn in a scaled context; text is drawn afterwards at device
            // pixels with point fonts so ClearType subpixel rendering stays active at high DPI.
            float artWidth = Math.Min(390, width * .44f);
            float artHeight = artWidth * 400 / 540;
            using (Brush yellow = new SolidBrush(MoyuPalette.Yellow))
            {
                GraphicsState shapeState = g.Save();
                g.ScaleTransform(scale, scale);
                if (!compact)
                    g.FillPolygon(yellow, new[] { new PointF(59, 149), new PointF(width < 790 ? 220 : 231, 144), new PointF(width < 790 ? 220 : 231, 155), new PointF(59, 160) });
                g.Restore(shapeState);
            }
            if (compact)
                g.DrawImage(MoyuArtwork.Logo, new Rectangle((int)(0 * scale), (int)(6 * scale), (int)(78 * scale), (int)(78 * scale)));
            else
                g.DrawImage(MoyuArtwork.Hero, new RectangleF((width - artWidth) * scale, Math.Max(0, (280 - artHeight) / 2f) * scale, artWidth * scale, artHeight * scale));
            using (Pen line = new Pen(MoyuPalette.Border))
            {
                GraphicsState lineState = g.Save(); g.ScaleTransform(scale, scale);
                g.DrawLine(line, 0, Height / scale - 1, width, Height / scale - 1); g.Restore(lineState);
            }

            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (Brush ink = new SolidBrush(MoyuPalette.Ink))
            using (Brush muted = new SolidBrush(MoyuPalette.Muted))
            using (Brush blue = new SolidBrush(MoyuPalette.Blue))
            {
                if (compact)
                {
                    using (Font eyebrow = PointFont(12, FontStyle.Bold), title = PointFont(25, FontStyle.Bold), body = PointFont(15, FontStyle.Regular))
                    {
                        g.DrawString("魔芋 · 字幕工作台", eyebrow, blue, 98 * scale, 6 * scale);
                        g.DrawString(running ? "你看故事，我来翻译。" : "每一句对白，都认真对待。", title, ink, 95 * scale, 28 * scale);
                        g.DrawString("识别、翻译与归档状态，以实际任务反馈为准。", body, muted, 98 * scale, 72 * scale);
                    }
                }
                else
                {
                    using (Font eyebrow = PointFont(12, FontStyle.Bold), title = PointFont(width < 790 ? 32 : 34, FontStyle.Bold), body = PointFont(15, FontStyle.Regular))
                    {
                        g.DrawString("—  一小块魔芋，一整个世界", eyebrow, blue, 0, 18 * scale);
                        g.DrawString("听懂世界，", title, ink, -3 * scale, 61 * scale);
                        g.DrawString("只差一块魔芋。", title, ink, -3 * scale, 112 * scale);
                        g.DrawString("从第一句对白，到最后一行字幕。\n把外语交给魔芋，把注意力留给好故事。", body, muted, 0, 187 * scale);
                        g.DrawString("双语字幕   /   播放器联动   /   自动归档", eyebrow, blue, 0, 249 * scale);
                    }
                }
            }
        }
        private static Font PointFont(float logicalPx, FontStyle style)
        {
            // 96-dpi logical pixels -> points (1 px = 0.75 pt); GDI+ renders points at device DPI.
            return new Font(MoyuTypography.FamilyName, logicalPx * 0.75f, style);
        }
    }
    // Decorative only: transparent yellow line art, no timer or input interception.
    internal sealed class MoyuSideVignette : MoyuSurfacePanel
    {
        internal MoyuSideVignette() { BackColor = MoyuPalette.Sidebar; TabStop = false; Enabled = false; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics; GraphicsState state = g.Save();
            g.ScaleTransform(Width / 160f, Height / 74f); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Color.FromArgb(255, 220, 104), 1.8f))
            using (Brush yellow = new SolidBrush(Color.FromArgb(255, 220, 104)))
            {
                pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                using (GraphicsPath player = MoyuDrawing.RoundedRectangle(new Rectangle(5, 20, 46, 33), 5)) g.DrawPath(pen, player);
                g.FillPolygon(yellow, new[] { new Point(23, 28), new Point(36, 36), new Point(23, 44) });
                pen.DashPattern = new[] { 1.7f, 2.2f };
                g.DrawLine(pen, 17, 59, 40, 59); g.DrawBezier(pen, 60, 37, 71, 22, 84, 22, 96, 37);
                pen.DashStyle = DashStyle.Solid;
                g.DrawLines(pen, new[] { new Point(89, 36), new Point(97, 38), new Point(95, 30) });
                g.DrawPolygon(pen, new[] { new Point(111, 19), new Point(140, 27), new Point(131, 62), new Point(102, 54) });
                g.DrawLines(pen, new[] { new Point(140, 27), new Point(148, 32), new Point(139, 67), new Point(131, 62) });
                g.DrawLine(pen, 128, 8, 130, 3); g.DrawLine(pen, 141, 15, 146, 13);
            }
            g.Restore(state);
        }
    }

    internal sealed class MoyuUploadMark : MoyuSurfacePanel
    {
        internal MoyuUploadMark() { BackColor = Color.White; TabStop = false; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath shape = MoyuDrawing.RoundedRectangle(new Rectangle(1, 1, Width - 3, Height - 3), 10))
            using (Brush fill = new SolidBrush(MoyuPalette.BlueSoft))
            using (Pen line = new Pen(Color.FromArgb(201, 227, 244)))
            { g.FillPath(fill, shape); g.DrawPath(line, shape); }
            GraphicsState state = g.Save(); g.TranslateTransform(Width / 2f, Height / 2f); float scale = g.DpiX / 96f; g.ScaleTransform(scale, scale);
            using (Pen pen = new Pen(MoyuPalette.Blue, 1.4f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new[] { new Point(-8, 2), new Point(-8, 9), new Point(8, 9), new Point(8, 2) });
                g.DrawLine(pen, 0, -8, 0, 4); g.DrawLines(pen, new[] { new Point(-4, -4), new Point(0, -8), new Point(4, -4) });
            }
            g.Restore(state);
        }
    }

}
