using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PotPlayerAiSubtitle
{
    internal static class MoyuPalette
    {
        internal static readonly Color Page = Color.FromArgb(255, 250, 237);
        internal static readonly Color Ink = Color.FromArgb(23, 46, 68);
        internal static readonly Color Muted = Color.FromArgb(97, 113, 123);
        internal static readonly Color Blue = Color.FromArgb(8, 118, 198);
        internal static readonly Color BlueDark = Color.FromArgb(5, 105, 181);
        internal static readonly Color BlueSoft = Color.FromArgb(233, 245, 253);
        internal static readonly Color Red = Color.FromArgb(206, 74, 91);
        internal static readonly Color Yellow = Color.FromArgb(255, 211, 78);
        internal static readonly Color Border = Color.FromArgb(193, 204, 202);
        internal static readonly Color Sidebar = Color.FromArgb(8, 118, 198);
        internal static readonly Color Green = Color.FromArgb(34, 129, 101);
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
                if (kind == "task")
                {
                    g.DrawPolygon(p, new[] { new Point(12, 2), new Point(15, 9), new Point(22, 12), new Point(15, 15), new Point(12, 22), new Point(9, 15), new Point(2, 12), new Point(9, 9) });
                }
                else if (kind == "library")
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

    internal sealed class MoyuBrandPanel : MoyuSurfacePanel
    {
        public Icon BrandIcon { get; set; }
        public MoyuBrandPanel() { DoubleBuffered = true; BackColor = MoyuPalette.Sidebar; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            float scale = g.DpiX / 96f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(MoyuArtwork.Logo, new Rectangle((int)(19 * scale), (int)(22 * scale), (int)(58 * scale), (int)(58 * scale)));
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (Font title = new Font(MoyuTypography.FamilyName, 29f * 0.75f, FontStyle.Bold))
            using (Font subtitle = new Font(MoyuTypography.FamilyName, 11f * 0.75f, FontStyle.Regular))
            using (Brush white = new SolidBrush(Color.White))
            using (Brush muted = new SolidBrush(Color.FromArgb(245, 250, 255)))
            {
                g.DrawString("魔芋", title, white, 82 * scale, 24 * scale);
                g.DrawString("你的翻译道具箱", subtitle, muted, 82 * scale, 66 * scale);
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
        protected readonly MoyuMotion Motion;
        protected float HoverAmount { get { return Motion[0]; } }
        protected float PressAmount { get { return Motion[1]; } }
        public int CornerRadius { get; set; }
        public Color HoverBackColor { get; set; }
        public MoyuButton()
        {
            Motion = new MoyuMotion(this);
            CornerRadius = 9; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        private void AnimateInteraction()
        {
            if (Motion == null) return;
            Motion.To(0, Enabled && Hovering ? 1 : 0, 140);
            Motion.To(1, Enabled && pressed ? 1 : 0, pressed ? 65 : 120);
        }
        protected override void OnMouseEnter(EventArgs e) { Hovering = true; AnimateInteraction(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hovering = false; pressed = false; AnimateInteraction(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; AnimateInteraction(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; AnimateInteraction(); base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) { pressed = false; AnimateInteraction(); } base.OnMouseCaptureChanged(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; AnimateInteraction(); } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = false; AnimateInteraction(); } base.OnKeyUp(e); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; AnimateInteraction(); base.OnLostFocus(e); }
        protected override void OnVisibleChanged(EventArgs e) { if (!Visible) { Hovering = pressed = false; AnimateInteraction(); } base.OnVisibleChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) Hovering = pressed = false; AnimateInteraction(); Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            MoyuSurface.Background(e.Graphics, this, Parent == null ? MoyuPalette.Page : Parent.BackColor);
            Color hover = HoverBackColor.IsEmpty ? MoyuDrawing.Blend(BackColor, MoyuPalette.Ink, .08f) : HoverBackColor;
            Color fill = !Enabled ? MoyuDrawing.Blend(MoyuPalette.Page, MoyuPalette.Border, .35f) : MoyuDrawing.Blend(MoyuDrawing.Blend(BackColor, hover, HoverAmount), MoyuDrawing.Blend(BackColor, Color.Black, .12f), PressAmount);
            Color text = Enabled ? ForeColor : MoyuPalette.Muted;
            bool prominent = Enabled && (BackColor == MoyuPalette.Blue || BackColor == MoyuPalette.Yellow);
            int offset = prominent && Enabled ? Math.Max(2, (int)(3 * e.Graphics.DpiX / 96f)) : 0;
            Rectangle rect = new Rectangle(1, 1, Math.Max(1, Width - offset - 3), Math.Max(1, Height - offset - 3));
            if (offset > 0)
            {
                Rectangle shadow = rect; shadow.Offset(offset, offset);
                using (GraphicsPath shadowPath = MoyuDrawing.RoundedRectangle(shadow, CornerRadius))
                using (Brush ink = new SolidBrush(MoyuPalette.Ink)) e.Graphics.FillPath(ink, shadowPath);
            }
            int depression = (int)Math.Round(offset * PressAmount); rect.Offset(depression, depression);
            using (GraphicsPath path = MoyuDrawing.RoundedRectangle(rect, CornerRadius))
            {
                using (Brush brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                if (prominent && Enabled)
                {
                    GraphicsState state = e.Graphics.Save(); e.Graphics.SetClip(path, CombineMode.Intersect);
                    MoyuSurface.Grain(e.Graphics, this, rect, BackColor == MoyuPalette.Blue); e.Graphics.Restore(state);
                }
                using (Pen pen = new Pen(prominent ? MoyuPalette.Ink : MoyuPalette.Border, prominent ? 1.8f : 1f)) e.Graphics.DrawPath(pen, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, rect, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) { rect.Inflate(-5, -5); ControlPaint.DrawFocusRectangle(e.Graphics, rect, text, fill); }
        }
    }

    internal sealed class MoyuNavButton : MoyuButton
    {
        private bool selected;
        public bool Selected { get { return selected; } set { if (selected == value) return; selected = value; Motion.To(2, value ? 1 : 0, 170); Invalidate(); } }
        public string GlyphKind { get; set; }
        public MoyuNavButton() { TextAlign = ContentAlignment.MiddleLeft; }
        protected override void OnPaint(PaintEventArgs e)
        {
            MoyuSurface.Background(e.Graphics, this, MoyuPalette.Sidebar); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96f, selection = Motion[2];
            Color text = MoyuDrawing.Blend(Color.FromArgb(245, 250, 255), MoyuPalette.Ink, selection);
            if (selection > 0 || HoverAmount > 0)
            {
                int shadow = Math.Max(2, (int)(3 * scale));
                Rectangle face = new Rectangle(1, 1, Width - shadow - 3, Height - shadow - 3);
                Rectangle behind = face; behind.Offset(shadow, shadow);
                using (GraphicsPath path = MoyuDrawing.RoundedRectangle(behind, 8))
                using (Brush ink = new SolidBrush(Color.FromArgb((int)(255 * selection), MoyuPalette.Ink))) e.Graphics.FillPath(ink, path);
                int depression = (int)Math.Round(shadow * PressAmount); face.Offset(depression, depression);
                Color fill = MoyuDrawing.Blend(MoyuDrawing.Blend(MoyuPalette.Sidebar, MoyuPalette.BlueDark, HoverAmount), MoyuPalette.Yellow, selection);
                fill = MoyuDrawing.Blend(fill, MoyuDrawing.Blend(fill, Color.Black, .08f), PressAmount);
                using (GraphicsPath path = MoyuDrawing.RoundedRectangle(face, 8))
                {
                    using (Brush brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                    if (selection > .99f)
                    {
                        GraphicsState state = e.Graphics.Save(); e.Graphics.SetClip(path, CombineMode.Intersect);
                        MoyuSurface.Grain(e.Graphics, this, face, false); e.Graphics.Restore(state);
                    }
                    using (Pen pen = new Pen(Color.FromArgb((int)(255 * selection), MoyuPalette.Ink), 1.8f)) e.Graphics.DrawPath(pen, path);
                }
            }
            int shift = (int)Math.Round(3 * scale * PressAmount);
            MoyuDrawing.Glyph(e.Graphics, GlyphKind, new Rectangle((int)(16 * scale) + shift, (Height - (int)(21 * scale)) / 2 + shift, (int)(21 * scale), (int)(21 * scale)), text);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle((int)(50 * scale) + shift, shift, Width - (int)(55 * scale), Height), text,
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
        private readonly MoyuMotion motion;
        internal float DisplayedValue { get { return motion[0]; } }
        internal bool IsAnimating { get { return motion.IsRunning; } }
        public new int Value
        {
            get { return base.Value; }
            set { int previous = base.Value; base.Value = value; motion.To(0, value, value <= previous || value == Maximum ? 0 : 220); Invalidate(); }
        }
        public Color TrackColor { get; set; }
        public Color FillColor { get; set; }
        public MoyuProgressBar()
        {
            motion = new MoyuMotion(this);
            TrackColor = MoyuPalette.Page; FillColor = MoyuPalette.Yellow;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = MoyuDrawing.RoundedRectangle(track, Height / 2))
            {
                using (Brush brush = new SolidBrush(TrackColor)) e.Graphics.FillPath(brush, path);
                using (Pen pen = new Pen(MoyuPalette.Ink)) e.Graphics.DrawPath(pen, path);
            }
            double ratio = Maximum == Minimum ? 0 : (DisplayedValue - Minimum) / (double)(Maximum - Minimum);
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
