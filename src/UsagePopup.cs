using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>
/// The panel that appears above the tray. Fully custom-painted: layout and measurement run
/// through one <see cref="Render"/> pass so the window height can never drift from the content.
/// </summary>
public sealed class UsagePopup : Form
{
    const int DipWidth = 300;
    const int DipPad = 16;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    const int DwmwaCornerPreference = 33;
    const int CornerRound = 2;

    readonly Font _title, _label, _value, _small, _link;
    readonly List<(Rectangle Bounds, Action Click)> _hits = new();
    int _hovered = -1;

    UsageResult? _result;
    bool _refreshing;
    string? _notice;
    Screen? _screen;

    public Action? OnRefresh { get; set; }
    public Action? OnOpenDashboard { get; set; }
    public Action? OnQuit { get; set; }

    public UsagePopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Theme.Surface;
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "Claude Code Usage";

        const string family = "Segoe UI";
        _title = new Font(family, 10.5f, FontStyle.Bold);
        _label = new Font(family, 9f, FontStyle.Regular);
        _value = new Font(family, 11f, FontStyle.Bold);
        _small = new Font(family, 8f, FontStyle.Regular);
        _link = new Font(family, 8.5f, FontStyle.Regular);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int pref = CornerRound;
        try { DwmSetWindowAttribute(Handle, DwmwaCornerPreference, ref pref, sizeof(int)); }
        catch (DllNotFoundException) { /* pre-Win11: square corners are fine */ }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    const uint MonitorDefaultToNearest = 2;
    const int MdtEffectiveDpi = 0;

    /// <summary>
    /// DPI of the monitor the panel is about to appear on. The window is created on the
    /// primary monitor, so DeviceDpi would still report the primary's scaling while we lay
    /// out for a differently-scaled display.
    /// </summary>
    int _targetDpi;

    float S => (_targetDpi > 0 ? _targetDpi : DeviceDpi) / 96f;
    int Px(float dip) => (int)Math.Round(dip * S);

    static int DpiForPoint(Point pt)
    {
        try
        {
            var monitor = MonitorFromPoint(pt, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero &&
                GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX > 0)
                return (int)dpiX;
        }
        catch (DllNotFoundException) { /* pre-8.1: single system DPI */ }
        catch (EntryPointNotFoundException) { }
        return 0;
    }

    /// <param name="notice">
    /// Optional status line for a condition the numbers themselves don't show — a rate limit
    /// or network blip that left the displayed snapshot stale.
    /// </param>
    public void ShowUsage(UsageResult? result, bool refreshing, string? notice = null)
    {
        _result = result;
        _refreshing = refreshing;
        _notice = notice;
        Relayout();
        if (Visible) Invalidate();
    }

    public void ShowNearTray()
    {
        var cursor = Cursor.Position;
        _targetDpi = DpiForPoint(cursor);
        _screen = Screen.FromPoint(cursor);

        Relayout();
        Reposition();

        Show();
        Activate();
    }

    /// <summary>Anchors the panel to the tray corner of its working area.</summary>
    void Reposition()
    {
        var area = (_screen ?? Screen.PrimaryScreen).WorkingArea;
        int margin = Px(12);
        Location = new Point(
            Math.Max(area.Left + margin, area.Right - Width - margin),
            Math.Max(area.Top + margin, area.Bottom - Height - margin));
    }

