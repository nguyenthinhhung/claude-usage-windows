# Claude Code Usage — Windows tray app

A system-tray gauge for your Claude Code rate limits. The tray glyph is two concentric
arcs — **outer = weekly**, **inner = current 5-hour session** — so you can see at a glance
how much headroom is left. Click it for the detail panel.

```
┌─ Claude Code Usage ──── updated 15:34 ─┐
│ Session · 5h                       15% │
│ ▓▓▓▓░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  │
│ resets 20:09 · in 4h 35m               │
│                                        │
│ Weekly                             32% │
│ ▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  │
│ resets 16:59 · in 1h 25m               │
├────────────────────────────────────────┤
│ Extra usage credits              $0.00 │
├────────────────────────────────────────┤
│ Refresh   Dashboard               Quit │
└────────────────────────────────────────┘
```

Colours: green under 70%, amber at 70%+, red at 90%+.

## Build & run

Requires the .NET 6 SDK with the Windows Desktop runtime (already present if
`dotnet --list-runtimes` lists `Microsoft.WindowsDesktop.App`).

```powershell
dotnet build -c Release
.\bin\Release\net6.0-windows\ClaudeUsageTray.exe
```

For a single self-contained file you can copy anywhere:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o publish
```

> **Windows 11 hides new tray icons by default.** On first run, click the `^` chevron in the
> notification area and drag the icon out onto the taskbar to keep it visible.

Right-click the icon for **Refresh now**, **Open usage dashboard**, **Start with Windows**
(writes a single `HKCU\...\Run` value), and **Quit**. Only one instance runs at a time.

## Where the numbers come from

The app reads the OAuth access token that Claude Code already stores in
`%USERPROFILE%\.claude\.credentials.json` (or `$env:CLAUDE_CONFIG_DIR`) and calls the same
endpoint the `/usage` slash command uses:

```
GET https://api.anthropic.com/api/oauth/usage
```

It polls every 60 seconds, and every 20 seconds while the last call failed.

**It never refreshes the token itself.** The refresh token is single-use, so consuming it
here would sign the CLI out. Instead the credentials file is re-read on every poll, which
picks up whatever Claude Code rotated in the background. If the token does expire while the
CLI is idle, the panel says so and recovers on its own after the next `claude` run.

Nothing is sent anywhere except that one authenticated GET to Anthropic, and nothing is
written to disk apart from the optional autostart registry value.

## Layout

| File | Purpose |
| --- | --- |
| `src/Program.cs` | Entry point, single-instance mutex, DPI mode |
| `src/TrayApp.cs` | Tray icon, polling timer, context menu, autostart |
| `src/UsageService.cs` | Credential read, HTTP call, response parsing |
| `src/UsagePopup.cs` | The custom-painted detail panel |
| `src/IconRenderer.cs` | The two-arc tray glyph |
| `src/Theme.cs` | Shared colours |

Two implementation notes worth keeping in mind if you edit the drawing code:

- All text goes through `TextRenderer` (GDI), not `Graphics.DrawString` (GDI+). At 8pt the
  typographic `StringFormat` collapses thin glyphs — the colon in `15:24` vanishes — and
  drawing with the same engine used for measuring keeps right-alignment exact.
- The panel sizes itself from `GetDpiForMonitor` for the monitor it is about to appear on,
  not from `DeviceDpi`. The window is created on the primary monitor, so `DeviceDpi` would
  report the wrong scaling for a secondary display with different DPI.

## Limitations

- Percentages are what the API reports; plans without a given cap (e.g. no Opus-specific
  weekly limit) simply omit it, and the panel shows the highest weekly cap that applies.
- Requires a subscription login. API-key-only setups have no rate-limit windows to report.
