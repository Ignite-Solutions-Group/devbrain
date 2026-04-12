using DevBrain.Functions.Auth.Services;
using DevBrain.Functions.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace DevBrain.Functions.Tests.Auth.Services;

/// <summary>
/// Regression guard for the v1.6 post-deploy bug where DI failed with
/// <c>"Multiple constructors accepting all given argument types have been found in type 'EntraOAuthClient'"</c>.
///
/// <para>
/// <b>Why this file exists:</b> <c>EntraOAuthClient</c> is registered via
/// <c>AddHttpClient&lt;IUpstreamOAuthClient, EntraOAuthClient&gt;()</c>, which instantiates the
/// typed client through <see cref="ActivatorUtilities"/>.<see cref="ActivatorUtilities.CreateInstance{T}(IServiceProvider, object[])"/>.
/// <see cref="ActivatorUtilities"/> has a stricter constructor-picking algorithm than the generic
/// DI container used by <c>AddSingleton&lt;T&gt;</c>: when two constructors are both satisfiable
/// given the supplied arguments plus the service provider, it refuses to choose and throws.
/// </para>
///
/// <para>
/// Our dual-constructor pattern (3-arg convenience for tests, 4-arg with <c>ILogger</c> for DI)
/// hits this exact failure. The fix is <see cref="ActivatorUtilitiesConstructorAttribute"/> on
/// the 4-arg constructor. These tests lock in both behaviors:
/// </para>
///
/// <list type="number">
///   <item><see cref="CreateInstance_UsingActivatorUtilities_ResolvesWithoutAmbiguity"/> — calls
///         <see cref="ActivatorUtilities.CreateInstance{T}(IServiceProvider, object[])"/> the same
///         way <c>AddHttpClient</c> does. If someone ever removes the attribute, this test throws
///         <c>InvalidOperationException</c> (not just wrong behavior — hard failure).</item>
///   <item><see cref="BothConstructors_StillConstructableDirectly"/> — both ctors remain callable
///         via <c>new</c> for test ergonomics.</item>
/// </list>
/// </summary>
public sealed class EntraOAuthClientActivationTests
{
    private const string TenantGuid = "11111111-1111-1111-1111-111111111111";

    private static EntraOAuthClientOptions ValidOptions() => new()
    {
        TenantId = TenantGuid,
        ClientId = "upstream-client-id",
        ClientSecret = "upstream-client-secret",
        RedirectUri = "https://devbrain.example.com/callback",
        Scope = "openid profile offline_access documents.readwrite",
    };

    [Fact]
    public void CreateInstance_UsingActivatorUtilities_ResolvesWithoutAmbiguity()
    {
        // Build a minimal service provider with everything ActivatorUtilities would need to
        // satisfy either constructor: Options, IConfigurationManager, and the logger
        // infrastructure (NullLoggerFactory registers ILogger<T> for every T).
        var services = new ServiceCollection();
        services.AddSingleton(ValidOptions());
        services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(
            FakeOpenIdConfigurationManager.ForTenant(TenantGuid));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        using var provider = services.BuildServiceProvider();

        // AddHttpClient supplies HttpClient as the additional argument. We replicate that here.
        using var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);

        // The Act — this is the exact call AddHttpClient makes internally. Without
        // [ActivatorUtilitiesConstructor], this throws InvalidOperationException with
        // "Multiple constructors accepting all given argument types have been found".
        var client = ActivatorUtilities.CreateInstance<EntraOAuthClient>(provider, httpClient);

        Assert.NotNull(client);
    }

    [Fact]
    public void BothConstructors_StillConstructableDirectly()
    {
        // Tests use `new` directly, bypassing DI entirely. Both constructors must remain usable
        // so existing test code doesn't have to change.
        using var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var configManager = FakeOpenIdConfigurationManager.ForTenant(TenantGuid);

        // 3-arg (test convenience — null logger)
        var viaShortCtor = new EntraOAuthClient(http, ValidOptions(), configManager);
        Assert.NotNull(viaShortCtor);

        // 4-arg (explicit logger — this is the one DI uses)
        var viaLongCtor = new EntraOAuthClient(http, ValidOptions(), configManager, logger: null);
        Assert.NotNull(viaLongCtor);
    }
}
