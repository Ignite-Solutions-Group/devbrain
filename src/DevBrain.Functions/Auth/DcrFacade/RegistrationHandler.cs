using System.Text.Json.Serialization;
using DevBrain.Functions.Auth.Models;
using DevBrain.Functions.Auth.Services;
using Microsoft.Extensions.Logging;

namespace DevBrain.Functions.Auth.DcrFacade;

/// <summary>
/// Service layer for <c>POST /register</c>. Held separate from the HTTP adapter so it can be
/// unit-tested without standing up the Functions runtime — the endpoint class is a thin wrapper
/// that parses the JSON body, calls <see cref="HandleAsync"/>, and formats the response.
/// </summary>
public sealed class RegistrationHandler
{
    // RFC 7591 §3.2.1: client_id values SHOULD remain valid for 90 days without re-registration
    // unless the client explicitly re-registers. We honour that with a 90-day TTL on the record.
    private static readonly TimeSpan ClientTtl = TimeSpan.FromDays(90);

    private readonly IOAuthStateStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RegistrationHandler>? _logger;

    public RegistrationHandler(IOAuthStateStore store, TimeProvider timeProvider)
        : this(store, timeProvider, logger: null)
    {
    }

    public RegistrationHandler(IOAuthStateStore store, TimeProvider timeProvider, ILogger<RegistrationHandler>? logger)
    {
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<RegistrationResult> HandleAsync(RegistrationRequest request)
    {
        _logger?.LogInformation(
            "RegistrationHandler: request received clientName={ClientName} redirectUriCount={RedirectUriCount}",
            request.ClientName, request.RedirectUris?.Length ?? 0);

        // RFC 7591 §2: redirect_uris is REQUIRED and MUST contain at least one value.
        if (request.RedirectUris is null || request.RedirectUris.Length == 0)
        {
            _logger?.LogWarning("RegistrationHandler: rejected — redirect_uris missing or empty");
            return RegistrationResult.Error("invalid_redirect_uri", "redirect_uris is required and must contain at least one entry.");
        }

        foreach (var uri in request.RedirectUris)
        {
            if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                _logger?.LogWarning("RegistrationHandler: rejected — redirect_uri {Uri} is not a valid absolute URI", uri);
                return RegistrationResult.Error("invalid_redirect_uri", $"redirect_uri '{uri}' is not a valid absolute URI.");
            }

            // Only http/https are acceptable. We don't accept custom URI schemes, mailto:, etc.
            if (parsed.Scheme is not ("http" or "https"))
            {
                _logger?.LogWarning("RegistrationHandler: rejected — redirect_uri scheme {Scheme} not allowed", parsed.Scheme);
                return RegistrationResult.Error("invalid_redirect_uri", $"redirect_uri scheme '{parsed.Scheme}' is not allowed. Use http or https.");
            }
        }

        // Opaque GUID as client_id. FastMCP-style: this is a handle onto the client's declared
        // redirect URIs, not an Entra app. Every issued client_id maps internally to the same
        // upstream DevBrain Entra app (see sprint §"DCR client_id strategy").
        var clientId = Guid.NewGuid().ToString("N");
        var now = _timeProvider.GetUtcNow();

        var record = new RegisteredClient
        {
            ClientId = clientId,
            ClientName = request.ClientName,
            RedirectUris = request.RedirectUris,
            CreatedAt = now,
            ExpiresAt = now + ClientTtl,
            Ttl = (int)ClientTtl.TotalSeconds,
        };
        await _store.SaveClientAsync(record);

        _logger?.LogInformation(
            "RegistrationHandler: client registered clientId={ClientId} clientName={ClientName}",
            clientId, request.ClientName);

        return RegistrationResult.Success(new RegistrationResponse(
            ClientId: clientId,
            ClientIdIssuedAt: now.ToUnixTimeSeconds(),
            ClientName: request.ClientName,
            RedirectUris: request.RedirectUris,
            // Public clients only — DevBrain doesn't issue client secrets because the client_id
            // is a handle, not an Entra app, and there's no upstream secret to protect.
            TokenEndpointAuthMethod: "none"));
    }
}

/// <summary>
/// Incoming body shape for <c>POST /register</c>. Matches RFC 7591 §2 (subset DevBrain honors).
///
/// <para>
/// <b>Explicit <see cref="JsonPropertyNameAttribute"/> on every field is load-bearing.</b> RFC 7591
/// defines these fields as snake_case (<c>redirect_uris</c>, <c>client_name</c>), and every
/// spec-compliant DCR client sends them that way. The endpoint's <c>JsonOptions</c> inherits
/// <see cref="JsonSerializerDefaults.Web"/>, which applies <see cref="JsonNamingPolicy.CamelCase"/>
/// and would otherwise look for <c>redirectUris</c>/<c>clientName</c> — no match, no exception,
/// silently null fields, and the handler rejecting "redirect_uris missing or empty" for every
/// real client. The response-side <see cref="RegisterEndpoint"/>.<c>RegistrationResponseDto</c>
/// has always had these annotations; the request side was missed until Claude Desktop hit it
/// in the v1.6 post-deploy window.
/// </para>
/// </summary>
public sealed record RegistrationRequest(
    [property: JsonPropertyName("redirect_uris")] string[]? RedirectUris,
    [property: JsonPropertyName("client_name")] string? ClientName);

/// <summary>Result envelope — either a success (to be serialized as RFC 7591 §3.2.1 response) or an error (RFC 7591 §3.2.2).</summary>
public sealed record RegistrationResult(bool IsSuccess, RegistrationResponse? Response, string? ErrorCode, string? ErrorDescription)
{
    public static RegistrationResult Success(RegistrationResponse response) => new(true, response, null, null);
    public static RegistrationResult Error(string code, string description) => new(false, null, code, description);
}

public sealed record RegistrationResponse(
    string ClientId,
    long ClientIdIssuedAt,
    string? ClientName,
    string[] RedirectUris,
    string TokenEndpointAuthMethod);
