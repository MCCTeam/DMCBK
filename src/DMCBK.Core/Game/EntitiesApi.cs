using Umpk.Client.Actions;
using Umpk.Game.Entities;
using Umpk.Geometry;

namespace DMCBK.Core;

/// <summary>
/// The entity surface: snapshots of tracked entities (all/by-id/by-uuid/of-type/nearby/nearest) with identity, kinematics, pose, custom name, equipment, effects, and the rider graph, plus attack/interact actions.
/// Entity types resolve to real registry identifiers because the client always wires static registries (H1).
/// Requires the Entities feature; disabled features surface <see cref="DmcbkFeatureDisabledException"/>.
/// </summary>
public sealed class EntitiesApi
{
    private readonly GameSession _session;
    private readonly Umpk.Text.ITranslationSource? _translations;

    internal EntitiesApi(GameSession session, Umpk.Text.ITranslationSource? translations)
    {
        _session = session;
        _translations = translations;
    }

    /// <summary>Snapshots every tracked entity.</summary>
    public Task<IReadOnlyList<EntitySnapshot>> AllAsync(CancellationToken ct = default)
        => _session.RunAsync(async client => Project(await client.Snapshots.EntitiesAsync(ct).ConfigureAwait(false)));

    /// <summary>Snapshots the entity with the given network id, or null when untracked.</summary>
    public Task<EntitySnapshot?> ByIdAsync(int entityId, CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            Umpk.Client.Snapshots.EntitySnapshot? entity = await client.Snapshots.EntityByIdAsync(entityId, ct).ConfigureAwait(false);
            return entity is null ? null : Project(entity);
        });

    /// <summary>Snapshots the entity with the given uuid, or null when untracked.</summary>
    public Task<EntitySnapshot?> ByUuidAsync(Guid uuid, CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            Umpk.Client.Snapshots.EntitySnapshot? entity = await client.Snapshots.EntityByUuidAsync(uuid, ct).ConfigureAwait(false);
            return entity is null ? null : Project(entity);
        });

    /// <summary>
    /// Snapshots every tracked entity whose type id matches <paramref name="typeId"/> (a namespaced or bare id, for example <c>minecraft:zombie</c> or <c>zombie</c>).
    /// </summary>
    public Task<IReadOnlyList<EntitySnapshot>> OfTypeAsync(string typeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        return _session.RunAsync(async client => Project(await client.Snapshots.EntitiesOfTypeAsync(typeId, ct).ConfigureAwait(false)));
    }

    /// <summary>Snapshots tracked entities within <paramref name="radius"/> blocks of the player.</summary>
    public Task<IReadOnlyList<EntitySnapshot>> NearbyAsync(double radius, CancellationToken ct = default)
    {
        if (radius < 0)
            throw new ArgumentOutOfRangeException(nameof(radius));

        return _session.RunAsync(async client => Project(await client.Snapshots.EntitiesNearbyAsync(radius, ct).ConfigureAwait(false)));
    }

    /// <summary>
    /// Snapshots the closest tracked entity within <paramref name="radius"/> blocks of the player, or null when none qualify.
    /// </summary>
    public Task<EntitySnapshot?> NearestAsync(double radius, CancellationToken ct = default)
    {
        if (radius < 0)
            throw new ArgumentOutOfRangeException(nameof(radius));

        return _session.RunAsync(async client =>
        {
            Umpk.Client.Snapshots.EntitySnapshot? entity =
                await client.Snapshots.NearestEntityAsync(radius, predicate: null, ct).ConfigureAwait(false);
            return entity is null ? null : Project(entity);
        });
    }

    /// <summary>Attacks (left-clicks) the entity with the given network id.</summary>
    public Task AttackAsync(int entityId, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Interaction.AttackEntityAsync(entityId, ct));

    /// <summary>Interacts (right-clicks) with the entity with the given network id.</summary>
    public Task InteractAsync(int entityId, Hand hand = Hand.Main, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Interaction.InteractEntityAsync(entityId, hand, ct));

    private IReadOnlyList<EntitySnapshot> Project(IReadOnlyList<Umpk.Client.Snapshots.EntitySnapshot> entities)
    {
        var list = new List<EntitySnapshot>(entities.Count);
        foreach (Umpk.Client.Snapshots.EntitySnapshot entity in entities)
            list.Add(Project(entity));

        return list;
    }

    private EntitySnapshot Project(Umpk.Client.Snapshots.EntitySnapshot entity)
    {
        var equipment = new Dictionary<string, ItemStackInfo>();
        foreach (KeyValuePair<EquipmentSlot, Umpk.Game.Items.ItemStack> slot in entity.Equipment)
            equipment[slot.Key.ToString()] = ItemStackInfo.From(slot.Value, _translations);

        var effects = new List<EffectSnapshot>(entity.Effects.Count);
        foreach (Umpk.Client.Snapshots.EffectSnapshot effect in entity.Effects)
        {
            effects.Add(new EffectSnapshot(
                effect.EffectId.ToString(), effect.Amplifier, effect.Level, effect.Duration,
                effect.IsInfinite, effect.IsAmbient, effect.ShowParticles, effect.ShowIcon));
        }

        return new EntitySnapshot(
            entity.Id,
            entity.Uuid,
            entity.TypeId.ToString(),
            entity.Position,
            entity.Velocity,
            entity.Yaw,
            entity.Pitch,
            entity.HeadYaw,
            entity.OnGround,
            entity.Pose.ToString(),
            entity.CustomName?.ToPlainText(_translations),
            entity.PlayerName,
            equipment,
            effects,
            entity.PassengerIds,
            entity.VehicleId,
            entity.CarriedItem is null ? null : ItemStackInfo.From(entity.CarriedItem, _translations));
    }
}

