using System.Collections.Concurrent;
using System.Runtime.Loader;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DMCBK.PluginSdk;

/// <summary>
/// Inter-plugin communication: typed publish/subscribe plus typed request/response.
/// Use this instead of cross-plugin statics such as <c>DiscordBridge.GetInstance()</c>, shared observed-state singletons, and Map-to-bridge coupling.
/// Messages are routed by their contract identity (see <see cref="PluginContract.NameOf"/>); a plugin publishes a value and every subscriber to that contract receives it, or a plugin registers a single responder for a request contract and others call it.
/// All subscriptions/responders return/track disposables the host cleans up on unload.
/// <para>
/// SUPPORTED PAYLOADS.
/// Every plugin is loaded into its own collectible <see cref="AssemblyLoadContext"/>, so a type declared inside plugin A's assembly and a type with the same name declared inside plugin B's assembly are two DIFFERENT runtime types that cannot be cast to one another.
/// A message contract is therefore deliverable across plugins only when its type is SHARED, that is loaded in the default context: BCL types, MCC SDK/core types, and types from an assembly listed in the <c>deps</c> of the manifest.
/// See <see cref="PluginContract.IsShared"/>.
/// </para>
/// <para>
/// UNSUPPORTED PAYLOADS FAIL LOUDLY.
/// A plugin-private contract is never dropped in silence.
/// It raises a <see cref="PluginContractMismatchException"/> from <see cref="Publish{T}"/> and <see cref="TryRequest{TRequest, TResponse}"/> naming both plugins, both load contexts and the fix, and the host logs a warning as soon as the second, incompatible registration arrives, before any message is even sent.
/// </para>
/// </summary>
public interface IPluginMessenger
{
    /// <summary>Subscribes to every message published with contract <typeparamref name="T"/>. Dispose to unsubscribe.</summary>
    IDisposable Subscribe<T>(Action<T> handler);

    /// <summary>
    /// Publishes a message to all subscribers of contract <typeparamref name="T"/>.
    /// Delivers to every compatible subscriber first, then throws <see cref="PluginContractMismatchException"/> if any subscriber registered an incompatible private copy of the same contract.
    /// </summary>
    /// <exception cref="PluginContractMismatchException">
    /// A subscriber registered a same-named contract type from a different plugin load context, so the message cannot be delivered to it.
    /// </exception>
    void Publish<T>(T message);

    /// <summary>Registers the single responder for request contract <typeparamref name="TRequest"/>. Dispose to remove.</summary>
    IDisposable RegisterResponder<TRequest, TResponse>(Func<TRequest, TResponse> responder);

    /// <summary>
    /// Invokes the registered responder for <typeparamref name="TRequest"/>; false when none is registered.
    /// </summary>
    /// <exception cref="PluginContractMismatchException">
    /// A responder IS registered for this contract name but was registered with an incompatible private copy of the type, so it cannot be called.
    /// Returning false here would be indistinguishable from "nobody is listening", which would silently drop the message.
    /// </exception>
    bool TryRequest<TRequest, TResponse>(TRequest request, out TResponse? response);
}

/// <summary>
/// Contract identity for messenger payloads: an assembly-agnostic name plus the shared/private test.
/// Two plugin-private copies of the same declaration produce the SAME name (which is how the mismatch becomes detectable at all) but are different <see cref="Type"/> instances (which is why delivery is impossible).
/// </summary>
public static class PluginContract
{
    /// <summary>
    /// The assembly-agnostic contract name of a type: the full name with generic arguments and array element types resolved recursively and assembly qualification stripped, so <c>List&lt;Foo&gt;</c> from two different plugins yields one name rather than two.
    /// </summary>
    public static string NameOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var builder = new StringBuilder();
        Append(builder, type);
        return builder.ToString();
    }

    /// <summary>
    /// True when the type can travel between plugins: it and every type argument live outside a collectible plugin load context (the BCL, the MCC assemblies, or a shared <c>deps</c> assembly).
    /// </summary>
    public static bool IsShared(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.HasElementType && type.GetElementType() is { } element && !IsShared(element))
            return false;

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                if (!IsShared(argument))
                    return false;
            }
        }

        return LoadContextOf(type) is not { IsCollectible: true };
    }

    /// <summary>The display name of the load context a type was loaded into (<c>default</c> when shared).</summary>
    public static string LoadContextNameOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        AssemblyLoadContext? context = LoadContextOf(type);
        return context is null || ReferenceEquals(context, AssemblyLoadContext.Default)
            ? "default"
            : context.Name ?? "unnamed";
    }

    private static AssemblyLoadContext? LoadContextOf(Type type)
        => AssemblyLoadContext.GetLoadContext(type.Assembly);

    private static void Append(StringBuilder builder, Type type)
    {
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            Append(builder, element);
            builder.Append(type.IsArray ? "[]" : type.IsPointer ? "*" : "&");
            return;
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            builder.Append(type.GetGenericTypeDefinition().FullName ?? type.GetGenericTypeDefinition().Name);
            builder.Append('[');
            Type[] arguments = type.GetGenericArguments();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i > 0)
                    builder.Append(',');

                Append(builder, arguments[i]);
            }

            builder.Append(']');
            return;
        }

        builder.Append(type.FullName ?? type.Name);
    }
}

