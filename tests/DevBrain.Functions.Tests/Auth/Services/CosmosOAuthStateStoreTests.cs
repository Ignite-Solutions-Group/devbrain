using DevBrain.Core.Auth.Services;

namespace DevBrain.Functions.Tests.Auth.Services;

public sealed class CosmosOAuthStateStoreTests
{
    [Theory]
    [InlineData("", "client", "abc", "client:abc")]
    [InlineData("v2:", "upstream", "jti", "v2:upstream:jti")]
    public void ComposeKey_AppliesHostNamespace(
        string prefix,
        string recordKind,
        string identifier,
        string expected)
    {
        Assert.Equal(expected, CosmosOAuthStateStore.ComposeKey(prefix, recordKind, identifier));
    }
}
