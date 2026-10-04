using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The provider-URL rules and the metadata check behind <see cref="AuthServerProbe"/>.
/// Network-free: the normalizer is pure, and the response check runs against literal bodies.
/// </summary>
public sealed class AuthServerProbeTests
{
    [Theory]
    [InlineData("https://auth.example.com/api/yggdrasil", "https://auth.example.com/api/yggdrasil/")]
    [InlineData("https://auth.example.com/api/yggdrasil/", "https://auth.example.com/api/yggdrasil/")]
    [InlineData("http://127.0.0.1:25585/authlib-injector", "http://127.0.0.1:25585/authlib-injector/")]
    [InlineData("  https://auth.example.com  ", "https://auth.example.com/")]
    public void TryNormalize_AppendsTheTrailingSlashEveryConsumerResolvesAgainst(string input, string expected)
    {
        Assert.True(AuthServerProbe.TryNormalize(input, out Uri? normalized));
        Assert.Equal(expected, normalized.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auth.example.com")]              // relative: no scheme
    [InlineData("ftp://auth.example.com/api")]    // not http(s)
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:pass@auth.example.com/")]  // credentials belong in the login prompt
    [InlineData("https://auth.example.com/api?token=1")]
    [InlineData("https://auth.example.com/api#frag")]
    public void TryNormalize_RejectsWhatCannotBeAProviderRoot(string? input)
    {
        Assert.False(AuthServerProbe.TryNormalize(input, out Uri? normalized));
        Assert.Null(normalized);
    }

    [Fact]
    public void HasImplementationName_AcceptsAuthlibInjectorMetadata()
    {
        const string body = """
            {"meta":{"serverName":"Example","implementationName":"drasl","implementationVersion":"1.0"},
             "skinDomains":["example.com"],"signaturePublickey":"..."}
            """;
        Assert.True(AuthServerProbe.HasImplementationName(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"hello":"world"}""")]
    [InlineData("""{"meta":{}}""")]
    [InlineData("""{"meta":{"implementationName":""}}""")]
    [InlineData("""{"meta":{"implementationName":"   "}}""")]
    [InlineData("""{"meta":{"implementationName":42}}""")]
    [InlineData("""{"meta":"drasl"}""")]
    public void HasImplementationName_RejectsAnythingElse(string body)
        => Assert.False(AuthServerProbe.HasImplementationName(body));
}