/// <summary>An immutable snapshot of one tracked entity.</summary>
/// <param name="Id">The server-assigned entity id.</param>
/// <param name="Uuid">The entity uuid.</param>
/// <param name="TypeId">The namespaced entity type id (a real identifier, never <c>minecraft:unknown</c> for known types).</param>
/// <param name="Position">The entity position.</param>
/// <param name="Velocity">The entity velocity.</param>
/// <param name="Yaw">Body yaw in degrees.</param>
/// <param name="Pitch">Pitch in degrees.</param>
/// <param name="HeadYaw">Head yaw in degrees.</param>
/// <param name="OnGround">Whether the entity was on the ground per the last update.</param>
/// <param name="Pose">The entity pose name.</param>
/// <param name="CustomName">The rendered custom name, or null.</param>
/// <param name="PlayerName">The player profile name for player entities, else null.</param>
/// <param name="Equipment">Worn/held items keyed by equipment slot name.</param>
/// <param name="Effects">Active status effects on the entity.</param>
/// <param name="PassengerIds">The ids of entities riding this one.</param>
/// <param name="VehicleId">The id of the entity this one rides, or null.</param>
/// <param name="CarriedItem">The stack carried by a dropped-item entity, or null for every other entity and when the stack is not yet known.</param>
public sealed record EntitySnapshot(
    int Id,
    Guid Uuid,
    string TypeId,
    Vec3d Position,
    Vec3d Velocity,
    float Yaw,
    float Pitch,
    float HeadYaw,
    bool OnGround,
    string Pose,
    string? CustomName,
    string? PlayerName,
    IReadOnlyDictionary<string, ItemStackInfo> Equipment,
    IReadOnlyList<EffectSnapshot> Effects,
    IReadOnlyList<int> PassengerIds,
    int? VehicleId,
    ItemStackInfo? CarriedItem = null);

/// <summary>An immutable snapshot of one active status effect.</summary>
/// <param name="EffectId">The namespaced effect id (for example <c>minecraft:speed</c>).</param>
/// <param name="Amplifier">The amplifier (0 = level I).</param>
/// <param name="Level">The 1-based level.</param>
/// <param name="Duration">Remaining duration in ticks (-1 = infinite).</param>
/// <param name="IsInfinite">Whether the effect is infinite.</param>
/// <param name="IsAmbient">Whether the effect is ambient.</param>
/// <param name="ShowParticles">Whether particles are shown.</param>
/// <param name="ShowIcon">Whether the HUD icon is shown.</param>
public sealed record EffectSnapshot(
    string EffectId,
    int Amplifier,
    int Level,
    int Duration,
    bool IsInfinite,
    bool IsAmbient,
    bool ShowParticles,
    bool ShowIcon);
