using DMCBK.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk;
using Umpk.Client;
using Umpk.Data.Java;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Transport;

namespace DMCBK.Core;

/// <summary>
/// Fluent builder producing an <see cref="Client"/>.
/// Accepts a server endpoint, an account (offline or online), an optional explicit version, a feature composition, a logger factory, a host interface, an optional token-store path, an optional forwarding proxy, and a reconnect-policy provider.
/// No plugins yet.
/// </summary>
public sealed partial class ClientBuilder
{
    private ServerEndpoint? _endpoint;
    private MccAccount? _account;
    private string? _tokenStorePath;
    private JavaVersion? _version;
    private ClientFeatures _features = new();
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private IHostInterface _host = new NullHostInterface();
    private MccConfiguration? _configuration;
    private IConnectionFactory? _proxyFactory;
    private bool _proxyForPing;
    private bool _pingForDisplayWhenPinned;
    private IReconnectPolicyProvider? _reconnectProvider;
    private ProxyConfig? _configuredProxy;

    /// <summary>Sets the server endpoint to connect to.</summary>
    public ClientBuilder UseServer(ServerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoint = endpoint;
        return this;
    }

    /// <summary>Sets the server endpoint from a host and port.</summary>
    public ClientBuilder UseServer(string host, ushort port = ServerEndpoint.DefaultJavaPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _endpoint = new ServerEndpoint(host, port);
        return this;
    }

    /// <summary>Sets the account (offline or online) to log in as.</summary>
    public ClientBuilder UseAccount(MccAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        _account = account;
        return this;
    }

    /// <summary>Offline sugar over <see cref="UseAccount"/>: logs in offline as the given username.</summary>
    public ClientBuilder UseUsername(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        _account = MccAccount.Offline(username);
        return this;
    }