/// <summary>
/// Thrown when a messenger contract name is registered by two plugins with two different, plugin-private copies of the type, so the payload cannot cross between them.
/// Carries enough detail to fix it: the contract name and both sides' plugin ids and load contexts.
/// </summary>
public sealed class PluginContractMismatchException : InvalidOperationException
{
    internal PluginContractMismatchException(
        string message, string contract, string publisherOwner, string publisherContext, string subscriberOwner, string subscriberContext)
        : base(message)
    {
        Contract = contract;
        PublisherOwner = publisherOwner;
        PublisherLoadContext = publisherContext;
        SubscriberOwner = subscriberOwner;
        SubscriberLoadContext = subscriberContext;
    }

    /// <summary>The assembly-agnostic contract name that failed to unify.</summary>
    public string Contract { get; }

    /// <summary>The plugin id that published (or requested).</summary>
    public string PublisherOwner { get; }

    /// <summary>The load context the publisher's copy of the contract came from.</summary>
    public string PublisherLoadContext { get; }

    /// <summary>The plugin id that subscribed (or responded).</summary>
    public string SubscriberOwner { get; }

    /// <summary>The load context the subscriber's copy of the contract came from.</summary>
    public string SubscriberLoadContext { get; }
}

/// <summary>
/// The host-wide message bus shared by all plugins.
/// One instance per plugin host; each plugin gets its own <see cref="PluginMessenger"/> view over it so the host can dispose exactly that plugin's registrations on unload without touching others.
/// Routing is keyed by <see cref="PluginContract.NameOf"/> rather than by <see cref="Type"/> identity: keying by <c>Type</c> made a cross-plugin contract land in a different bucket from its own subscribers, which is what made the drop invisible.
/// </summary>
internal sealed class PluginMessengerHub
{
    private readonly ConcurrentDictionary<string, List<Registration>> _subscribers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Registration> _responders = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    internal PluginMessengerHub(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// The host's second opinion on a contract <see cref="PluginContract.IsShared"/> calls private.
    /// A plugin can export an assembly, and a dependent then resolves the very same type out of the exporter's context, so such a type is exchangeable despite living in a collectible context.
    /// Null until a host supplies one.
    /// </summary>
    internal Func<Type, bool>? IsExchangeable { get; set; }
    internal Func<string, string, bool>? CanCommunicate { get; set; }

    internal IDisposable Subscribe<T>(string owner, Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var registration = new Registration(typeof(T), owner, handler);
        string key = PluginContract.NameOf(typeof(T));
        List<Registration> list = _subscribers.GetOrAdd(key, static _ => []);
        lock (list)
        {
            WarnOnIncompatibleNeighbor(key, registration, list);
            list.Add(registration);
        }

        return new Unsubscriber(() =>
        {
            lock (list)
                list.Remove(registration);
        });
    }

    internal void Publish<T>(string owner, T message)
    {
        string key = PluginContract.NameOf(typeof(T));
        if (!_subscribers.TryGetValue(key, out List<Registration>? list))
            return; // Nobody is listening. That is legitimate, and is NOT the same as an undeliverable message.

        Registration[] registrations;
        lock (list)
            registrations = [.. list];

        Registration? mismatch = null;
        foreach (Registration registration in registrations)
        {
            if (CanCommunicate?.Invoke(owner, registration.Owner) == false) continue;
            if (!ReferenceEquals(registration.Contract, typeof(T)))
            {
                // Same contract name, different runtime type: two private copies in two collectible load contexts.
                // The cast below would throw, so record it and report after the compatible subscribers have had their message.
                mismatch ??= registration;
                continue;
            }

            try
            {
                ((Action<T>)registration.Handler)(message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Message}", PluginStrings.MessengerSubscriberThrew(registration.Owner, key));
            }
        }

        if (mismatch is not null)
            throw Mismatch(key, typeof(T), owner, mismatch);
    }

    internal IDisposable RegisterResponder<TRequest, TResponse>(string owner, Func<TRequest, TResponse> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);
        string key = PluginContract.NameOf(typeof(TRequest));
        var registration = new Registration(typeof(TRequest), owner, responder);
        if (!_responders.TryAdd(key, registration))
            throw new InvalidOperationException(PluginStrings.MessengerResponderTaken(key));

        WarnOnPrivateContract(key, registration);
        return new Unsubscriber(() => _responders.TryRemove(new KeyValuePair<string, Registration>(key, registration)));
    }

