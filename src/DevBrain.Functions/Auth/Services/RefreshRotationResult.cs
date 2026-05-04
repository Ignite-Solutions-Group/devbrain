namespace DevBrain.Functions.Auth.Services;

/// <summary>
/// Result of rotating a DevBrain refresh token. Immediate replays of the old token return the
/// same replacement refresh token so client retries remain idempotent.
/// </summary>
public sealed record RefreshRotationResult(
    string UpstreamJti,
    string RefreshToken,
    bool IsReplay);