    /// <summary>
    /// Sets the directory used to persist auth tokens and certificates for online accounts.
    /// When unset, online accounts use an in-memory store (no persistence across runs).
    /// Ignored for offline accounts.
    /// </summary>
    public ClientBuilder UseTokenStorePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _tokenStorePath = path;
        return this;
    }

    /// <summary>Pins an explicit version; when unset, the version is auto-detected by status ping.</summary>
    public ClientBuilder UseVersion(JavaVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        _version = version;
        return this;
    }

    /// <summary>
    /// Overrides the feature composition (default: all on).
    /// A programmatic caller states the WHOLE coherent composition here: UMPK's <see cref="ClientFeatures.Normalized"/> resolves implied dependencies UPWARD at <c>Build()</c> (physics implies terrain; pathfinding implies physics), so setting only <c>Terrain = false</c> while leaving <c>Physics</c>/<c>Pathfinding</c> at their defaulted <c>true</c> does not turn terrain off for the engine UMPK builds; it turns terrain back ON for it.
    /// That is the right behavior for a caller who opts INTO one capability at a time, but it means a caller who wants terrain (and, transitively, physics and pathfinding) off must say so for all three.
    /// Contrast this with the config-file path (<c>configuration validation</c>), which derives the same three keys DOWNWARD instead, because a TOML file gives every key an explicit value.
    /// </summary>
    public ClientBuilder UseFeatures(ClientFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);
        _features = features;
        return this;
    }

    /// <summary>
    /// Configures the current feature composition in place (mirrors <c>UmpkClientBuilder.ConfigureFeatures</c>).
    /// See <see cref="UseFeatures"/> for why a programmatic caller must state the whole coherent composition rather than flipping one key and expecting the others to follow.
    /// </summary>
    public ClientBuilder ConfigureFeatures(Action<ClientFeatures> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_features);
        return this;
    }

    /// <summary>Supplies the logger factory (default: <see cref="NullLoggerFactory"/>).</summary>
    public ClientBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory = loggerFactory;
        return this;
    }

    /// <summary>Supplies the host interface seam (default: a no-op host).</summary>
    public ClientBuilder UseHostInterface(IHostInterface host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        return this;
    }

    /// <summary>
    /// Routes the whole session (and, when <paramref name="forPing"/> is set, the version-detection ping) through the given forwarding-proxy connection factory.
    /// UMPK uses one connection factory per session, so a login-only proxy cannot be separated from in-game traffic.
    /// </summary>
    public ClientBuilder UseProxy(IConnectionFactory proxyFactory, bool forPing = true)
    {
        ArgumentNullException.ThrowIfNull(proxyFactory);
        _proxyFactory = proxyFactory;
        _proxyForPing = forPing;
        return this;
    }

    /// <summary>
    /// Whether to fire a one-shot, best-effort status ping purely for a host's connect-time status-panel display when the version is pinned; pinning otherwise skips the status ping entirely (it is not needed to resolve the version).
    /// Defaults to <c>false</c>, preserving "pinned means no ping" for every existing caller: a caller supplying a non-standard <see cref="IConnectionFactory"/> that does not support two independent concurrent connections (for example a hermetic test double backed by a single fixed pipe) would otherwise have this extra ping corrupt its one real connection.
    /// Only a host that actually renders the panel (the classic and TUI CLI hosts) should turn this on.
    /// </summary>
    public ClientBuilder UsePingForDisplayWhenPinned(bool enabled = true)
    {
        _pingForDisplayWhenPinned = enabled;
        return this;
    }

    /// <summary>Supplies the plugin-overridable reconnect-policy seam (default: no auto-reconnect).</summary>
    public ClientBuilder UseReconnectPolicyProvider(IReconnectPolicyProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _reconnectProvider = provider;
        return this;
    }

    /// <summary>
    /// Applies an immutable configuration snapshot, mapping its resolved endpoint, version, account and feature gates onto the builder inputs.
    /// Later explicit <c>Use*</c> calls still override these values.
    /// </summary>
    public ClientBuilder UseConfiguration(MccConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _configuration = config;

        if (!string.IsNullOrWhiteSpace(config.ResolvedHost))
            _endpoint = new ServerEndpoint(config.ResolvedHost, config.ResolvedPort);

        _features = new ClientFeatures
        {
            Terrain = config.Gameplay.Terrain,
            Inventory = config.Gameplay.Inventory,
            Entities = config.Gameplay.Entity,
            Physics = config.Gameplay.Physics,
            Pathfinding = config.Gameplay.Pathfinding,
        };

        if (!string.Equals(config.ResolvedVersion, "auto", StringComparison.OrdinalIgnoreCase))
        {
            if (!JavaVersions.TryGetByName(config.ResolvedVersion, out JavaVersion version))
            {
                throw new InvalidOperationException(
                    $"The configuration pins an unknown Minecraft version '{config.ResolvedVersion}'.");
            }

            _version = version;
        }
        else
            _version = null;

        MccAccount? account = ToAccount(config.ResolvedAccount);
        if (account is not null)
            _account = account;

        // The concrete proxy factory is built in Build() so it captures the final logger factory (the CLI sets the logger after UseConfiguration).
        // The reconnect provider needs no logger.
        _configuredProxy = config.Proxy;
        _reconnectProvider = new ConfigReconnectPolicyProvider(config.Connection.Reconnect);

        return this;
    }

    /// <summary>
    /// Builds the UMPK connection factory for a configured proxy.
    /// Empty username/password strings are normalized to null: an empty username would otherwise make Socks5ConnectionFactory offer user/pass auth for no reason, and would put an empty (but present) userid on the SOCKS4 wire.
    /// </summary>
    internal static IConnectionFactory CreateProxyFactory(ProxyConfig proxy)
    {
        var options = new ProxyOptions
        {
            Host = proxy.Host,
            Port = proxy.Port,
            Username = string.IsNullOrEmpty(proxy.Username) ? null : proxy.Username,
            Password = string.IsNullOrEmpty(proxy.Password) ? null : proxy.Password,
        };
        return proxy.Kind switch
        {
            ProxyKind.Socks4 => new Socks4ConnectionFactory(options),
            ProxyKind.Socks4a => new Socks4ConnectionFactory(options, remoteDns: true),
            ProxyKind.Socks5 => new Socks5ConnectionFactory(options),
            _ => new HttpConnectConnectionFactory(options),
        };
    }

    private static MccAccount? ToAccount(ConfiguredAccount account)
    {
        string user = string.IsNullOrWhiteSpace(account.Login) ? account.Name : account.Login;
        if (string.IsNullOrWhiteSpace(user))
            return null;

        if (account.Kind == MccAccountKind.Offline)
            return MccAccount.Offline(user);

        Uri? authServer = null;
        if (!string.IsNullOrWhiteSpace(account.AuthServer))
        {
            authServer = Uri.TryCreate(account.AuthServer, UriKind.Absolute, out Uri? absolute)
                ? absolute
                : new Uri($"https://{account.AuthServer}");
        }

        return new MccAccount { Kind = account.Kind, User = user, AuthServerBaseUrl = authServer };
    }

    /// <summary>
    /// Builds the client.
    /// An account is required; a server is not.
    /// Built without one, the client is idle: commands, plugins and everything that does not need a session run, and it dials as soon as something names a server (<see cref="Client.ReconnectAsync"/>, which is what <c>connect</c> calls).
    /// Until then <see cref="Client.StartAsync"/> throws <see cref="MccNoServerConfiguredException"/>.
    /// </summary>
    public Client Build()
    {
        ServerEndpoint? endpoint = _endpoint;
        MccAccount account = _account
            ?? throw new InvalidOperationException("An account is required; call UseAccount or UseUsername.");

        if (account.Kind == MccAccountKind.Offline && account.User.Length > 16)
            throw new InvalidOperationException("An offline username must be at most 16 characters.");

        IConnectionFactory? proxyFactory = _proxyFactory;
        bool proxyForPing = _proxyForPing;
        if (proxyFactory is null && _configuredProxy is { } proxy && (proxy.EnabledLogin || proxy.EnabledIngame))
        {
            // UMPK uses one connection factory for the session; when either flag is on we proxy the whole session and route the pre-login ping through it only when login proxying is requested.
            proxyFactory = CreateProxyFactory(proxy);
            proxyForPing = proxy.EnabledLogin;
        }

        // FixedReconnectPolicyProvider(null): the default reconnect provider, no auto-reconnect (matches legacy default).
        IReconnectPolicyProvider reconnectProvider = _reconnectProvider ?? new FixedReconnectPolicyProvider(null);

        Client client = new Client(
            endpoint, account, _tokenStorePath, _version, _features, _loggerFactory, _host, _configuration,
            proxyFactory, proxyForPing, _pingForDisplayWhenPinned, reconnectProvider);
        client.Application = _application;
        try
        {
            foreach (Action<Client> installer in _moduleInstallers)
                installer(client);
            return client;
        }
        catch
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }
}
