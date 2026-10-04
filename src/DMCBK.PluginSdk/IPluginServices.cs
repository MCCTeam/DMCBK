using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace DMCBK.PluginSdk;

/// <summary>
/// Direct inter-plugin calls: a plugin registers one instance per contract type and any other plugin looks it up.
/// The other half of <see cref="IPluginMessenger"/>, for the shape publish/subscribe fits badly, namely "call this and give me an answer now, through an interface with more than one method".
/// <para>
/// The same load-context rule applies.
/// A type declared inside a plugin's own assembly is private to that plugin's collectible load context, so another plugin's copy of the same declaration is a DIFFERENT runtime type.
/// A contract is exchangeable when it comes from the BCL, from MCC, from an assembly both plugins list in <c>deps</c>, or, since SDK 2.1, from an assembly the registering plugin names in <c>[exports]</c> and the calling plugin names in <c>[requires]</c>.
/// </para>
/// <para>
/// Registrations are per plugin and the host disposes them on unload, so a service never outlives the plugin that offered it.
/// </para>
/// </summary>
public interface IPluginServices
{
    /// <summary>
    /// Registers <paramref name="instance"/> as the implementation of contract <typeparamref name="T"/>.
    /// Dispose the handle to withdraw it; the host disposes it for you when your plugin unloads.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another plugin already offers this contract.</exception>
    IDisposable Register<T>(T instance)
        where T : class;

    /// <summary>
    /// Looks up the instance registered for contract <typeparamref name="T"/>; false when nobody offers it, which is a legitimate answer and the one an <c>[optional]</c> dependency produces when absent.
    /// </summary>
    /// <exception cref="PluginContractMismatchException">
    /// Something IS registered under this contract name but with an incompatible private copy of the type, so it cannot be handed back.
    /// Returning false here would be indistinguishable from "nobody offers it", which is the silent drop this API refuses to perform.
    /// </exception>
    bool TryGet<T>([NotNullWhen(true)] out T? instance)
        where T : class;
}

/// <summary>
/// The host-wide service registry shared by all plugins, keyed by <see cref="PluginContract.NameOf"/> exactly as the messenger's routing is, so the same cross-context diagnostics apply.
/// </summary>
internal sealed class PluginServiceHub
{
    private readonly ConcurrentDictionary<string, Registration> _services = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    internal PluginServiceHub(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// The host's second opinion on a contract <see cref="PluginContract.IsShared"/> calls private; see <see cref="PluginMessengerHub.IsExchangeable"/>.
    /// An exported assembly's types unify across the contexts that resolve them.
    /// </summary>
    internal Func<Type, bool>? IsExchangeable { get; set; }
    internal Func<string, string, bool>? CanCommunicate { get; set; }

    internal IDisposable Register<T>(string owner, T instance)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        string key = PluginContract.NameOf(typeof(T));
        var registration = new Registration(typeof(T), owner, instance);
        if (!_services.TryAdd(key, registration))
        {
            throw new InvalidOperationException(
                PluginStrings.ServiceTaken(key, _services[key].Owner));
        }

        if (!PluginContract.IsShared(typeof(T)) && IsExchangeable?.Invoke(typeof(T)) != true)
        {
            // A private contract nobody else can match.
            // Said at registration, not at the first failed lookup.
            _logger.LogWarning("{Message}", PluginStrings.ServicePrivateContract(
                owner, key, PluginContract.LoadContextNameOf(typeof(T))));
        }

        return new Handle(() => _services.TryRemove(new KeyValuePair<string, Registration>(key, registration)));
    }

    internal bool TryGet<T>(string owner, [NotNullWhen(true)] out T? instance)
        where T : class
    {
        string key = PluginContract.NameOf(typeof(T));
        if (!_services.TryGetValue(key, out Registration? registration) || CanCommunicate?.Invoke(owner, registration.Owner) == false)
        {
            instance = null;
            return false;
        }

        if (registration.Instance is not T typed)
        {
            throw new PluginContractMismatchException(
                PluginStrings.MessengerContractMismatch(
                    key,
                    owner,
                    PluginContract.LoadContextNameOf(typeof(T)),
                    registration.Owner,
                    PluginContract.LoadContextNameOf(registration.Contract)),
                key,
                owner,
                PluginContract.LoadContextNameOf(typeof(T)),
                registration.Owner,
                PluginContract.LoadContextNameOf(registration.Contract));
        }

        instance = typed;
        return true;
    }

    private sealed class Registration(Type contract, string owner, object instance)
    {
        internal Type Contract { get; } = contract;

        internal string Owner { get; } = owner;

        internal object Instance { get; } = instance;
    }

    private sealed class Handle(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>A per-plugin view over the shared <see cref="PluginServiceHub"/> that tracks its own handles.</summary>
internal sealed class PluginServices : IPluginServices
{
    private readonly PluginServiceHub _hub;
    private readonly string _owner;
    private readonly object _gate = new();
    private readonly List<IDisposable> _handles = [];

    internal PluginServices(PluginServiceHub hub, string owner)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        _hub = hub;
        _owner = owner;
    }

    /// <inheritdoc/>
    public IDisposable Register<T>(T instance)
        where T : class
    {
        IDisposable handle = _hub.Register(_owner, instance);
        lock (_gate)
            _handles.Add(handle);

        return handle;
    }

    /// <inheritdoc/>
    public bool TryGet<T>([NotNullWhen(true)] out T? instance)
        where T : class
        => _hub.TryGet(_owner, out instance);

    /// <summary>Withdraws every service this plugin offered (host teardown).</summary>
    internal void DisposeAll()
    {
        IDisposable[] handles;
        lock (_gate)
        {
            handles = [.. _handles];
            _handles.Clear();
        }

        foreach (IDisposable handle in handles)
            handle.Dispose();
    }
}
