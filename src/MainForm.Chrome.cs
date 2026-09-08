using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PotPlayerAiSubtitle
{
    internal sealed partial class MainForm
    {
        private MoyuWindowButton minimizeWindowButton, maximizeWindowButton, closeWindowButton;
        private MoyuTitlebar titlebar;
        private bool restoringNativeSize, nonNormalWindow;
        private Size nativeRestoreSize;
        private int windowMessageDepth;
        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            // WinForms may add the removed caption/frame again after nested WM_SIZE returns.
            if (restoringNativeSize)
            {
                if ((specified & BoundsSpecified.Width) != 0) width = nativeRestoreSize.Width;
                if ((specified & BoundsSpecified.Height) != 0) height = nativeRestoreSize.Height;
            }
            base.SetBoundsCore(x, y, width, height, specified);
        }
        // Keep standard sizing, system-menu and taskbar behaviour while drawing the caption ourselves.
        protected override CreateParams CreateParams
        {
            get { CreateParams value = base.CreateParams; value.Style |= 0x000F0000; return value; }
        }
        private void BuildWindowControls(MoyuTitlebar header)
        {
            titlebar = header;
            minimizeWindowButton = new MoyuWindowButton("minimize", "最小化");
            maximizeWindowButton = new MoyuWindowButton("maximize", "最大化");
            closeWindowButton = new MoyuWindowButton("close", "关闭到托盘");
            foreach (MoyuWindowButton button in new[] { minimizeWindowButton, maximizeWindowButton, closeWindowButton })
            { button.Size = new Size(45, 48); button.Anchor = AnchorStyles.Top | AnchorStyles.Right; header.Controls.Add(button); }
            minimizeWindowButton.Left = header.Width - 135;
            maximizeWindowButton.Left = header.Width - 90;
            closeWindowButton.Left = header.Width - 45;
            minimizeWindowButton.Click += delegate { WindowState = FormWindowState.Minimized; };
            maximizeWindowButton.Click += delegate { ToggleMaximize(); };
            closeWindowButton.Click += delegate { Close(); };
            Resize += delegate
            {
                maximizeWindowButton.RestoredGlyph = WindowState == FormWindowState.Maximized;
                maximizeWindowButton.AccessibleName = maximizeWindowButton.RestoredGlyph ? "还原窗口" : "最大化";
                maximizeWindowButton.Invalidate();
            };
        }
        internal void ToggleMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }
        protected override void OnActivated(EventArgs e) { RefreshChrome(); base.OnActivated(e); }
        protected override void OnDeactivate(EventArgs e) { RefreshChrome(); base.OnDeactivate(e); }
        private void RefreshChrome()
        {
            if (titlebar != null) titlebar.Invalidate();
            if (minimizeWindowButton != null) minimizeWindowButton.Invalidate();
            if (maximizeWindowButton != null) maximizeWindowButton.Invalidate();
            if (closeWindowButton != null) closeWindowButton.Invalidate();
        }
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        internal void BeginCaptionDrag()
        {
            if (WindowState != FormWindowState.Normal) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
        }
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch { }
        }
        internal int WindowHitTest(Point point)
        {
            float scale = titlebar == null ? 1 : titlebar.Height / 48f;
            int grip = Math.Max(5, (int)(6 * scale));
            if (WindowState == FormWindowState.Normal)
            {
                bool left = point.X < grip, right = point.X >= ClientSize.Width - grip;
                bool top = point.Y < grip, bottom = point.Y >= ClientSize.Height - grip;
                if (top) return left ? 13 : right ? 14 : 12;
                if (bottom) return left ? 16 : right ? 17 : 15;
                if (left) return 10;
                if (right) return 11;
            }
            if (point.Y >= 0 && point.Y < 48 * scale && point.X < ClientSize.Width - 135 * scale) return 2;
            return 1;
        }
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        private void SendWindowCommand(int command) { SendMessage(Handle, 0x112, new IntPtr(command), IntPtr.Zero); }
        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr window);
        [StructLayout(LayoutKind.Sequential)]
        private struct WindowRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WindowLimits { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
        protected override void WndProc(ref Message message)
        {
            windowMessageDepth++;
            try { ProcessWindowMessage(ref message); }
            finally { if (--windowMessageDepth == 0) restoringNativeSize = false; }
        }
        private void ProcessWindowMessage(ref Message message)
        {
            const int NcCalcSize = 0x83, NcHitTest = 0x84, GetMinMaxInfo = 0x24, WmNcPaint = 0x85, WmNcActivate = 0x86;
            // Our non-client chrome is drawn entirely in the client area; never let the OS paint a
            // default frame over it (this is what flashes a plain bar on focus changes).
            if (message.Msg == WmNcPaint) { message.Result = IntPtr.Zero; return; }
            if (message.Msg == WmNcActivate)
            {
                message.Result = new IntPtr(1); // handled: keep our custom chrome steady
                return;
            }
            if (message.Msg == NcCalcSize)
            {
                if (message.WParam != IntPtr.Zero)
                {
                    // Windows retains an invisible resize frame outside a maximized window.
                    // Keep the drawable client inside the target monitor's working area.
                    if (IsZoomed(Handle))
                    {
                        WindowRect client = (WindowRect)Marshal.PtrToStructure(message.LParam, typeof(WindowRect));
                        Rectangle work = Screen.FromRectangle(Rectangle.FromLTRB(client.Left, client.Top, client.Right, client.Bottom)).WorkingArea;
                        client.Left = work.Left; client.Top = work.Top; client.Right = work.Right; client.Bottom = work.Bottom;
                        Marshal.StructureToPtr(client, message.LParam, false);
                    }
                }
                else
                {
                    // Old-style call: lParam is a RECT; keep the client equal to the whole window.
                    WindowRect rect = (WindowRect)Marshal.PtrToStructure(message.LParam, typeof(WindowRect));
                    if (IsZoomed(Handle))
                    {
                        Rectangle work = Screen.FromRectangle(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)).WorkingArea;
                        rect.Left = work.Left; rect.Top = work.Top; rect.Right = work.Right; rect.Bottom = work.Bottom;
                        Marshal.StructureToPtr(rect, message.LParam, false);
                    }
                }
                message.Result = IntPtr.Zero; return;
            }
            if (message.Msg == NcHitTest)
            {
                long packed = message.LParam.ToInt64();
                Point point = PointToClient(new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff))));
                message.Result = new IntPtr(WindowHitTest(point)); return;
            }
            int originalMessage = message.Msg;
            bool restore = message.Msg == 0x5 && message.WParam == IntPtr.Zero && nonNormalWindow;
            if (message.Msg == 0x5 && message.WParam != IntPtr.Zero) nonNormalWindow = true;
            if (restore)
            {
                long size = message.LParam.ToInt64();
                nativeRestoreSize = new Size((int)(size & 0xffff), (int)((size >> 16) & 0xffff));
                restoringNativeSize = true; nonNormalWindow = false;
            }
            base.WndProc(ref message);
            if (originalMessage == GetMinMaxInfo)
            {
                Screen screen = Screen.FromHandle(Handle);
                WindowLimits limits = (WindowLimits)Marshal.PtrToStructure(message.LParam, typeof(WindowLimits));
                limits.MaxPosition = new Point(screen.WorkingArea.Left - screen.Bounds.Left, screen.WorkingArea.Top - screen.Bounds.Top);
                limits.MaxSize = new Point(screen.WorkingArea.Width, screen.WorkingArea.Height);
                limits.MinTrackSize = new Point(MinimumSize.Width, MinimumSize.Height);
                Marshal.StructureToPtr(limits, message.LParam, false);
            }
        }
    }
    internal sealed class MoyuWindowButton : MoyuButton
    {
        private readonly string kind;
        internal bool RestoredGlyph { get; set; }
        internal MoyuWindowButton(string kind, string label)
        {
            this.kind = kind; AccessibleName = label; AccessibleRole = AccessibleRole.PushButton;
            BackColor = MoyuPalette.Page; ForeColor = MoyuPalette.Muted; Cursor = Cursors.Default;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            MoyuSurface.Background(g, this, MoyuPalette.Page);
            Color hover = kind == "close" ? Color.FromArgb(215, 77, 87) : MoyuDrawing.Blend(MoyuPalette.Page, MoyuPalette.Ink, .07f);
            using (Brush brush = new SolidBrush(Color.FromArgb((int)(255 * HoverAmount), hover))) g.FillRectangle(brush, ClientRectangle);
            float scale = g.DpiX / 96f;
            GraphicsState state = g.Save(); g.TranslateTransform(Width / 2f, Height / 2f); g.ScaleTransform(scale, scale);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(kind == "close" ? MoyuDrawing.Blend(ForeColor, Color.White, HoverAmount) : ForeColor, 1.15f))
            {
                if (kind == "minimize") g.DrawLine(pen, -5, 0, 5, 0);
                else if (kind == "close") { g.DrawLine(pen, -4, -4, 4, 4); g.DrawLine(pen, 4, -4, -4, 4); }
                else if (RestoredGlyph) { g.DrawRectangle(pen, -5, -2, 7, 7); g.DrawLines(pen, new[] { new Point(-2, -3), new Point(-2, -5), new Point(5, -5), new Point(5, 2), new Point(3, 2) }); }
                else g.DrawRectangle(pen, -4, -4, 8, 8);
            }
            g.Restore(state);
            if (Focused && ShowFocusCues) { Rectangle focus = ClientRectangle; focus.Inflate(-7, -7); ControlPaint.DrawFocusRectangle(g, focus); }
        }
    }

    // Integrated 48px caption: self-drawn page name and status (crisp ClearType), its own drag and
    // double-click behaviour, and the three window buttons on the right as real child controls.
    internal sealed class MoyuTitlebar : MoyuSurfacePanel
    {
        private readonly MainForm owner;
        private string pageName = "";
        private string statusText = "";
        private Color statusColor = MoyuPalette.Blue;
        internal MoyuTitlebar(MainForm form)
        {
            owner = form; BackColor = MoyuPalette.Page; TabStop = false;
        }
        internal void SetPage(string name) { pageName = name; Invalidate(); }
        internal void SetStatus(string text, Color color) { statusText = text; statusColor = color; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics; float scale = g.DpiX / 96f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (Font nameFont = new Font(MoyuTypography.FamilyName, 9.75f, FontStyle.Regular))
            using (Font statusFont = new Font(MoyuTypography.FamilyName, 9f, FontStyle.Regular))
            using (Brush muted = new SolidBrush(MoyuPalette.Muted))
            using (Brush status = new SolidBrush(statusColor))
            {
                if (!string.IsNullOrEmpty(pageName))
                {
                    float line = nameFont.GetHeight(g);
                    g.DrawString(pageName, nameFont, muted, 34 * scale, (Height - line) / 2f);
                }
                if (!string.IsNullOrEmpty(statusText))
                {
                    SizeF size = g.MeasureString(statusText, statusFont);
                    float dot = 6 * scale, gap = 8 * scale;
                    float buttons = 45 * 3 * scale, margin = 21 * scale;
                    float sx = Width - buttons - margin - size.Width - dot - gap;
                    float line = statusFont.GetHeight(g);
                    using (Brush dotBrush = new SolidBrush(statusColor)) g.FillEllipse(dotBrush, sx, (Height - dot) / 2f, dot, dot);
                    g.DrawString(statusText, statusFont, status, sx + dot + gap, (Height - line) / 2f);
                }
            }
            using (Pen border = new Pen(MoyuPalette.Border)) g.DrawLine(border, 0, Height - 1, Width, Height - 1);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) owner.BeginCaptionDrag();
            base.OnMouseDown(e);
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) owner.ToggleMaximize();
            base.OnMouseDoubleClick(e);
        }
    }
}
