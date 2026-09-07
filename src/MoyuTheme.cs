using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PotPlayerAiSubtitle
{
    internal static class MoyuPalette
    {
        internal static readonly Color Page = Color.FromArgb(246, 248, 251);
        internal static readonly Color Ink = Color.FromArgb(30, 42, 62);
        internal static readonly Color Muted = Color.FromArgb(118, 130, 151);
        internal static readonly Color Blue = Color.FromArgb(62, 103, 226);
        internal static readonly Color BlueDark = Color.FromArgb(45, 79, 188);
        internal static readonly Color BlueSoft = Color.FromArgb(236, 241, 255);
        internal static readonly Color Red = Color.FromArgb(206, 74, 91);
        internal static readonly Color Yellow = Color.FromArgb(230, 177, 75);
        internal static readonly Color Border = Color.FromArgb(229, 234, 241);
        internal static readonly Color Sidebar = Color.FromArgb(23, 34, 53);
        internal static readonly Color Green = Color.FromArgb(31, 145, 114);
    }

    internal static class MoyuDrawing
    {
        internal static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (rectangle.Width <= 0 || rectangle.Height <= 0) return path;
            int diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height)));
            Rectangle arc = new Rectangle(rectangle.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90); arc.X = rectangle.Right - diameter;
            path.AddArc(arc, 270, 90); arc.Y = rectangle.Bottom - diameter;
            path.AddArc(arc, 0, 90); arc.X = rectangle.Left;
            path.AddArc(arc, 90, 90); path.CloseFigure();
            return path;
        }

        internal static Color Blend(Color first, Color second, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            return Color.FromArgb((int)(first.A + (second.A - first.A) * amount),
                (int)(first.R + (second.R - first.R) * amount), (int)(first.G + (second.G - first.G) * amount),
                (int)(first.B + (second.B - first.B) * amount));
        }

        internal static void RoundControl(Control control, int radius)
        {
            if (control == null || control.Width <= 0 || control.Height <= 0) return;
            using (GraphicsPath path = RoundedRectangle(new Rectangle(0, 0, control.Width, control.Height), radius))
            {
                Region old = control.Region; control.Region = new Region(path); if (old != null) old.Dispose();
            }
        }

        internal static void Glyph(Graphics g, string kind, Rectangle r, Color color)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(r.X, r.Y); g.ScaleTransform(r.Width / 24f, r.Height / 24f);
            using (Pen p = new Pen(color, 1.6f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                if (kind == "library")
                {
                    g.DrawLines(p, new[] { new Point(3, 7), new Point(3, 4), new Point(10, 4), new Point(13, 7), new Point(21, 7), new Point(21, 20), new Point(3, 20), new Point(3, 7) });
                    g.DrawLine(p, 3, 10, 21, 10);
                }
                else if (kind == "settings")
                {
                    g.DrawLine(p, 5, 3, 5, 21); g.DrawLine(p, 12, 3, 12, 21); g.DrawLine(p, 19, 3, 19, 21);
                    using (SolidBrush b = new SolidBrush(color)) { g.FillEllipse(b, 2, 6, 6, 6); g.FillEllipse(b, 9, 13, 6, 6); g.FillEllipse(b, 16, 5, 6, 6); }
                }
                else
                {
                    g.DrawRectangle(p, 3, 4, 18, 16);
                    g.DrawLines(p, new[] { new Point(10, 8), new Point(15, 12), new Point(10, 16), new Point(10, 8) });
                }
            }
            g.Restore(state);
        }
    }

    internal sealed class MoyuBrandPanel : Panel
    {
        public Icon BrandIcon { get; set; }
        public MoyuBrandPanel() { DoubleBuffered = true; BackColor = MoyuPalette.Sidebar; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float scale = e.Graphics.DpiX / 96f;
            e.Graphics.ScaleTransform(scale, scale);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (BrandIcon != null) e.Graphics.DrawIcon(BrandIcon, new Rectangle(22, 29, 38, 38));
            using (Font title = new Font("Microsoft YaHei UI", 19f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font subtitle = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Brush white = new SolidBrush(Color.White))
            using (Brush muted = new SolidBrush(Color.FromArgb(142, 159, 185)))
            {
                e.Graphics.DrawString("魔芋", title, white, 72, 27);
                e.Graphics.DrawString("MOYU / SUBTITLE", subtitle, muted, 73, 55);
            }
        }
    }

    internal sealed class MoyuPageHost : TabControl
    {
        public MoyuPageHost()
        {
            Appearance = TabAppearance.FlatButtons; ItemSize = new Size(1, 1); SizeMode = TabSizeMode.Fixed;
            Multiline = true; Padding = Point.Empty;
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x1328 && !DesignMode) message.Result = new IntPtr(1);
            else base.WndProc(ref message);
        }
    }

    internal class MoyuButton : Button
    {
        protected bool Hovering;
        private bool pressed;
        public int CornerRadius { get; set; }
        public Color HoverBackColor { get; set; }
        public MoyuButton()
        {
            CornerRadius = 9; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnMouseEnter(EventArgs e) { Hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hovering = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? MoyuPalette.Page : Parent.BackColor);
            Color fill = !Enabled ? MoyuPalette.Border : pressed ? MoyuDrawing.Blend(BackColor, Color.Black, .10f)
                : Hovering ? (HoverBackColor.IsEmpty ? MoyuDrawing.Blend(BackColor, MoyuPalette.Ink, .05f) : HoverBackColor) : BackColor;
            Color text = Enabled ? ForeColor : MoyuPalette.Muted;
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = MoyuDrawing.RoundedRectangle(rect, CornerRadius))
            {
                using (Brush brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                if (FlatAppearance.BorderSize > 0) using (Pen pen = new Pen(FlatAppearance.BorderColor)) e.Graphics.DrawPath(pen, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, rect, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) { rect.Inflate(-5, -5); ControlPaint.DrawFocusRectangle(e.Graphics, rect, text, fill); }
        }
    }

    internal sealed class MoyuNavButton : MoyuButton
    {
        public bool Selected { get; set; }
        public string GlyphKind { get; set; }
        public MoyuNavButton() { TextAlign = ContentAlignment.MiddleLeft; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(MoyuPalette.Sidebar); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96f;
            Color text = Selected ? Color.White : Color.FromArgb(164, 180, 202);
            if (Selected || Hovering)
            {
                using (GraphicsPath path = MoyuDrawing.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 9))
                using (Brush brush = new SolidBrush(Selected ? MoyuPalette.Blue : Color.FromArgb(33, 48, 70))) e.Graphics.FillPath(brush, path);
            }
            MoyuDrawing.Glyph(e.Graphics, GlyphKind, new Rectangle((int)(16 * scale), (Height - (int)(21 * scale)) / 2, (int)(21 * scale), (int)(21 * scale)), text);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle((int)(50 * scale), 0, Width - (int)(55 * scale), Height), text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), text, MoyuPalette.Sidebar);
        }
    }

    internal sealed class MoyuInputFrame : Panel
    {
        private readonly TextBox input;
        public MoyuInputFrame(TextBox textBox)
        {
            input = textBox; DoubleBuffered = true; BackColor = MoyuPalette.Page;
            input.BorderStyle = BorderStyle.None; input.BackColor = BackColor; input.ForeColor = MoyuPalette.Ink;
            input.Location = new Point(12, 12); input.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            input.Width = Math.Max(10, Width - 24); Controls.Add(input);
            input.Enter += delegate { Invalidate(); }; input.Leave += delegate { Invalidate(); };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = MoyuDrawing.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            using (Pen border = new Pen(input.Focused ? MoyuPalette.Blue : MoyuPalette.Border)) e.Graphics.DrawPath(border, path);
        }
    }

    internal sealed class MoyuProgressBar : ProgressBar
    {
        public Color TrackColor { get; set; }
        public Color FillColor { get; set; }
        public MoyuProgressBar()
        {
            TrackColor = MoyuPalette.Border; FillColor = MoyuPalette.Blue;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = MoyuDrawing.RoundedRectangle(track, Height / 2))
            using (Brush brush = new SolidBrush(TrackColor)) e.Graphics.FillPath(brush, path);
            double ratio = Maximum == Minimum ? 0 : (Value - Minimum) / (double)(Maximum - Minimum);
            int width = (int)(track.Width * Math.Max(0, Math.Min(1, ratio)));
            if (width > 0)
            {
                using (GraphicsPath path = MoyuDrawing.RoundedRectangle(new Rectangle(0, 0, width, track.Height), Height / 2))
                using (Brush brush = new SolidBrush(FillColor)) e.Graphics.FillPath(brush, path);
            }
        }
    }

    internal sealed class MoyuStageStrip : Control
    {
        public int Percent { get; set; }
        public bool Running { get; set; }
        public MoyuStageStrip() { DoubleBuffered = true; AccessibleName = "字幕处理阶段"; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            string[] labels = { "准备视频", "语音识别", "场景翻译", "字幕归档" };
            int active = Percent < 3 ? 0 : Percent < 45 ? 1 : Percent < 96 ? 2 : 3;
            float scale = e.Graphics.DpiX / 96f;
            int circle = (int)(22 * scale);
            for (int i = 0; i < labels.Length; i++)
            {
                int x = i * Width / 4;
                bool done = Percent == 100 || (Running && i < active);
                Color color = done ? MoyuPalette.Green : Running && i == active ? MoyuPalette.Blue : MoyuPalette.Muted;
                using (Brush brush = new SolidBrush(done ? Color.FromArgb(230, 246, 240) : Running && i == active ? MoyuPalette.BlueSoft : MoyuPalette.Page))
                    e.Graphics.FillEllipse(brush, x, 2, circle, circle);
                TextRenderer.DrawText(e.Graphics, done ? "✓" : (i + 1).ToString(), Font, new Rectangle(x, 2, circle, circle), color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(e.Graphics, labels[i], Font, new Rectangle(x + circle + (int)(8 * scale), 0, Width / 4 - circle - 10, circle + 4), color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
