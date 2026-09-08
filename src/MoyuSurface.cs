using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PotPlayerAiSubtitle
{
    internal static class MoyuEffects
    {
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);
        internal static bool MotionEnabled
        {
            get { int enabled; return !SystemInformation.HighContrast && !SystemInformation.TerminalServerSession
                && SystemParametersInfo(0x1042, 0, out enabled, 0) && enabled != 0; }
        }
        internal static bool TextureEnabled
        {
            get { int disabled; return !SystemInformation.HighContrast
                && SystemParametersInfo(0x1040, 0, out disabled, 0) && disabled == 0; }
        }
    }

    // Immutable, small process-lifetime tiles; paint never generates noise or schedules another frame.
    internal static class MoyuSurface
    {
        private static readonly Bitmap paperTile = MakeTile(false);
        private static readonly Bitmap printTile = MakeTile(true);
        private static Bitmap paperScaled, printScaled;
        private static TextureBrush paperBrush, printBrush;
        private static int scaledSize;
        private static Bitmap MakeTile(bool blue)
        {
            Bitmap tile = new Bitmap(192, 192);
            Random random = new Random(blue ? 27 : 42);
            for (int i = 0; i < 2100; i++)
                tile.SetPixel(random.Next(192), random.Next(192), Color.FromArgb(random.Next(8, 20), blue ? Color.White : Color.FromArgb(125, 95, 54)));
            using (Graphics g = Graphics.FromImage(tile))
            using (Pen fiber = new Pen(Color.FromArgb(8, blue ? Color.White : Color.FromArgb(145, 105, 55))))
            {
                for (int i = 0; i < 100; i++)
                {
                    int x = random.Next(188), y = random.Next(192);
                    g.DrawLine(fiber, x, y, x + random.Next(2, 5), y);
                }
                if (blue) using (Brush dot = new SolidBrush(Color.FromArgb(10, Color.White)))
                    for (int y = 4; y < 192; y += 12)
                        for (int x = (y / 12 % 2) * 6 + 4; x < 192; x += 12) g.FillEllipse(dot, x, y, 2, 2);
            }
            return tile;
        }
        private static Bitmap ScaleTile(Bitmap source, int size)
        {
            Bitmap scaled = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(source, 0, 0, size, size);
            }
            return scaled;
        }
        private static void EnsureScale(Graphics g)
        {
            int size = (int)Math.Round(192 * (g.DpiX / 96f));
            if (paperBrush != null && size == scaledSize) return;
            if (paperBrush != null) { paperBrush.Dispose(); paperBrush = null; }
            if (printBrush != null) { printBrush.Dispose(); printBrush = null; }
            if (paperScaled != null) { paperScaled.Dispose(); paperScaled = null; }
            if (printScaled != null) { printScaled.Dispose(); printScaled = null; }
            paperScaled = ScaleTile(paperTile, size); printScaled = ScaleTile(printTile, size);
            paperBrush = new TextureBrush(paperScaled, WrapMode.Tile);
            printBrush = new TextureBrush(printScaled, WrapMode.Tile);
            scaledSize = size;
        }
        internal static void Background(Graphics g, Control owner, Color color)
        {
            // Cover every edge pixel before antialiased foreground painting. Otherwise the
            // buffered window's previous pixels bleed through as dark top/left hairlines.
            SmoothingMode smoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            using (Brush fill = new SolidBrush(color)) g.FillRectangle(fill, owner.ClientRectangle);
            g.SmoothingMode = smoothing;
            if (color == MoyuPalette.Page || color == MoyuPalette.Sidebar) Grain(g, owner, owner.ClientRectangle, color == MoyuPalette.Sidebar);
        }
        internal static void Grain(Graphics g, Control owner, Rectangle area, bool blue)
        {
            if (!MoyuEffects.TextureEnabled) return;
            EnsureScale(g);
            // Anchor all child surfaces to the window, including transparent label repainting.
            Point offset = Point.Empty;
            for (Control c = owner; c != null && !(c is Form); c = c.Parent) offset.Offset(c.Left, c.Top);
            TextureBrush brush = blue ? printBrush : paperBrush;
            // Pre-scaled tile + translation (no per-pixel scale) is dramatically faster than a
            // scaled TextureBrush, which has to resample every pixel on high-DPI displays.
            using (Matrix matrix = new Matrix(1f, 0, 0, 1f, -offset.X, -offset.Y))
            {
                brush.Transform = matrix;
                g.FillRectangle(brush, area);
            }
        }
    }
    internal class MoyuSurfacePanel : Panel
    {
        internal MoyuSurfacePanel() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }
        protected override void OnPaintBackground(PaintEventArgs e) { MoyuSurface.Background(e.Graphics, this, BackColor); }
    }
    internal sealed class MoyuSurfaceFlow : FlowLayoutPanel
    {
        internal MoyuSurfaceFlow() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }
        protected override void OnPaintBackground(PaintEventArgs e) { MoyuSurface.Background(e.Graphics, this, BackColor); }
    }

    // Three channels share one lazily created UI timer. Logical state never waits for visual state.
    internal sealed class MoyuMotion : IDisposable
    {
        private readonly Control owner;
        private readonly Func<bool> allowed;
        private readonly float[] values = new float[3], starts = new float[3], targets = new float[3];
        private readonly double[] began = new double[3], durations = new double[3];
        private Timer timer;
        private bool disposed;
        internal MoyuMotion(Control control) : this(control, delegate { return MoyuEffects.MotionEnabled; }) { }
        internal MoyuMotion(Control control, Func<bool> policy)
        {
            owner = control; allowed = policy;
            owner.VisibleChanged += AvailabilityChanged; owner.EnabledChanged += AvailabilityChanged;
            owner.HandleDestroyed += AvailabilityChanged; owner.Disposed += OwnerDisposed;
        }
        internal float this[int channel] { get { return values[channel]; } }
        internal bool IsRunning { get { return timer != null && timer.Enabled; } }
        private static double Now { get { return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency; } }
        private bool CanAnimate { get { return !disposed && owner.IsHandleCreated && owner.Visible && owner.Enabled && allowed(); } }
        internal void To(int channel, float target, int milliseconds)
        {
            if (disposed) return;
            Advance();
            if (targets[channel] == target && values[channel] == target) return;
            starts[channel] = values[channel]; targets[channel] = target; began[channel] = Now; durations[channel] = milliseconds;
            if (milliseconds <= 0 || !CanAnimate) { values[channel] = target; Advance(); owner.Invalidate(); return; }
            if (timer == null) { timer = new Timer { Interval = 15 }; timer.Tick += Tick; }
            timer.Start(); owner.Invalidate();
        }
        private void Advance()
        {
            bool moving = false; double now = Now;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == targets[i]) continue;
                double t = durations[i] <= 0 ? 1 : Math.Min(1, (now - began[i]) / durations[i]);
                values[i] = t >= 1 ? targets[i] : starts[i] + (targets[i] - starts[i]) * (float)(1 - Math.Pow(1 - t, 3));
                moving |= values[i] != targets[i];
            }
            if (!moving && timer != null) timer.Stop();
        }
        private void Tick(object sender, EventArgs e)
        {
            if (!CanAnimate) Snap(); else Advance();
            if (!owner.IsDisposed) owner.Invalidate();
        }
        internal void Snap()
        {
            Array.Copy(targets, values, values.Length);
            if (timer != null) timer.Stop();
            if (!owner.IsDisposed) owner.Invalidate();
        }
        private void AvailabilityChanged(object sender, EventArgs e) { if (!owner.Visible || !owner.Enabled || !owner.IsHandleCreated) Snap(); }
        private void OwnerDisposed(object sender, EventArgs e) { Dispose(); }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (timer != null) { timer.Stop(); timer.Tick -= Tick; timer.Dispose(); }
            owner.VisibleChanged -= AvailabilityChanged; owner.EnabledChanged -= AvailabilityChanged;
            owner.HandleDestroyed -= AvailabilityChanged; owner.Disposed -= OwnerDisposed;
        }
    }
}
