using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DevBrain.Functions.Auth.DcrFacade;

namespace DevBrain.Functions.Tests.Auth.DcrFacade;

/// <summary>
/// Wire-format regression guards for every OAuth/DCR response DTO. Each test serializes the
/// production DTO through a <see cref="JsonSerializerOptions"/> instance that exactly matches
/// what the corresponding endpoint uses, parses the result as a raw <see cref="JsonNode"/>, and
/// asserts each field name matches the governing RFC.
///
/// <para>
/// <b>Why this file exists:</b> the v1.6 post-deploy investigation produced a hypothesis that
/// <c>JsonSerializerDefaults.Web</c>'s camelCase naming policy was silently converting these DTOs'
/// field names (<c>accessToken</c> instead of <c>access_token</c>, etc.), breaking clients that
/// follow spec. The audit showed every DTO already had explicit <see cref="JsonPropertyNameAttribute"/>
/// attributes, and System.Text.Json's attribute-wins-over-policy rule means the fields were
/// emitting correctly. These tests pin that behavior so a future refactor that accidentally
/// strips an attribute fails here loudly instead of breaking real clients silently.
/// </para>
///
/// <para>
/// <b>What these tests do NOT cover:</b> they don't verify that we emit every field the RFC
/// <i>could</i> want (e.g., RFC 7591 §3.2.1 says registration responses SHOULD echo all request
/// metadata — we only echo a subset). They pin the fields we DO emit, and guard those field
/// names against drift.
/// </para>
/// </summary>
public sealed class OAuthWireFormatTests
{
    // ---------------- RFC 6749 §5.1 — Token Response ----------------

    [Fact]
    public void TokenResponseDto_EmitsRfc6749SnakeCaseFieldNames()
    {
        // Match the options TokenEndpoint uses exactly (line 19): JsonSerializerDefaults.Web
        // with no additional customization.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var dto = new TokenEndpoint.TokenResponseDto(
            AccessToken: "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.test",
            TokenType: "Bearer",
            ExpiresIn: 600,
            RefreshToken: "opaque-refresh-token-value",
            Scope: "documents.readwrite");

        var json = JsonSerializer.Serialize(dto, options);
        var node = JsonNode.Parse(json)!.AsObject();

        // RFC 6749 §5.1 — required fields for a successful response.
        Assert.True(node.ContainsKey("access_token"), $"missing access_token in {json}");
        Assert.True(node.ContainsKey("token_type"), $"missing token_type in {json}");
        Assert.True(node.ContainsKey("expires_in"), $"missing expires_in in {json}");
        Assert.True(node.ContainsKey("refresh_token"), $"missing refresh_token in {json}");
        Assert.True(node.ContainsKey("scope"), $"missing scope in {json}");

        // Negative guard: camelCase variants must NOT be present. If the attribute ever got
        // stripped, the CamelCase naming policy from JsonSerializerDefaults.Web would produce
        // these instead — explicitly assert they're absent so the test fails in the mode the
        // original hypothesis feared.
        Assert.False(node.ContainsKey("accessToken"), $"camelCase accessToken leaked in {json}");
        Assert.False(node.ContainsKey("tokenType"), $"camelCase tokenType leaked in {json}");
        Assert.False(node.ContainsKey("expiresIn"), $"camelCase expiresIn leaked in {json}");
        Assert.False(node.ContainsKey("refreshToken"), $"camelCase refreshToken leaked in {json}");

