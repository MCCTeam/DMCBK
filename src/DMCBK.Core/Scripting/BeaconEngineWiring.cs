using System.Runtime.CompilerServices;

namespace DMCBK.Core.Beacon;

/// <summary>
/// Binds each client's <see cref="IBeaconEngine"/> where plugins can find it: the engine is created lazily by the <c>/scripts</c> runtime (one per client), while plugins activate at client start, so neither side can hand the reference over directly.
/// Entries hold the client weakly: a dead client drops its engine with no explicit unbind.
/// </summary>
public static class BeaconEngineWiring
{
    private static readonly ConditionalWeakTable<Client, EngineHolder> Wired = new();

    /// <summary>Binds <paramref name="engine"/> as the Beacon engine for <paramref name="client"/>.</summary>
    public static void Bind(Client client, IBeaconEngine engine)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(engine);
        Wired.AddOrUpdate(client, new EngineHolder(engine));
    }

    /// <summary>Resolves the bound engine, or null when <c>/scripts</c> never ran here.</summary>
    public static IBeaconEngine? Resolve(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return Wired.TryGetValue(client, out EngineHolder? holder) ? holder.Engine : null;
    }

    private sealed class EngineHolder(IBeaconEngine engine)
    {
        public IBeaconEngine Engine { get; } = engine;
    }
}
