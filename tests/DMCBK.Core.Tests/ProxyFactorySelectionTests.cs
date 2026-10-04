using DMCBK.Core;
using DMCBK.Core.Configuration;
using Umpk.Protocol.Java.Transport;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Asserts ClientBuilder.CreateProxyFactory picks the right UMPK connection factory type (and the right SOCKS4/SOCKS4a RemoteDns flag) for each configured ProxyKind.
/// </summary>
public sealed class ProxyFactorySelectionTests
{
    [Fact]
    public void Http_CreatesHttpConnectConnectionFactory()
    {
        var proxy = new ProxyConfig { Kind = ProxyKind.Http, Host = "127.0.0.1", Port = 8080 };

        var factory = ClientBuilder.CreateProxyFactory(proxy);

        Assert.IsType<HttpConnectConnectionFactory>(factory);
    }

    [Fact]
    public void Socks4_CreatesSocks4ConnectionFactoryWithRemoteDnsFalse()
    {
        var proxy = new ProxyConfig { Kind = ProxyKind.Socks4, Host = "127.0.0.1", Port = 1080 };

        var factory = ClientBuilder.CreateProxyFactory(proxy);

        var socks4 = Assert.IsType<Socks4ConnectionFactory>(factory);
        Assert.False(socks4.RemoteDns);
    }

    [Fact]
    public void Socks4a_CreatesSocks4ConnectionFactoryWithRemoteDnsTrue()
    {
        var proxy = new ProxyConfig { Kind = ProxyKind.Socks4a, Host = "127.0.0.1", Port = 1080 };

        var factory = ClientBuilder.CreateProxyFactory(proxy);

        var socks4 = Assert.IsType<Socks4ConnectionFactory>(factory);
        Assert.True(socks4.RemoteDns);
    }

    [Fact]
    public void Socks5_CreatesSocks5ConnectionFactory()
    {
        var proxy = new ProxyConfig { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = 1080 };

        var factory = ClientBuilder.CreateProxyFactory(proxy);

        Assert.IsType<Socks5ConnectionFactory>(factory);
    }
}