        // Values round-trip correctly.
        Assert.Equal("eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.test", (string)node["access_token"]!);
        Assert.Equal("Bearer", (string)node["token_type"]!);
        Assert.Equal(600, (int)node["expires_in"]!);
        Assert.Equal("opaque-refresh-token-value", (string)node["refresh_token"]!);
        Assert.Equal("documents.readwrite", (string)node["scope"]!);
    }

    // ---------------- RFC 7591 §3.2.1 — Dynamic Client Registration Response ----------------

    [Fact]
    public void RegistrationResponseDto_EmitsRfc7591SnakeCaseFieldNames()
    {
        // Match RegisterEndpoint options: Web defaults + DefaultIgnoreCondition.WhenWritingNull
        // (line 21-24).
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var dto = new RegisterEndpoint.RegistrationResponseDto(
            ClientId: "abc123def456",
            ClientIdIssuedAt: 1_700_000_000L,
            ClientName: "Claude Desktop",
            RedirectUris: ["https://claude.ai/api/mcp/auth_callback"],
            TokenEndpointAuthMethod: "none");

        var json = JsonSerializer.Serialize(dto, options);
        var node = JsonNode.Parse(json)!.AsObject();

        Assert.True(node.ContainsKey("client_id"), $"missing client_id in {json}");
        Assert.True(node.ContainsKey("client_id_issued_at"), $"missing client_id_issued_at in {json}");
        Assert.True(node.ContainsKey("client_name"), $"missing client_name in {json}");
        Assert.True(node.ContainsKey("redirect_uris"), $"missing redirect_uris in {json}");
        Assert.True(node.ContainsKey("token_endpoint_auth_method"), $"missing token_endpoint_auth_method in {json}");

        // camelCase leak guards
        Assert.False(node.ContainsKey("clientId"), $"camelCase clientId leaked in {json}");
        Assert.False(node.ContainsKey("clientIdIssuedAt"), $"camelCase clientIdIssuedAt leaked in {json}");
        Assert.False(node.ContainsKey("clientName"), $"camelCase clientName leaked in {json}");
        Assert.False(node.ContainsKey("redirectUris"), $"camelCase redirectUris leaked in {json}");
        Assert.False(node.ContainsKey("tokenEndpointAuthMethod"), $"camelCase tokenEndpointAuthMethod leaked in {json}");

        // Values
        Assert.Equal("abc123def456", (string)node["client_id"]!);
        Assert.Equal(1_700_000_000L, (long)node["client_id_issued_at"]!);
        Assert.Equal("Claude Desktop", (string)node["client_name"]!);
        Assert.Equal("none", (string)node["token_endpoint_auth_method"]!);
        var uris = node["redirect_uris"]!.AsArray();
        Assert.Single(uris);
        Assert.Equal("https://claude.ai/api/mcp/auth_callback", (string)uris[0]!);
    }

    [Fact]
    public void RegistrationResponseDto_NullClientName_IsOmittedFromOutput()
    {
        // Pin the WhenWritingNull behavior: a null ClientName should not appear in the JSON at
        // all, not even as "client_name": null. This matches how the live endpoint is configured.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var dto = new RegisterEndpoint.RegistrationResponseDto(
            ClientId: "abc",
            ClientIdIssuedAt: 0L,
            ClientName: null,
            RedirectUris: ["https://example.com/cb"],
            TokenEndpointAuthMethod: "none");

        var json = JsonSerializer.Serialize(dto, options);
        var node = JsonNode.Parse(json)!.AsObject();

        Assert.False(node.ContainsKey("client_name"), $"client_name should be omitted when null, got {json}");
    }

    // ---------------- RFC 8414 — Authorization Server Metadata ----------------

    [Fact]
    public void AuthorizationServerMetadata_EmitsRfc8414SnakeCaseFieldNames()
    {
        // Match DiscoveryEndpoints options (line 34-37): Web + WhenWritingNull.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var dto = new DiscoveryEndpoints.AuthorizationServerMetadata(
            Issuer: "https://devbrain.example.com",
            RegistrationEndpoint: "https://devbrain.example.com/register",
            AuthorizationEndpoint: "https://devbrain.example.com/authorize",
            TokenEndpoint: "https://devbrain.example.com/token",
            ResponseTypesSupported: ["code"],
            GrantTypesSupported: ["authorization_code", "refresh_token"],
            CodeChallengeMethodsSupported: ["S256"],
            TokenEndpointAuthMethodsSupported: ["none"],
            ScopesSupported: ["documents.readwrite"]);

        var json = JsonSerializer.Serialize(dto, options);
        var node = JsonNode.Parse(json)!.AsObject();

        // RFC 8414 §2 — every field by its spec name.
        Assert.True(node.ContainsKey("issuer"), $"missing issuer in {json}");
        Assert.True(node.ContainsKey("registration_endpoint"), $"missing registration_endpoint in {json}");
        Assert.True(node.ContainsKey("authorization_endpoint"), $"missing authorization_endpoint in {json}");
        Assert.True(node.ContainsKey("token_endpoint"), $"missing token_endpoint in {json}");
        Assert.True(node.ContainsKey("response_types_supported"), $"missing response_types_supported in {json}");
        Assert.True(node.ContainsKey("grant_types_supported"), $"missing grant_types_supported in {json}");
        Assert.True(node.ContainsKey("code_challenge_methods_supported"), $"missing code_challenge_methods_supported in {json}");
        Assert.True(node.ContainsKey("token_endpoint_auth_methods_supported"), $"missing token_endpoint_auth_methods_supported in {json}");
        Assert.True(node.ContainsKey("scopes_supported"), $"missing scopes_supported in {json}");

        // camelCase leak guards (spot-check the ones with multi-word names)
        Assert.False(node.ContainsKey("registrationEndpoint"), $"camelCase leaked in {json}");
        Assert.False(node.ContainsKey("authorizationEndpoint"), $"camelCase leaked in {json}");
        Assert.False(node.ContainsKey("tokenEndpoint"), $"camelCase leaked in {json}");
        Assert.False(node.ContainsKey("responseTypesSupported"), $"camelCase leaked in {json}");
        Assert.False(node.ContainsKey("codeChallengeMethodsSupported"), $"camelCase leaked in {json}");
        Assert.False(node.ContainsKey("tokenEndpointAuthMethodsSupported"), $"camelCase leaked in {json}");
    }

    // ---------------- RFC 9728 — Protected Resource Metadata ----------------

    [Fact]
    public void ProtectedResourceMetadata_EmitsRfc9728SnakeCaseFieldNames()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var dto = new DiscoveryEndpoints.ProtectedResourceMetadata(
            Resource: "https://devbrain.example.com/runtime/webhooks/mcp",
            AuthorizationServers: ["https://devbrain.example.com"],
            BearerMethodsSupported: ["header"],
            ScopesSupported: ["documents.readwrite"]);

        var json = JsonSerializer.Serialize(dto, options);
        var node = JsonNode.Parse(json)!.AsObject();

        // RFC 9728 §2 — required + optional fields by spec name.
        Assert.True(node.ContainsKey("resource"), $"missing resource in {json}");
        Assert.True(node.ContainsKey("authorization_servers"), $"missing authorization_servers in {json}");
        Assert.True(node.ContainsKey("bearer_methods_supported"), $"missing bearer_methods_supported in {json}");
        Assert.True(node.ContainsKey("scopes_supported"), $"missing scopes_supported in {json}");

        // camelCase leak guards
        Assert.False(node.ContainsKey("authorizationServers"), $"camelCase leaked in {json}");
        Assert.False(node.ContainsKey("bearerMethodsSupported"), $"camelCase leaked in {json}");
        Assert.False(node.ContainsKey("scopesSupported"), $"camelCase leaked in {json}");
    }

    // ---------------- Behavior pin: attributes win over naming policy ----------------

    [Fact]
    public void JsonPropertyName_AlwaysWinsOverWebNamingPolicy()
    {
        // Meta-regression: pin the System.Text.Json contract we rely on. If a future .NET release
        // ever changes [JsonPropertyName] vs JsonSerializerDefaults.Web's PropertyNamingPolicy
        // precedence, this test fails before shipping.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var sample = new SampleDto("hello");
        var json = JsonSerializer.Serialize(sample, options);

        Assert.Contains("\"explicit_field\"", json);
        Assert.DoesNotContain("\"rawValue\"", json);
        Assert.DoesNotContain("\"RawValue\"", json);
    }

    private sealed record SampleDto([property: JsonPropertyName("explicit_field")] string RawValue);
}
