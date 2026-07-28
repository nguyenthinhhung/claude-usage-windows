using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ClaudeUsageTray;

public enum Severity { Normal, Warning, Critical }

/// <summary>One rate-limit window as reported by the API.</summary>
public sealed record LimitWindow(string Label, double Percent, DateTimeOffset? ResetsAt)
{
    public Severity Severity => Percent >= 90 ? Severity.Critical
                              : Percent >= 70 ? Severity.Warning
                              : Severity.Normal;

    public TimeSpan? TimeToReset =>
        ResetsAt is { } r && r > DateTimeOffset.Now ? r - DateTimeOffset.Now : null;
}

public sealed record UsageSnapshot(
    LimitWindow Session,
    LimitWindow Weekly,
    decimal? ExtraCreditsUsed,
    string Currency,
    DateTimeOffset FetchedAt)
{
    /// <summary>The limit you will actually hit first — what the tray icon reflects.</summary>
    public double BindingPercent => Math.Max(Session.Percent, Weekly.Percent);

    public Severity Severity => Session.Severity > Weekly.Severity ? Session.Severity : Weekly.Severity;
}

public enum UsageError { None, NoCredentials, NotSubscription, AuthExpired, Network, BadResponse }

public sealed record UsageResult(UsageSnapshot? Snapshot, UsageError Error, string? Detail)
{
    public bool Ok => Error == UsageError.None && Snapshot is not null;

    public static UsageResult Success(UsageSnapshot s) => new(s, UsageError.None, null);
    public static UsageResult Fail(UsageError e, string? detail = null) => new(null, e, detail);

    public string Message => Error switch
    {
        UsageError.NoCredentials  => "Claude Code credentials not found",
        UsageError.NotSubscription => "No Claude subscription on this login",
        UsageError.AuthExpired    => "Login expired — run any claude command",
        UsageError.Network        => "Can't reach the usage API",
        UsageError.BadResponse    => "Unexpected response from the usage API",
        _                         => "Unknown error",
    };
}

public sealed class UsageService
{
    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Resolves ~/.claude (or CLAUDE_CONFIG_DIR) fresh on every call so we transparently
    /// pick up the access token Claude Code rotates in the background. We deliberately do
    /// not perform the OAuth refresh ourselves: the refresh token is single-use, and
    /// consuming it here would sign the CLI out.
    /// </summary>
    static string CredentialsPath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(dir, ".credentials.json");
        }
    }

    public async Task<UsageResult> FetchAsync(CancellationToken ct = default)
    {
        string token;
        try
        {
            var path = CredentialsPath;
            if (!File.Exists(path))
                return UsageResult.Fail(UsageError.NoCredentials, path);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                !oauth.TryGetProperty("accessToken", out var at) ||
                at.GetString() is not { Length: > 0 } t)
                return UsageResult.Fail(UsageError.NotSubscription);

            token = t;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return UsageResult.Fail(UsageError.NoCredentials, ex.Message);
        }

        HttpResponseMessage response;
        string body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.Add("Authorization", "Bearer " + token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            response = await Http.SendAsync(req, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return UsageResult.Fail(UsageError.Network, "timed out");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return UsageResult.Fail(UsageError.Network, ex.Message);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return UsageResult.Fail(UsageError.AuthExpired);
            if (!response.IsSuccessStatusCode)
                return UsageResult.Fail(UsageError.BadResponse, $"HTTP {(int)response.StatusCode}");
        }

        try
        {
            return UsageResult.Success(Parse(body));
        }
        catch (Exception ex)
        {
            return UsageResult.Fail(UsageError.BadResponse, ex.Message);
        }
    }

    static UsageSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        LimitWindow? session = null, weekly = null;

        // Preferred shape: a flat `limits` array grouped into session / weekly buckets.
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in limits.EnumerateArray())
            {
                var group = Str(l, "group");
                var kind = Str(l, "kind") ?? group;
                var pct = Num(l, "percent") ?? 0;
                var resets = Time(l, "resets_at");

                if (group == "session")
                {
                    session = new LimitWindow("Session · 5h", pct, resets);
                }
                else if (group == "weekly")
                {
                    // A plan can carry several weekly caps (all-models, Opus-only).
                    // The highest one is the constraint worth showing.
                    if (weekly is null || pct > weekly.Percent)
                        weekly = new LimitWindow(WeeklyLabel(kind), pct, resets);
                }
            }
        }

        // Older shape, still returned alongside `limits`.
        session ??= Window(root, "five_hour", "Session · 5h");
        weekly ??= Window(root, "seven_day", "Weekly");

        decimal? credits = null;
        var currency = "USD";
        if (root.TryGetProperty("spend", out var spend) && spend.ValueKind == JsonValueKind.Object)
        {
            if (spend.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True &&
                spend.TryGetProperty("used", out var used) && used.ValueKind == JsonValueKind.Object)
            {
                var minor = Num(used, "amount_minor") ?? 0;
                var exponent = (int)(Num(used, "exponent") ?? 2);
                credits = (decimal)minor / (decimal)Math.Pow(10, exponent);
                currency = Str(used, "currency") ?? currency;
            }
        }

        return new UsageSnapshot(
            session ?? new LimitWindow("Session · 5h", 0, null),
            weekly ?? new LimitWindow("Weekly", 0, null),
            credits,
            currency,
            DateTimeOffset.Now);
    }

    static string WeeklyLabel(string? kind) => kind switch
    {
        null or "weekly_all" => "Weekly",
        "weekly_opus"        => "Weekly · Opus",
        "weekly_sonnet"      => "Weekly · Sonnet",
        _                    => "Weekly · " + kind.Replace("weekly_", "").Replace('_', ' '),
    };

    static LimitWindow? Window(JsonElement root, string prop, string label)
    {
        if (!root.TryGetProperty(prop, out var e) || e.ValueKind != JsonValueKind.Object)
            return null;
        return new LimitWindow(label, Num(e, "utilization") ?? 0, Time(e, "resets_at"));
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static DateTimeOffset? Time(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, out var t) ? t.ToLocalTime() : null;
}
