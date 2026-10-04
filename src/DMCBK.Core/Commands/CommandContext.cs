using DMCBK.Core.Configuration;
using DMCBK.Core.Presentation;
using Microsoft.Extensions.Logging;
using Umpk.Client;
using Umpk.Commands;
using Umpk.Text;

namespace DMCBK.Core.Commands;

/// <summary>
/// The per-execution context injected into every command (the Brigadier dispatcher's source type, and a UMPK <see cref="ICommandSource"/>).
/// It carries the live client and its game facade, the host output sink and optional UI hooks, the config snapshot, the variable store, the translation service, and the command service (for re-dispatch).
/// It also holds the mutable <see cref="Result"/> the command sets and the cancellation token for the current execution.
/// Replaces the legacy static <c>CmdResult.currentHandler</c> ambient client; no statics, one fresh instance per dispatched line.
/// </summary>
public sealed class CommandContext : ICommandSource
{
    internal CommandContext(
        Client client,
        ICommandOutput output,
        IHostUi? ui,
        DmcbkConfiguration? config,
        VariableStore variables,
        ClientFeatures features,
        ICommandDispatcher commands,
        CancellationToken cancellation)
    {
        Client = client;
        Output = output;
        Ui = ui;
        Config = config;
        Variables = variables;
        Features = features;
        Commands = commands;
        Cancellation = cancellation;
    }

    /// <summary>The live client.</summary>
    public Client Client { get; }

    /// <summary>The live-session game facade (throws when not in a session; guard with <see cref="InSession"/>).</summary>
    public GameApi Game => Client.Game;

    /// <summary>The host output sink for multi-line command bodies.</summary>
    public ICommandOutput Output { get; }

    /// <summary>The optional host UI hooks; null means text-only.</summary>
    public IHostUi? Ui { get; }

    /// <summary>The configuration snapshot, or null when the client was built programmatically without one.</summary>
    public DmcbkConfiguration? Config { get; }

    /// <summary>The <c>%var%</c> store.</summary>
    public VariableStore Variables { get; }

    /// <summary>The effective feature composition (terrain/inventory/entities/physics/pathfinding).</summary>
    public ClientFeatures Features { get; }

    /// <summary>The command service, for re-dispatch.</summary>
    public ICommandDispatcher Commands { get; }

    /// <summary>
    /// The host's diagnostic logger for command internals.
    /// Use it for the technical cause behind a result, never for anything the user is meant to read: the result message is the user-facing channel and it goes through the string corpus, while this one goes to the log (and the log file) at the host's own level filtering.
    /// Debug is the right level for "the operation failed and here is what the library said", which no localized result line has room for.
    /// </summary>
    internal ILogger Logger => Commands.Logger;

    /// <summary>The translation source.</summary>
    public Umpk.Text.ITranslationSource Translations => Client.Translations;

    /// <summary>The cancellation token scoped to this execution.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>The mutable result the command sets; read by the command service after dispatch.</summary>
    public CmdResult Result { get; } = new();

    /// <summary>True when the client is in a live play session.</summary>
    public bool InSession => Client.Status == ClientStatus.Playing;

    /// <summary>True when terrain/movement handling is enabled.</summary>
    public bool TerrainEnabled => Features.Terrain;

    /// <summary>True when inventory handling is enabled.</summary>
    public bool InventoryEnabled => Features.Inventory;

    /// <summary>True when entity handling is enabled.</summary>
    public bool EntityEnabled => Features.Entities;

    /// <summary>
    /// The resolved status-glyph vocabulary (see <see cref="GlyphSet"/>).
    /// Comes from the host, because picking between emoji and ASCII needs the output encoding and the terminal type.
    /// </summary>
    public GlyphSet Glyphs => Commands.Glyphs;

    /// <summary>True when the local physics simulation is enabled.</summary>
    public bool PhysicsEnabled => Features.Physics;

    /// <summary>True when pathfinding/navigation is enabled.</summary>
    public bool PathfindingEnabled => Features.Pathfinding;

    /// <inheritdoc/>
    public ValueTask ReplyAsync(Component message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        Output.WriteLine(message.ToPlainText(Translations));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public T? GetService<T>() where T : class
    {
        object? service = typeof(T) switch
        {
            _ when typeof(T) == typeof(Client) => Client,
            _ when typeof(T) == typeof(ICommandDispatcher) => Commands,
            _ when typeof(T) == typeof(VariableStore) => Variables,
            _ when typeof(T) == typeof(DmcbkConfiguration) => Config,
            _ when typeof(T) == typeof(ITranslationSource) => Translations,
            _ when typeof(T) == typeof(IRegistrySuggestionSource) => Commands.RegistrySuggestions,
            _ => Client.TryGetModule<T>(out T? module) ? module : null,
        };

        return service as T;
    }

    /// <summary>
    /// Blocks on an async facade read/action and returns its result (sync-over-async at the command boundary).
    /// Guarded by <see cref="Client.WaitForCommandWork"/>, which raises <see cref="DmcbkSessionLoopBlockedException"/> rather than hanging when this very call is what is stopping the session loop from advancing.
    /// </summary>
    internal T Run<T>(Func<CancellationToken, Task<T>> op)
    {
        ArgumentNullException.ThrowIfNull(op);
        Task<T> work = op(Cancellation);
        Client.WaitForCommandWork(work);
        return work.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Blocks on an async facade action (sync-over-async at the command boundary), under the same session-loop guard as the value-returning overload.
    /// </summary>
    internal void Run(Func<CancellationToken, Task> op)
    {
        ArgumentNullException.ThrowIfNull(op);
        Task work = op(Cancellation);
        Client.WaitForCommandWork(work);
    }
}