    internal bool TryRequest<TRequest, TResponse>(string owner, TRequest request, out TResponse? response)
    {
        string key = PluginContract.NameOf(typeof(TRequest));
        if (!_responders.TryGetValue(key, out Registration? registration) || CanCommunicate?.Invoke(owner, registration.Owner) == false)
        {
            response = default;
            return false;
        }

        if (!ReferenceEquals(registration.Contract, typeof(TRequest)) || registration.Handler is not Func<TRequest, TResponse> responder)
            // A responder IS registered under this contract name but cannot be invoked with our copy of the type.
            // Reporting "false" here would be the silent drop wearing a return value.
            throw Mismatch(key, typeof(TRequest), owner, registration);

        response = responder(request);
        return true;
    }

    /// <summary>
    /// Registration-time diagnostic: when a second plugin registers an incompatible private copy of a contract, warn at once.
    /// This fires before any message is published, which is the earliest point the mismatch can be detected.
    /// </summary>
    private void WarnOnIncompatibleNeighbor(string key, Registration incoming, List<Registration> existing)
    {
        foreach (Registration other in existing)
        {
            if (ReferenceEquals(other.Contract, incoming.Contract)
                || string.Equals(other.Owner, incoming.Owner, StringComparison.Ordinal))
                continue;

            _logger.LogWarning("{Message}", PluginStrings.MessengerContractMismatch(
                key,
                other.Owner,
                PluginContract.LoadContextNameOf(other.Contract),
                incoming.Owner,
                PluginContract.LoadContextNameOf(incoming.Contract)));
            return;
        }

        WarnOnPrivateContract(key, incoming);
    }

    /// <summary>
    /// Registration-time diagnostic for the first registration of a plugin-private contract: no other plugin can ever match it, so warn once at the point of registration rather than dropping messages later.
    /// </summary>
    private void WarnOnPrivateContract(string key, Registration registration)
    {
        if (PluginContract.IsShared(registration.Contract)
            || IsExchangeable?.Invoke(registration.Contract) == true)
            return;

        _logger.LogWarning("{Message}", PluginStrings.MessengerPrivateContract(
            registration.Owner, key, PluginContract.LoadContextNameOf(registration.Contract)));
    }

    private static PluginContractMismatchException Mismatch(string key, Type mine, string owner, Registration other)
    {
        string myContext = PluginContract.LoadContextNameOf(mine);
        string otherContext = PluginContract.LoadContextNameOf(other.Contract);
        return new PluginContractMismatchException(
            PluginStrings.MessengerContractMismatch(key, owner, myContext, other.Owner, otherContext),
            key, owner, myContext, other.Owner, otherContext);
    }

    private sealed class Registration(Type contract, string owner, Delegate handler)
    {
        internal Type Contract { get; } = contract;

        internal string Owner { get; } = owner;

        internal Delegate Handler { get; } = handler;
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}

/// <summary>A per-plugin view over the shared <see cref="PluginMessengerHub"/> that tracks its own handles.</summary>
internal sealed class PluginMessenger : IPluginMessenger
{
    private readonly PluginMessengerHub _hub;
    private readonly string _owner;
    private readonly object _gate = new();
    private readonly List<IDisposable> _handles = [];

    internal PluginMessenger(PluginMessengerHub hub, string owner)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        _hub = hub;
        _owner = owner;
    }

    /// <inheritdoc/>
    public IDisposable Subscribe<T>(Action<T> handler) => Track(_hub.Subscribe(_owner, handler));

    /// <inheritdoc/>
    public void Publish<T>(T message) => _hub.Publish(_owner, message);

    /// <inheritdoc/>
    public IDisposable RegisterResponder<TRequest, TResponse>(Func<TRequest, TResponse> responder)
        => Track(_hub.RegisterResponder<TRequest, TResponse>(_owner, responder));

    /// <inheritdoc/>
    public bool TryRequest<TRequest, TResponse>(TRequest request, out TResponse? response)
        => _hub.TryRequest<TRequest, TResponse>(_owner, request, out response);

    /// <summary>Disposes every handle this plugin created (host teardown).</summary>
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

    private IDisposable Track(IDisposable handle)
    {
        lock (_gate)
            _handles.Add(handle);

        return handle;
    }
}
