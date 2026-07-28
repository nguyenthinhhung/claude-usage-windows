using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace ClaudeUsageTray;

public sealed class TrayApp : ApplicationContext
{
    const string DashboardUrl = "https://claude.ai/settings/usage";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "ClaudeUsageTray";

    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    static readonly TimeSpan ErrorRetryInterval = TimeSpan.FromSeconds(20);

    readonly UsageService _service = new();
    readonly NotifyIcon _tray;
    readonly UsagePopup _popup;
    readonly System.Windows.Forms.Timer _timer;
    readonly ToolStripMenuItem _autostartItem;
    readonly CancellationTokenSource _cts = new();

    Icon? _currentIcon;
    UsageResult? _last;
    bool _fetching;
    DateTime _popupHiddenAt = DateTime.MinValue;

    public TrayApp()
    {
        _popup = new UsagePopup
        {
            OnRefresh = () => _ = RefreshAsync(),
            OnOpenDashboard = OpenDashboard,
            OnQuit = Quit,
        };
        _popup.VisibleChanged += (_, _) =>
        {
            if (!_popup.Visible) _popupHiddenAt = DateTime.UtcNow;
        };

        _autostartItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsAutostartEnabled(),
        };
        _autostartItem.Click += (_, _) => SetAutostart(_autostartItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, (_, _) => _ = RefreshAsync());
        menu.Items.Add("Open usage dashboard", null, (_, _) => OpenDashboard());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _tray = new NotifyIcon
        {
            Text = "Claude Code Usage — loading…",
            ContextMenuStrip = menu,
            Visible = true,
        };
        UpdateIcon(null);
        _tray.MouseClick += OnTrayClick;

        _timer = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        // Clicking the icon while the panel is open deactivates (and hides) it first;
        // without this guard the click would immediately reopen it.
        if (_popup.Visible || (DateTime.UtcNow - _popupHiddenAt) < TimeSpan.FromMilliseconds(250))
        {
            _popup.Hide();
            return;
        }

        _popup.ShowUsage(_last, _fetching);
        _popup.ShowNearTray();
        if (_last is null || !_last.Ok) _ = RefreshAsync();
    }

    async Task RefreshAsync()
    {
        if (_fetching) return;
        _fetching = true;
        if (_popup.Visible) _popup.ShowUsage(_last, refreshing: true);

        try
        {
            var result = await _service.FetchAsync(_cts.Token);
            _last = result;

            UpdateIcon(result);
            if (_popup.Visible) _popup.ShowUsage(result, refreshing: false);

            // Back off less on failure so a transient network blip clears quickly.
            var next = result.Ok ? PollInterval : ErrorRetryInterval;
            _timer.Interval = (int)next.TotalMilliseconds;
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            _fetching = false;
        }
    }

    void UpdateIcon(UsageResult? result)
    {
        var snapshot = result?.Snapshot;
        var icon = IconRenderer.Render(SystemInformation.SmallIconSize.Width, snapshot);

        _tray.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;

        _tray.Text = Truncate(
            snapshot is not null
                ? string.Format(CultureInfo.InvariantCulture,
                    "Claude Code Usage\nSession {0:0.#}% · {1} {2:0.#}%",
                    snapshot.Session.Percent, snapshot.Weekly.Label, snapshot.Weekly.Percent)
                : $"Claude Code Usage\n{result?.Message ?? "loading…"}",
            127);
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    static void OpenDashboard()
    {
        try { Process.Start(new ProcessStartInfo(DashboardUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is not null;
        }
        catch { return false; }
    }

    static void SetAutostart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                if (Environment.ProcessPath is { } exe)
                    key.SetValue(RunValue, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't update the startup setting.\n\n{ex.Message}",
                "Claude Code Usage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void Quit()
    {
        _cts.Cancel();
        _timer.Stop();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tray.Dispose();
            _popup.Dispose();
            _currentIcon?.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
