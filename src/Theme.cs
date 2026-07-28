namespace ClaudeUsageTray;

/// <summary>Single source of truth for colours so the tray icon and the popup agree.</summary>
public static class Theme
{
    public static readonly Color Ok       = Color.FromArgb(0x34, 0xD3, 0x99); // emerald
    public static readonly Color Warning  = Color.FromArgb(0xFB, 0xBF, 0x24); // amber
    public static readonly Color Critical = Color.FromArgb(0xF8, 0x71, 0x71); // red
    public static readonly Color Unknown  = Color.FromArgb(0x9C, 0xA3, 0xAF); // gray

    public static readonly Color Surface     = Color.FromArgb(0x1C, 0x1C, 0x1E);
    public static readonly Color SurfaceEdge = Color.FromArgb(0x3A, 0x3A, 0x3D);
    public static readonly Color Track       = Color.FromArgb(0x3A, 0x3A, 0x3D);
    public static readonly Color TextPrimary = Color.FromArgb(0xF5, 0xF5, 0xF7);
    public static readonly Color TextMuted   = Color.FromArgb(0x8E, 0x8E, 0x93);
    public static readonly Color Hover       = Color.FromArgb(0x2C, 0x2C, 0x2E);

    public static Color For(Severity s) => s switch
    {
        Severity.Critical => Critical,
        Severity.Warning  => Warning,
        _                 => Ok,
    };

    /// <summary>Muted version of a status colour, for the unfilled part of a track.</summary>
    public static Color Dim(Color c, int alpha) => Color.FromArgb(alpha, c);
}
