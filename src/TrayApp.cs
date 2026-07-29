using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace ClaudeUsageTray;

public sealed class TrayApp : ApplicationContext
{
    const string DashboardUrl = "https://claude.ai/settings/usage";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "ClaudeUsageTray";

    // The windows we report on are 5-hourly and weekly, so polling harder than this buys no
    // real freshness — it only spends quota against the usage endpoint's own rate limit.
    static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    // Failures back off exponentially from this floor. Retrying *faster* after an error is what
    // turns a single 429 into a permanent one: the retries keep the limit from ever clearing.
    static readonly TimeSpan RetryFloor = TimeSpan.FromSeconds(30);
    static readonly TimeSpan RateLimitFloor = TimeSpan.FromMinutes(2);
    static readonly TimeSpan RetryCeiling = TimeSpan.FromMinutes(30);

    /// <summary>Shortest gap between two user-triggered fetches, so clicking can't spam the API.</summary>
    static readonly TimeSpan ManualCooldown = TimeSpan.FromSeconds(15);

    readonly UsageService _service = new();
    readonly NotifyIcon _tray;
    readonly UsagePopup _popup;
    readonly System.Windows.Forms.Timer _timer;
    readonly ToolStripMenuItem _autostartItem;
    readonly CancellationTokenSource _cts = new();

    Icon? _currentIcon;
    UsageResult? _last;
    UsageSnapshot? _lastGood;
    string? _notice;
    bool _fetching;
    bool _paused, _locked, _suspended;
    int _failures;
    DateTime _lastAttemptAt = DateTime.MinValue;
    DateTime _blockedUntil = DateTime.MinValue;
    DateTime _popupHiddenAt = DateTime.MinValue;

    public TrayApp()
    {
        _popup = new UsagePopup
        {
            OnRefresh = () => _ = RefreshAsync(manual: true),
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
        menu.Items.Add("Refresh now", null, (_, _) => _ = RefreshAsync(manual: true));
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

        // SystemEvents raises its callbacks on a thread of its own, so we need a live window
        // handle to marshal them back onto the UI thread. Touching Handle creates one without
        // showing the panel; nothing else in startup would have created it yet.
        _ = _popup.Handle;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _ = RefreshAsync();
    }

    // ── idle detection ───────────────────────────────────────────────────────
    //
    // Nobody is spending Claude quota while the workstation is locked or asleep, so polling
    // through it only burns requests against the usage endpoint's own rate limit. Suspending
    // on those two signals covers nights, weekends and holidays without a configured schedule
    // — and, unlike fixed office hours, it stays correct for anyone working at odd times.

    void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect)
            RunOnUi(() => { _locked = true; ApplyPause(); });
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect)
            RunOnUi(() => { _locked = false; ApplyPause(); });
    }

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) RunOnUi(() => { _suspended = true; ApplyPause(); });
        else if (e.Mode == PowerModes.Resume) RunOnUi(() => { _suspended = false; ApplyPause(); });
    }

    /// <summary>
    /// Lock and sleep are tracked separately because they nest: Windows wakes the machine for
    /// scheduled maintenance and fires Resume while the session is still locked. Treating that
    /// as "the user is back" would leave us polling all night at the lock screen.
    /// </summary>
    void ApplyPause()
    {
        var paused = _locked || _suspended;
        if (_paused == paused) return;
        _paused = paused;

        if (paused)
        {
            _timer.Stop();
            return;
        }

        _timer.Start();

        // However long the pause lasted, the figures on screen are that stale — and the window
        // they describe may well have rolled over. Fetch straight away rather than waiting out
        // the poll interval. Going through the manual path keeps the 429 cool-off in force, so
        // a lock/unlock cycle can't be used to sidestep a rate limit.
        _ = RefreshAsync(manual: true);
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread, dropping it if we're shutting down.</summary>
    void RunOnUi(Action action)
    {
        if (_popup.IsDisposed || !_popup.IsHandleCreated) return;
        try
        {
            if (_popup.InvokeRequired) _popup.BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException) { /* raced with shutdown */ }
        catch (InvalidOperationException) { /* handle went away mid-post */ }
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

        _popup.ShowUsage(_last, _fetching, _notice);
        _popup.ShowNearTray();
        if (_last is null || !_last.Ok) _ = RefreshAsync(manual: true);
    }

    /// <param name="manual">
    /// True for refreshes the user asked for. Those bypass the poll schedule but still honour
    /// a short cooldown, and a server-imposed 429 cool-off, so no amount of clicking can
    /// out-pace the API's rate limit.
    /// </param>
    async Task RefreshAsync(bool manual = false)
    {
        if (_fetching) return;

        var now = DateTime.UtcNow;
        if (manual && (now < _blockedUntil || now - _lastAttemptAt < ManualCooldown))
        {
            if (_popup.Visible) _popup.ShowUsage(_last, refreshing: false, _notice);
            return;
        }

        _fetching = true;
        _lastAttemptAt = now;
        if (_popup.Visible) _popup.ShowUsage(_last, refreshing: true, _notice);

        try
        {
            var result = await _service.FetchAsync(_cts.Token);
            var next = Schedule(result);

            if (result.Ok)
            {
                _lastGood = result.Snapshot;
                _last = result;
                _notice = null;
            }
            else if (_lastGood is not null && result.IsTransient)
            {
                // A transient failure is no reason to throw away good numbers — keep showing
                // them (the header timestamp already marks them as stale) and note the retry.
                _last = UsageResult.Success(_lastGood);
                _notice = $"{result.Message} · retrying in {Approx(next)}";
            }
            else
            {
                _last = result;
                _notice = null;
            }

            UpdateIcon(_last);
            if (_popup.Visible) _popup.ShowUsage(_last, refreshing: false, _notice);

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

    /// <summary>
    /// Decides when to poll next and, for rate limits, until when every request is off-limits.
    /// </summary>
    TimeSpan Schedule(UsageResult result)
    {
        if (result.Ok)
        {
            _failures = 0;
            _blockedUntil = DateTime.MinValue;
            return PollInterval;
        }

        _failures++;
        var rateLimited = result.Error == UsageError.RateLimited;
        var floor = rateLimited ? RateLimitFloor : RetryFloor;

        // Doubling is capped at 2^6 before the ceiling clamp so a long outage can't overflow.
        var backoff = TimeSpan.FromSeconds(floor.TotalSeconds * Math.Pow(2, Math.Min(_failures - 1, 6)));
        var next = result.RetryAfter is { } asked && asked > floor ? asked : backoff;
        if (next > RetryCeiling) next = RetryCeiling;

        // Only a 429 hard-blocks manual refreshes: for auth or network errors the user may well
        // have just fixed the cause and should be able to retry on demand.
        _blockedUntil = rateLimited ? DateTime.UtcNow + next : DateTime.MinValue;
        return next;
    }

    static string Approx(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{Math.Round(t.TotalMinutes)}m" : $"{Math.Max(1, (int)t.TotalSeconds)}s";

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
                    "Claude Code Usage\nSession {0} · {1} {2}{3}",
                    snapshot.Session.Display, snapshot.Weekly.Label, snapshot.Weekly.Display,
                    _notice is null ? "" : "\n" + _notice)
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
            // SystemEvents holds its handlers in a static list; leaving them attached would
            // keep this object — and its icon — alive past shutdown.
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;

            _timer.Dispose();
            _tray.Dispose();
            _popup.Dispose();
            _currentIcon?.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
