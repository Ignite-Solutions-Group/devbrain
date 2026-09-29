namespace DevBrain.Core.Auth.Services;

/// <summary>
/// Outcome of rotating a DevBrain refresh token.
/// </summary>
public enum RefreshRotationOutcome
{
    Rotated,
    Replayed,
    Missing,
    Expired,
    ReplayWindowExpired,
    WrongClient,
    WrongResource,
    ReplayMarkerMissingReplacement,
    UpstreamMissingOrExpired,
    ConcurrentReplayUnavailable,
}

/// <summary>
/// Result of rotating a DevBrain refresh token. Immediate replays of the old token return the
/// same replacement refresh token so client retries remain idempotent. Rejections carry a
/// reason code for server-side diagnostics; callers still return a generic OAuth
/// <c>invalid_grant</c> to clients.
/// </summary>
public sealed record RefreshRotationResult(
    string? UpstreamJti,
    string? RefreshToken,
    RefreshRotationOutcome Outcome)
{
    public bool Succeeded => Outcome is RefreshRotationOutcome.Rotated or RefreshRotationOutcome.Replayed;

    public bool IsReplay => Outcome is RefreshRotationOutcome.Replayed;

    public string LogCode => Outcome switch
    {
        RefreshRotationOutcome.Rotated => "rotated",
        RefreshRotationOutcome.Replayed => "replayed",
        RefreshRotationOutcome.Missing => "missing",
        RefreshRotationOutcome.Expired => "expired",
        RefreshRotationOutcome.ReplayWindowExpired => "replay_window_expired",
        RefreshRotationOutcome.WrongClient => "wrong_client",
        RefreshRotationOutcome.WrongResource => "wrong_resource",
        RefreshRotationOutcome.ReplayMarkerMissingReplacement => "replay_marker_missing_replacement",
        RefreshRotationOutcome.UpstreamMissingOrExpired => "upstream_missing_or_expired",
        RefreshRotationOutcome.ConcurrentReplayUnavailable => "concurrent_replay_unavailable",
        _ => "unknown",
    };

    public static RefreshRotationResult Rotated(string upstreamJti, string refreshToken) =>
        new(upstreamJti, refreshToken, RefreshRotationOutcome.Rotated);

    public static RefreshRotationResult Replayed(string upstreamJti, string refreshToken) =>
        new(upstreamJti, refreshToken, RefreshRotationOutcome.Replayed);

    public static RefreshRotationResult Rejected(RefreshRotationOutcome outcome) =>
        new(null, null, outcome);
}