    void Relayout()
    {
        Render(null, out int height);
        var size = new Size(Px(DipWidth), height);
        if (ClientSize != size) ClientSize = size;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Theme.Surface))
            e.Graphics.FillRectangle(bg, ClientRectangle);
        Render(e.Graphics, out _);
    }

    /// <summary>
    /// Lays out the panel. Pass a null <paramref name="g"/> to measure only — both modes walk
    /// the identical code path, so the measured height always matches what gets drawn.
    /// </summary>
    void Render(Graphics? g, out int height)
    {
        if (g is not null) _hits.Clear();

        int pad = Px(DipPad);
        int width = Px(DipWidth);
        int right = width - pad;
        int y = pad;

        // ── Header ────────────────────────────────────────────────────────────
        Draw(g, "Claude Code Usage", _title, Theme.TextPrimary, pad, y);
        var stamp = _refreshing ? "refreshing…"
                  : _result?.Snapshot is { } s ? "updated " + s.FetchedAt.ToString("HH:mm", Inv)
                  : "";
        if (stamp.Length > 0)
            DrawRight(g, stamp, _small, Theme.TextMuted, right, y + Px(4));
        y += Px(30);

        // ── Body ──────────────────────────────────────────────────────────────
        if (_result is { Ok: true, Snapshot: { } snap })
        {
            y = RenderLimit(g, snap.Session, pad, right, y);
            y += Px(14);
            y = RenderLimit(g, snap.Weekly, pad, right, y);

            if (snap.ExtraCreditsUsed is { } credits)
            {
                y += Px(14);
                y = RenderSeparator(g, pad, right, y);
                y += Px(10);
                Draw(g, "Extra usage credits", _label, Theme.TextMuted, pad, y);
                DrawRight(g, FormatMoney(credits, snap.Currency), _label, Theme.TextPrimary, right, y);
                y += Px(18);
            }
        }
        else if (_result is { } err)
        {
            var dot = new Rectangle(pad, y + Px(4), Px(8), Px(8));
            if (g is not null)
                using (var b = new SolidBrush(Theme.Unknown))
                    g.FillEllipse(b, dot);

            Draw(g, err.Message, _label, Theme.TextPrimary, pad + Px(16), y);
            y += Px(20);
            var hint = err.Error switch
            {
                UsageError.AuthExpired => "Claude Code refreshes the token on its next run.",
                UsageError.NoCredentials => "Sign in with the claude CLI, then refresh.",
                UsageError.NotSubscription => "Limits are only reported for subscription logins.",
                UsageError.RateLimited => "Too many requests — backing off before the next one.",
                _ => err.Detail ?? "Will retry automatically.",
            };
            y = DrawWrapped(g, hint, _small, Theme.TextMuted, pad, y, right - pad);
            y += Px(4);
        }
        else
        {
            Draw(g, "Loading…", _label, Theme.TextMuted, pad, y);
            y += Px(20);
        }

        if (_notice is { Length: > 0 } notice)
        {
            y += Px(10);
            y = DrawWrapped(g, notice, _small, Theme.Warning, pad, y, right - pad);
        }

        // ── Footer ────────────────────────────────────────────────────────────
        y += Px(12);
        y = RenderSeparator(g, pad, right, y);
        y += Px(10);

        int footerTop = y;
        int x = pad;
        x = RenderLink(g, "Refresh", x, footerTop, () => OnRefresh?.Invoke());
        x = RenderLink(g, "Dashboard", x + Px(14), footerTop, () => OnOpenDashboard?.Invoke());
        RenderLinkRight(g, "Quit", right, footerTop, () => OnQuit?.Invoke());

        y = footerTop + Px(18) + pad;
        height = y;
    }

    int RenderLimit(Graphics? g, LimitWindow limit, int left, int right, int y)
    {
        var expired = limit.IsExpired;
        var color = expired ? Theme.Unknown : Theme.For(limit.Severity);

        Draw(g, limit.Label, _label, Theme.TextPrimary, left, y + Px(2));
        DrawRight(g, limit.Display, _value, color, right, y);
        y += Px(22);

        // Track + fill, both fully rounded so a 1% sliver still renders as a visible pill.
        int barHeight = Px(7);
        var track = new Rectangle(left, y, right - left, barHeight);
        if (g is not null)
        {
            FillPill(g, track, Theme.Track);
            double pct = expired ? 0 : Math.Clamp(limit.Percent, 0, 100);
            if (pct > 0)
            {
                int filled = Math.Max(barHeight, (int)Math.Round(track.Width * pct / 100.0));
                FillPill(g, new Rectangle(track.X, track.Y, filled, barHeight), color);
            }
        }
        y += barHeight + Px(7);

        Draw(g, ResetText(limit), _small, Theme.TextMuted, left, y);
        return y + Px(15);
    }

    int RenderSeparator(Graphics? g, int left, int right, int y)
    {
        if (g is not null)
            using (var p = new Pen(Theme.SurfaceEdge, 1))
                g.DrawLine(p, left, y, right, y);
        return y + 1;
    }

    int RenderLink(Graphics? g, string text, int x, int y, Action click)
    {
        var size = Measure(text, _link);
        var bounds = new Rectangle(x, y - Px(3), size.Width, size.Height + Px(6));
        int index = _hits.Count;
        if (g is not null)
        {
            _hits.Add((bounds, click));
            if (_hovered == index)
                FillPill(g, Rectangle.Inflate(bounds, Px(6), 0), Theme.Hover);
            Draw(g, text, _link, _hovered == index ? Theme.TextPrimary : Theme.TextMuted, x, y);
        }
        return bounds.Right;
    }

    void RenderLinkRight(Graphics? g, string text, int right, int y, Action click)
    {
        var size = Measure(text, _link);
        RenderLink(g, text, right - size.Width, y, click);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = _hits.FindIndex(h => h.Bounds.Contains(e.Location));
        if (hit != _hovered)
        {
            _hovered = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered != -1) { _hovered = -1; Cursor = Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        foreach (var (bounds, click) in _hits)
            if (bounds.Contains(e.Location)) { click(); return; }
    }

    // Top-level forms get DpiChanged (DpiChangedAfterParent only fires for parented
    // controls), so this is what runs when the panel opens on a differently-scaled monitor.
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _targetDpi = e.DeviceDpiNew;
        Relayout();
        Reposition();
        Invalidate();
    }

    // ── drawing helpers ──────────────────────────────────────────────────────

    // All text goes through GDI (TextRenderer) rather than GDI+ DrawString: at 8pt the
    // typographic StringFormat collapses thin glyphs — a colon in "15:24" disappears — and
    // drawing with the same engine we measure with keeps alignment exact.
    const TextFormatFlags LineFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
                                    | TextFormatFlags.NoPrefix;
    const TextFormatFlags WrapFlags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak
                                    | TextFormatFlags.NoPrefix;

    static void Draw(Graphics? g, string text, Font font, Color color, int x, int y)
    {
        if (g is null) return;
        TextRenderer.DrawText(g, text, font, new Point(x, y), color, LineFlags);
    }

    void DrawRight(Graphics? g, string text, Font font, Color color, int right, int y)
    {
        if (g is null) return;
        Draw(g, text, font, color, right - Measure(text, font).Width, y);
    }

    int DrawWrapped(Graphics? g, string text, Font font, Color color, int x, int y, int width)
    {
        var size = TextRenderer.MeasureText(text, font, new Size(width, 0), WrapFlags);
        if (g is not null)
            TextRenderer.DrawText(g, text, font, new Rectangle(x, y, width, size.Height), color, WrapFlags);
        return y + size.Height;
    }

    static Size Measure(string text, Font font) =>
        TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), LineFlags);

    static void FillPill(Graphics g, Rectangle r, Color color)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        int radius = Math.Min(r.Height, r.Width) / 2;
        using var brush = new SolidBrush(color);
        if (radius <= 1) { g.FillRectangle(brush, r); return; }

        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    // The panel is English, so numbers, times and weekday names stay invariant rather than
    // picking up the OS locale (vi-VN would render "$0,00" and "Th 3").
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static string ResetText(LimitWindow limit)
    {
        if (limit.IsExpired)
            return "window has reset · awaiting new figures";

        if (limit.ResetsAt is not { } resets)
            return "reset time unavailable";

        var when = resets.LocalDateTime.Date == DateTime.Today
            ? resets.ToString("HH:mm", Inv)
            : resets.ToString("ddd HH:mm", Inv);

        return limit.TimeToReset is { } left
            ? $"resets {when} · in {Duration(left)}"
            : $"resets {when}";
    }

    static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h"
      : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
      : $"{Math.Max(1, (int)t.TotalMinutes)}m";

    static string FormatMoney(decimal amount, string currency) =>
        currency == "USD"
            ? "$" + amount.ToString("0.00", Inv)
            : amount.ToString("0.00", Inv) + " " + currency;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _title.Dispose(); _label.Dispose(); _value.Dispose();
            _small.Dispose(); _link.Dispose();
        }
        base.Dispose(disposing);
    }
}
