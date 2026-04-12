namespace DevBrain.Functions.Auth.Services;

/// <summary>
/// Thrown by <see cref="EntraOAuthClient"/> when an Entra <c>id_token</c> fails signature, issuer,
/// audience, or lifetime validation.
///
/// <para>
/// Semantically distinct from <see cref="UpstreamOAuthException"/>: that one signals an upstream
/// transport or grant failure and is forwarded to the client as a redirect with <c>server_error</c>.
/// This one signals a <i>security-relevant</i> failure — a token that successfully reached DevBrain
/// but couldn't be validated. The callback endpoint translates it into a local 400 with
/// <c>invalid_grant</c> rather than redirecting, so the failure isn't papered over in the client's
/// error-handling UI.
/// </para>
/// </summary>
public sealed class IdTokenValidationException : Exception
{
    public IdTokenValidationException(string message) : base(message) { }

    public IdTokenValidationException(string message, Exception innerException) : base(message, innerException) { }
}
