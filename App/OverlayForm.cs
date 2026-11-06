using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RaidAnsager.Engine;

namespace RaidAnsager.App;

/// <summary>
/// Click-through, always-on-top layered window with per-pixel alpha (works over the game in borderless/windowed
/// mode, not in exclusive fullscreen). Move mode makes it draggable.
/// </summary>
internal sealed class OverlayForm : Form
{
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8,
        WS_EX_NOACTIVATE = 0x08000000, GWL_EXSTYLE = -20, ULW_ALPHA = 2, WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private static CultureInfo Num => L.De ? De : CultureInfo.InvariantCulture;
    private readonly Settings _settings;
    private string _lastSignature = "";
    private bool _moveMode;
    private int _topmostTicks;

    public OverlayForm(Settings settings)
    {
        _settings = settings;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "Raid-Ansager Overlay";
        var screen = Screen.PrimaryScreen!.WorkingArea;
        var w = (int)(Width0 * settings.Scale);
        Location = settings.OverlayX >= 0 && OnSomeScreen(settings.OverlayX, settings.OverlayY)
            ? new Point(settings.OverlayX, settings.OverlayY)
            : new Point(screen.Left + (screen.Width - w) / 2, screen.Top + screen.Height / 6);
        Size = new Size(w, 40);
    }

