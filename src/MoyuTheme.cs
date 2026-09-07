using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PotPlayerAiSubtitle
{
    internal static class MoyuPalette
    {
        internal static readonly Color Page = Color.FromArgb(235, 247, 253);
        internal static readonly Color Ink = Color.FromArgb(28, 58, 78);
        internal static readonly Color Muted = Color.FromArgb(103, 126, 143);
        internal static readonly Color Blue = Color.FromArgb(25, 151, 224);
        internal static readonly Color BlueDark = Color.FromArgb(9, 113, 184);
        internal static readonly Color BlueSoft = Color.FromArgb(220, 243, 255);
        internal static readonly Color Red = Color.FromArgb(232, 72, 75);
        internal static readonly Color Yellow = Color.FromArgb(250, 201, 62);
    }

    internal static class MoyuDrawing
    {
        internal static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (rectangle.Width <= 0 || rectangle.Height <= 0) return path;
            int diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height)));
            Rectangle arc = new Rectangle(rectangle.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = rectangle.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rectangle.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rectangle.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal static Color Blend(Color first, Color second, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            return Color.FromArgb(
                (int)(first.A + (second.A - first.A) * amount),
                (int)(first.R + (second.R - first.R) * amount),
                (int)(first.G + (second.G - first.G) * amount),
                (int)(first.B + (second.B - first.B) * amount));
        }

        internal static void RoundControl(Control control, int radius)
        {
            if (control == null || control.Width <= 0 || control.Height <= 0) return;
            using (GraphicsPath path = RoundedRectangle(new Rectangle(0, 0, control.Width, control.Height), radius))
            {
                Region old = control.Region;
                control.Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }
    }

    internal sealed class MoyuHeaderPanel : Panel
    {
        public MoyuHeaderPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = MoyuPalette.Blue;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Font titleFont = new Font("Microsoft YaHei UI", 24f, FontStyle.Bold))
            using (Font subtitleFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold))
            using (Font detailFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular))
            {
                TextRenderer.DrawText(e.Graphics, "魔芋", titleFont, new Rectangle(104, 15, 360, 42), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(e.Graphics, "PotPlayer AI 双语字幕助手", subtitleFont, new Rectangle(106, 55, 390, 25), Color.FromArgb(225, 247, 255), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(e.Graphics, "完整识别  ·  整段翻译  ·  双语归档", detailFont, new Rectangle(106, 82, 420, 22), Color.FromArgb(206, 239, 252), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle bounds = ClientRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (LinearGradientBrush background = new LinearGradientBrush(bounds, Color.FromArgb(30, 166, 232), Color.FromArgb(8, 111, 185), 10f))
            {
                e.Graphics.FillRectangle(background, bounds);
            }

            using (SolidBrush bubble = new SolidBrush(Color.FromArgb(34, Color.White)))
            {
                e.Graphics.FillEllipse(bubble, Width - 112, 13, 64, 64);
                e.Graphics.FillEllipse(bubble, Width - 168, 70, 28, 28);
                e.Graphics.FillEllipse(bubble, 585, 17, 18, 18);
            }

            DrawBellMark(e.Graphics, new Rectangle(27, 22, 62, 62));

            using (GraphicsPath wave = new GraphicsPath())
            {
                wave.StartFigure();
                wave.AddLine(0, Height - 14, 0, Height);
                wave.AddLine(Width, Height, Width, Height - 24);
                wave.AddBezier(Width, Height - 24, Width * 3 / 4, Height + 2, Width / 3, Height - 31, 0, Height - 14);
                wave.CloseFigure();
                using (SolidBrush page = new SolidBrush(MoyuPalette.Page)) e.Graphics.FillPath(page, wave);
            }
        }

        private static void DrawBellMark(Graphics graphics, Rectangle bounds)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush white = new SolidBrush(Color.White)) graphics.FillEllipse(white, bounds);
            Rectangle collar = new Rectangle(bounds.X + 10, bounds.Y + 17, bounds.Width - 20, 9);
            using (GraphicsPath collarPath = MoyuDrawing.RoundedRectangle(collar, 4))
            using (SolidBrush red = new SolidBrush(MoyuPalette.Red)) graphics.FillPath(red, collarPath);

            using (GraphicsPath bell = new GraphicsPath())
            {
                int center = bounds.X + bounds.Width / 2;
                int left = bounds.X + 16;
                int right = bounds.X + bounds.Width - 16;
                int top = bounds.Y + 24;
                int bottom = bounds.Y + 46;
                bell.AddBezier(center, top, left + 4, top + 2, left + 2, bottom - 4, left, bottom);
                bell.AddLine(left, bottom, right, bottom);
                bell.AddBezier(right, bottom, right - 2, bottom - 4, right - 4, top + 2, center, top);
                bell.CloseFigure();
                using (SolidBrush yellow = new SolidBrush(MoyuPalette.Yellow)) graphics.FillPath(yellow, bell);
                using (Pen outline = new Pen(Color.FromArgb(204, 150, 22), 1.5f)) graphics.DrawPath(outline, bell);
            }
            using (SolidBrush gold = new SolidBrush(Color.FromArgb(218, 153, 18))) graphics.FillEllipse(gold, bounds.X + 27, bounds.Y + 43, 8, 8);
            using (Pen shine = new Pen(Color.FromArgb(245, 174, 27), 2f)) graphics.DrawLine(shine, bounds.X + 22, bounds.Y + 47, bounds.X + 40, bounds.Y + 47);
        }
    }

    internal sealed class MoyuTabControl : TabControl
    {
        public MoyuTabControl()
        {
            DrawMode = TabDrawMode.OwnerDrawFixed;
            SizeMode = TabSizeMode.Fixed;
            ItemSize = new Size(138, 40);
            Padding = new Point(18, 6);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle item = GetTabRect(e.Index);
            item.Inflate(-6, -4);
            bool selected = e.Index == SelectedIndex;
            Color fill = selected ? MoyuPalette.Blue : MoyuPalette.Page;
            Color text = selected ? Color.White : MoyuPalette.Muted;
            using (GraphicsPath path = MoyuDrawing.RoundedRectangle(item, 15))
            using (SolidBrush brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
            using (Font tabFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, tabFont, item, text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            pevent.Graphics.Clear(MoyuPalette.Page);
        }
    }

    internal sealed class MoyuButton : Button
    {
        private bool hovering;
        public int CornerRadius { get; set; }
        public Color HoverBackColor { get; set; }

        public MoyuButton()
        {
            CornerRadius = 14;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? MoyuPalette.Page : Parent.BackColor);
            Color fill = BackColor;
            Color text = ForeColor;
            if (!Enabled)
            {
                fill = MoyuDrawing.Blend(BackColor, Color.White, 0.58f);
                text = MoyuDrawing.Blend(ForeColor, Color.White, 0.38f);
            }
            else if (hovering)
            {
                fill = HoverBackColor.IsEmpty ? MoyuDrawing.Blend(BackColor, Color.White, 0.12f) : HoverBackColor;
            }

            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = MoyuDrawing.RoundedRectangle(rect, CornerRadius))
            using (SolidBrush brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
            if (FlatAppearance.BorderSize > 0)
            {
                using (GraphicsPath path = MoyuDrawing.RoundedRectangle(rect, CornerRadius))
                using (Pen pen = new Pen(FlatAppearance.BorderColor, FlatAppearance.BorderSize)) e.Graphics.DrawPath(pen, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, rect, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues)
            {
                Rectangle focus = rect;
                focus.Inflate(-5, -5);
                ControlPaint.DrawFocusRectangle(e.Graphics, focus, text, fill);
            }
        }
    }

    internal sealed class MoyuProgressBar : ProgressBar
    {
        public Color TrackColor { get; set; }
        public Color FillColor { get; set; }

        public MoyuProgressBar()
        {
            TrackColor = Color.FromArgb(216, 237, 247);
            FillColor = MoyuPalette.Blue;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = MoyuDrawing.RoundedRectangle(track, Height / 2))
            using (SolidBrush brush = new SolidBrush(TrackColor)) e.Graphics.FillPath(brush, path);
            double range = Maximum - Minimum;
            double ratio = range <= 0 ? 0 : (Value - Minimum) / range;
            int fillWidth = (int)Math.Round(track.Width * Math.Max(0, Math.Min(1, ratio)));
            if (fillWidth > 0)
            {
                Rectangle fill = new Rectangle(track.X, track.Y, Math.Max(Height, fillWidth), track.Height);
                if (fill.Right > track.Right) fill.Width = track.Right - fill.Left;
                using (GraphicsPath path = MoyuDrawing.RoundedRectangle(fill, Height / 2))
                using (LinearGradientBrush brush = new LinearGradientBrush(fill, Color.FromArgb(58, 190, 238), FillColor, 0f)) e.Graphics.FillPath(brush, path);
            }
        }
    }
}
