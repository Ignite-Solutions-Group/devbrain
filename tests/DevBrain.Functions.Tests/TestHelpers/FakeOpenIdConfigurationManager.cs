using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DevBrain.Functions.Tests.TestHelpers;

/// <summary>
/// Test double for <see cref="IConfigurationManager{T}"/> returning a pre-populated
/// <see cref="OpenIdConnectConfiguration"/>. Lets tests supply their own signing keys so they can
/// mint id_tokens that pass (or fail) validation in <c>EntraOAuthClient</c>.
///
/// <para>
/// The real <see cref="ConfigurationManager{T}"/> is sealed; this fake implements the interface
/// directly so the production code path (which takes <see cref="IConfigurationManager{T}"/>) can
/// be unit-tested without hitting the network.
/// </para>
/// </summary>
public sealed class FakeOpenIdConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
{
    public OpenIdConnectConfiguration Configuration { get; set; } = new();

    public int FetchCalls { get; private set; }

    /// <summary>When set, <see cref="GetConfigurationAsync"/> throws this exception instead of returning <see cref="Configuration"/>.</summary>
    public Exception? ThrowOnFetch { get; set; }

    public static FakeOpenIdConfigurationManager ForTenant(string tenantGuid) =>
        new()
        {
            Configuration = new OpenIdConnectConfiguration
            {
                Issuer = $"https://login.microsoftonline.com/{tenantGuid}/v2.0",
            },
        };

    public void AddSigningKey(SecurityKey key) => Configuration.SigningKeys.Add(key);

    public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        FetchCalls++;
        if (ThrowOnFetch is not null)
        {
            throw ThrowOnFetch;
        }
        return Task.FromResult(Configuration);
    }

    public void RequestRefresh() { /* no-op in tests */ }
}