    private const int Width0 = 460;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    private static bool OnSomeScreen(int x, int y) => Screen.AllScreens.Any(s => s.Bounds.Contains(x + 20, y + 10));

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool MoveMode
    {
        get => _moveMode;
        set
        {
            _moveMode = value;
            var ex = GetWindowLong(Handle, GWL_EXSTYLE);
            SetWindowLong(Handle, GWL_EXSTYLE, value ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT);
            if (!value)
            {
                _settings.OverlayX = Left;
                _settings.OverlayY = Top;
                _settings.Save();
            }
            _lastSignature = "";
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_moveMode || e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        _lastSignature = "";
    }

    /// <summary>Draws the state; cheap when nothing changed.</summary>
    public void Render(OverlayState st, string? status)
    {
        if (++_topmostTicks >= 40)
        {
            _topmostTicks = 0; // games sometimes push themselves above topmost windows
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
        var showStatus = _moveMode || status is not null || st.InModZone && st.Bosses.Count == 0;
        var signature = string.Join("|", st.Bosses.Select(b => $"{b.Name}{b.HpPct:0.0}{b.Phase}{(int)b.Seconds}")) + "#" +
                        string.Join("|", st.Alerts.Select(a => $"{a.Text}{Fade(a):0.00}")) + "#" +
                        string.Join("|", st.Bars.Select(b => $"{b.Text}{b.Remaining:0.0}")) + "#" +
                        string.Join("|", st.Unknown) + "#" + (showStatus ? status ?? st.Zone.ToString() : "") + _moveMode;
        if (signature == _lastSignature) return;
        _lastSignature = signature;

        using var bmp = Draw(st, status, _settings.Scale, _moveMode);
        Push(bmp);
    }

    /// <summary>The overlay as a bitmap with alpha (also used for offscreen previews).</summary>
    public static Bitmap Draw(OverlayState st, string? status, double scale, bool moveMode)
    {
        var showStatus = moveMode || status is not null || st.InModZone && st.Bosses.Count == 0;
        var k = (float)scale;
        var w = (int)(Width0 * k);
        var bossH = 26 * k;
        var alertH = 38 * k;
        var barH = 24 * k;
        var gap = 4 * k;
        var lineH = 17 * k;
        var lines = new List<(string Text, Color Color)>();
        if (showStatus)
            lines.Add((status ?? (st.InModZone ? $"{L.AppName} {L.T("ready", "bereit")} · Zone {st.Zone}" : L.AppName), Color.FromArgb(220, 200, 200, 210)));
        if (moveMode) lines.Add((L.T("Drag to move · Ctrl+Alt+O to lock", "Ziehen zum Verschieben · Strg+Alt+O fixiert"), Color.FromArgb(255, 255, 210, 74)));

        var h = st.Bosses.Count * (bossH + gap) + st.Alerts.Count * alertH + st.Bars.Count * (barH + gap) +
                st.Unknown.Count * lineH + lines.Count * lineH + 8 * k;
        if (moveMode) h = Math.Max(h, 140 * k);
        var height = Math.Max(1, (int)Math.Ceiling(h));

        var bmp = new Bitmap(w, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            if (moveMode)
            {
                using var bg = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
                g.FillRectangle(bg, 0, 0, w, height);
                using var pen = new Pen(Color.FromArgb(230, 255, 210, 74), 2) { DashStyle = DashStyle.Dash };
                g.DrawRectangle(pen, 1, 1, w - 3, height - 3);
            }
            var y = 4 * k;
            using var small = new Font("Segoe UI", 9.5f * k, FontStyle.Regular, GraphicsUnit.Point);
            using var bossFont = new Font("Segoe UI Semibold", 10.5f * k, FontStyle.Regular, GraphicsUnit.Point);
            using var barFont = new Font("Segoe UI Semibold", 10f * k, FontStyle.Regular, GraphicsUnit.Point);
            using var alertFamily = new FontFamily("Segoe UI");

            foreach (var b in st.Bosses)
            {
                var r = new RectangleF(0, y, w, bossH);
                using (var bg = new SolidBrush(Color.FromArgb(160, 12, 12, 18))) FillRound(g, bg, r, 6 * k);
                if (!double.IsNaN(b.HpPct))
                {
                    using var hp = new SolidBrush(Color.FromArgb(200, 190, 40, 40));
                    g.FillRectangle(hp, r.X + 6 * k, r.Bottom - 5 * k, (float)((w - 12 * k) * Math.Clamp(b.HpPct, 0, 100) / 100), 3 * k);
                }
                var text = $"{b.Name}   {(double.IsNaN(b.HpPct) ? "HP ?" : b.HpPct.ToString("0.0", Num) + " %")}" +
                           (b.Phase > 1 ? $"   Phase {b.Phase}" : "") + $"   {(int)(b.Seconds / 60)}:{(int)b.Seconds % 60:00}";
                using var fg = new SolidBrush(Color.White);
                g.DrawString(text, bossFont, fg, r.X + 8 * k, r.Y + 2 * k);
                y += bossH + gap;
            }

            foreach (var a in st.Alerts.AsEnumerable().Reverse())
            {
                var alpha = (int)(255 * Fade(a));
                var color = ParseColor(a.Color, alpha);
                using var path = new GraphicsPath();
                var size = 22f * k * 96f / 72f;
                var text = a.Text;
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                    path.AddString(text, alertFamily, (int)FontStyle.Bold, size, new RectangleF(0, y, w, alertH), fmt);
                using var outline = new Pen(Color.FromArgb(alpha, 0, 0, 0), 4.5f * k) { LineJoin = LineJoin.Round };
                g.DrawPath(outline, path);
                using var fill = new SolidBrush(color);
                g.FillPath(fill, path);
                y += alertH;
            }

            foreach (var b in st.Bars)
            {
                var r = new RectangleF(0, y, w, barH);
                using (var bg = new SolidBrush(Color.FromArgb(170, 18, 18, 28))) FillRound(g, bg, r, 5 * k);
                var frac = b.Total <= 0 ? 0 : (float)Math.Clamp(b.Remaining / b.Total, 0, 1);
                if (frac > 0)
                {
                    using var fill = new SolidBrush(b.Warning ? Color.FromArgb(220, 205, 55, 55) : Color.FromArgb(200, 58, 112, 200));
                    FillRound(g, fill, new RectangleF(r.X, r.Y, Math.Max(10 * k, r.Width * frac), r.Height), 5 * k);
                }
                using var fg = new SolidBrush(Color.White);
                g.DrawString(b.Text, barFont, fg, r.X + 8 * k, r.Y + 3 * k);
                var secs = b.Remaining >= 10 ? ((int)Math.Ceiling(b.Remaining)).ToString(Num) : b.Remaining.ToString("0.0", Num);
                var sz = g.MeasureString(secs, barFont);
                g.DrawString(secs, barFont, fg, r.Right - sz.Width - 6 * k, r.Y + 3 * k);
                y += barH + gap;
            }

            foreach (var u in st.Unknown)
            {
                using var fg = new SolidBrush(Color.FromArgb(200, 175, 175, 185));
                using var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
                g.DrawString("? " + u, small, shadow, 7 * k, y + 1);
                g.DrawString("? " + u, small, fg, 6 * k, y);
                y += lineH;
            }

            foreach (var (text, color) in lines)
            {
                using var fg = new SolidBrush(color);
                using var shadow = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
                g.DrawString(text, small, shadow, 7 * k, y + 1);
                g.DrawString(text, small, fg, 6 * k, y);
                y += lineH;
            }
        }
        return bmp;
    }

    private static double Fade(AlertView a)
    {
        var left = a.Seconds - a.Age;
        return Math.Clamp(left / 0.6, 0.15, 1);
    }

    private static Color ParseColor(string hex, int alpha)
    {
        try
        {
            var c = ColorTranslator.FromHtml(hex);
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return Color.FromArgb(alpha, 255, 210, 74);
        }
    }

    private static void FillRound(Graphics g, Brush brush, RectangleF r, float radius)
    {
        using var p = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        g.FillPath(brush, p);
    }

    /// <summary>UpdateLayeredWindow with the bitmap's per-pixel alpha.</summary>
    private void Push(Bitmap bmp)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hBitmap);
        try
        {
            var size = new SIZE(bmp.Width, bmp.Height);
            var src = new POINT(0, 0);
            var dst = new POINT(Left, Top);
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT(int x, int y)
    {
        public int X = x, Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE(int cx, int cy)
    {
        public int Cx = cx, Cy = cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
