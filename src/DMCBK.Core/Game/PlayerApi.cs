using Umpk.Client.Actions;
using Umpk.Client.Snapshots;

namespace DMCBK.Core;

/// <summary>
/// The self/player surface: vitals (health/food/xp/gamemode), abilities (flying/mayfly, with a fly setter), active effects, respawn, the tab list, scoreboard objectives and teams, boss bars, advancements, and the held item.
/// Self state and the always-present player-list/scoreboard/boss-bar/advancement models never require a gameplay feature; the held item requires the Inventory feature (null when it is disabled).
/// </summary>
public sealed class PlayerApi
{
    private readonly GameSession _session;
    private readonly Umpk.Text.ITranslationSource? _translations;

    internal PlayerApi(GameSession session, Umpk.Text.ITranslationSource? translations)
    {
        _session = session;
        _translations = translations;
    }

    /// <summary>
    /// Reads the player's vitals and game mode.
    /// Check <see cref="PlayerStatus.HasSpawned"/> before trusting <see cref="PlayerStatus.Position"/> or <see cref="PlayerStatus.GameMode"/>: until the server has placed the player, those two are the tracker's DEFAULTS (origin, survival) and not a reading.
    /// See <see cref="WaitForSpawnAsync"/>.
    /// </summary>
    public Task<PlayerStatus> GetStatusAsync(CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            SelfSnapshot self = await client.Snapshots.SelfAsync(ct).ConfigureAwait(false);
            return new PlayerStatus(
                self.Health, self.Food, self.Saturation,
                self.ExperienceLevel, self.ExperienceProgress, self.TotalExperience,
                self.GameMode.ToString(), self.IsSpectator, self.Position, self.OnGround, self.HasSpawned);
        });

    /// <summary>
    /// Waits until the server has placed the player in the world for this session: the join packet applied and the initial position teleport received.
    /// Returns true when that happened, and false when the session ended first or none was live.
    ///
    /// <para>
    /// An embedding host needs this because <see cref="Client.StartAsync"/> returns as soon as the login phase completes, which is before the play-phase join packet and the initial teleport have been applied.
    /// A read taken at that instant returns the default tracker state, which looks exactly like a reading.
    /// Wait for the initial state before reading position or mode.
    /// </para>
    /// </summary>
    /// <param name="ct">A cancellation token; supply a timeout through it rather than waiting forever.</param>
    public async Task<bool> WaitForSpawnAsync(CancellationToken ct = default)
    {
        Umpk.Client.UmpkClient? client = _session.Current;
        if (client is null)
            return false;

        return await client.Spawned.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads the player's server-advertised abilities.</summary>
    public Task<PlayerAbilities> GetAbilitiesAsync(CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            AbilitiesSnapshot abilities = await client.Snapshots.AbilitiesAsync(ct).ConfigureAwait(false);
            return new PlayerAbilities(
                abilities.Flying, abilities.MayFly, abilities.Invulnerable, abilities.InstantBuild,
                abilities.FlyingSpeed, abilities.WalkingSpeed);
        });

    /// <summary>Toggles the flying ability and notifies the server.</summary>
    public Task SetFlyingAsync(bool flying, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Session.SetFlyingAsync(flying, ct));

    /// <summary>
    /// Requests a respawn after death and reports what the server is known to have done about it, instead of reporting the completed send as a respawn; see <see cref="SessionActions.RespawnAsync"/>.
    /// </summary>
    public Task<RespawnOutcome> RespawnAsync(CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Session.RespawnAsync(ct));

    /// <summary>Reads the player's active status effects.</summary>
    public Task<IReadOnlyList<EffectSnapshot>> GetEffectsAsync(CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            IReadOnlyList<Umpk.Client.Snapshots.EffectSnapshot> effects =
                await client.Snapshots.EffectsAsync(ct).ConfigureAwait(false);
            var list = new List<EffectSnapshot>(effects.Count);
            foreach (Umpk.Client.Snapshots.EffectSnapshot effect in effects)
            {
                list.Add(new EffectSnapshot(
                    effect.EffectId.ToString(), effect.Amplifier, effect.Level, effect.Duration,
                    effect.IsInfinite, effect.IsAmbient, effect.ShowParticles, effect.ShowIcon));
            }

            return (IReadOnlyList<EffectSnapshot>)list;
        });

    /// <summary>The stack in the player's selected hotbar slot; null when the Inventory feature is disabled.</summary>
    public Task<ItemStackInfo?> GetHeldItemAsync(CancellationToken ct = default)
        => _session.RunAsync<ItemStackInfo?>(async client =>
        {
            if (!client.State.Features.Inventory)
                return null;

            Umpk.Game.Items.ItemStack stack = await client.Snapshots.HeldItemAsync(ct).ConfigureAwait(false);
            return ItemStackInfo.From(stack, _translations);
        });

    /// <summary>Snapshots the tab (player) list, including header and footer.</summary>
    public Task<TabListSnapshot> GetTabListAsync(CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            Umpk.Client.Snapshots.TabListSnapshot list = await client.Snapshots.TabListAsync(ct).ConfigureAwait(false);
            var entries = new List<TabListEntryInfo>(list.Entries.Count);
            foreach (Umpk.Client.Snapshots.TabListEntrySnapshot entry in list.Entries)
            {
                entries.Add(new TabListEntryInfo(
                    entry.Uuid, entry.Name, entry.GameMode.ToString(), entry.Latency,
                    entry.DisplayName?.ToPlainText(_translations), entry.Listed, entry.ListOrder));
            }

            return new TabListSnapshot(
                entries, list.Header?.ToPlainText(_translations), list.Footer?.ToPlainText(_translations));
        });

    /// <summary>Snapshots the scoreboard objectives and teams.</summary>
    public Task<ScoreboardSnapshot> GetScoreboardAsync(CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            Umpk.Client.Snapshots.ScoreboardSnapshot board = await client.Snapshots.ScoreboardAsync(ct).ConfigureAwait(false);
            var objectives = new List<ObjectiveInfo>(board.Objectives.Count);
            foreach (Umpk.Client.Snapshots.ObjectiveSnapshot objective in board.Objectives)
            {
                objectives.Add(new ObjectiveInfo(
                    objective.Name, objective.DisplayName.ToPlainText(_translations), objective.RenderType.ToString(),
                    objective.Scores));
            }

            var teams = new List<TeamInfo>(board.Teams.Count);
            foreach (Umpk.Client.Snapshots.TeamSnapshot team in board.Teams)
            {
                teams.Add(new TeamInfo(
                    team.Name, team.DisplayName.ToPlainText(_translations),
                    team.Prefix.ToPlainText(_translations), team.Suffix.ToPlainText(_translations),
                    team.Color, team.Members));
            }

            return new ScoreboardSnapshot(objectives, teams);
        });

    /// <summary>Snapshots the active boss bars.</summary>
    public Task<IReadOnlyList<BossBarSnapshot>> GetBossBarsAsync(CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            IReadOnlyList<Umpk.Client.Snapshots.BossBarSnapshot> bars =
                await client.Snapshots.BossBarsAsync(ct).ConfigureAwait(false);
            var list = new List<BossBarSnapshot>(bars.Count);
            foreach (Umpk.Client.Snapshots.BossBarSnapshot bar in bars)
            {
                list.Add(new BossBarSnapshot(
                    bar.Uuid, bar.Title.ToPlainText(_translations), bar.Progress,
                    bar.Color.ToString(), bar.Overlay.ToString(), bar.Flags.ToString()));
            }

            return (IReadOnlyList<BossBarSnapshot>)list;
        });

    /// <summary>
    /// Snapshots the advancements.
    /// Structured advancement contents decode only on protocols 770-773 and 26.2; on other bands UMPK relays the packet verbatim and the advancement set stays empty (hole H14).
    /// </summary>
    public Task<AdvancementsSnapshot> GetAdvancementsAsync(CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            Umpk.Client.Snapshots.AdvancementsSnapshot state =
                await client.Snapshots.AdvancementsAsync(ct).ConfigureAwait(false);
            var entries = new List<AdvancementInfo>(state.Entries.Count);
            foreach (Umpk.Client.Snapshots.AdvancementSnapshot advancement in state.Entries)
            {
                entries.Add(new AdvancementInfo(
                    advancement.Id.ToString(),
                    advancement.ParentId?.ToString(),
                    advancement.Title?.ToPlainText(_translations),
                    advancement.Description?.ToPlainText(_translations),
                    advancement.Frame,
                    advancement.CriteriaCount,
                    advancement.CriteriaCompleted));
            }

            int protocol = client.Session?.Version.Version.Protocol ?? 0;
            return new AdvancementsSnapshot(
                state.SelectedTab?.ToString(),
                entries,
                AdvancementPresentation.StructuredDataAvailable(protocol));
        });
}

/// <summary>The player's vitals and game mode.</summary>
/// <param name="Health">Current health.</param>
/// <param name="Food">Current food level.</param>
/// <param name="Saturation">Current food saturation.</param>
/// <param name="ExperienceLevel">Current experience level.</param>
/// <param name="ExperienceProgress">Progress across the current level, 0 to 1.</param>
/// <param name="TotalExperience">Total accumulated experience.</param>
/// <param name="GameMode">The game mode name; the DEFAULT (survival) until <paramref name="HasSpawned"/>.</param>
/// <param name="IsSpectator">Whether the game mode is spectator.</param>
/// <param name="Position">The player's feet position; the DEFAULT (origin) until <paramref name="HasSpawned"/>.</param>
/// <param name="OnGround">Whether the last movement step ended on the ground.</param>
/// <param name="HasSpawned">
/// Whether the server has actually placed the player in the world for this session (the join packet applied and the initial position teleport received).
/// FALSE means <paramref name="Position"/> and <paramref name="GameMode"/> are placeholders, not readings: the client cannot know either until the server says so, and a host that reports them anyway is publishing numbers it made up.
/// Await <see cref="PlayerApi.WaitForSpawnAsync"/> first.
/// </param>
public sealed record PlayerStatus(
    float Health,
    int Food,
    float Saturation,
    int ExperienceLevel,
    float ExperienceProgress,
    int TotalExperience,
    string GameMode,
    bool IsSpectator,
    Umpk.Geometry.Vec3d Position,
    bool OnGround,
    bool HasSpawned = false);

/// <summary>The player's server-advertised abilities.</summary>
public sealed record PlayerAbilities(
    bool Flying, bool MayFly, bool Invulnerable, bool InstantBuild, float FlyingSpeed, float WalkingSpeed);

/// <summary>A tab-list snapshot with header and footer.</summary>
public sealed record TabListSnapshot(IReadOnlyList<TabListEntryInfo> Entries, string? Header, string? Footer);

/// <summary>One tab-list entry.</summary>
public sealed record TabListEntryInfo(
    Guid Uuid, string Name, string GameMode, int Latency, string? DisplayName, bool Listed, int ListOrder);

/// <summary>A scoreboard snapshot.</summary>
public sealed record ScoreboardSnapshot(IReadOnlyList<ObjectiveInfo> Objectives, IReadOnlyList<TeamInfo> Teams);

/// <summary>One scoreboard objective and its scores.</summary>
public sealed record ObjectiveInfo(string Name, string DisplayName, string RenderType, IReadOnlyDictionary<string, int> Scores);

/// <summary>One scoreboard team.</summary>
public sealed record TeamInfo(
    string Name, string DisplayName, string Prefix, string Suffix, int Color, IReadOnlyList<string> Members);

/// <summary>One boss bar.</summary>
public sealed record BossBarSnapshot(Guid Uuid, string Title, float Progress, string Color, string Overlay, string Flags);

/// <summary>An advancements snapshot.</summary>
public sealed record AdvancementsSnapshot(
    string? SelectedTab,
    IReadOnlyList<AdvancementInfo> Entries,
    bool StructuredDataAvailable = true);

/// <summary>One advancement and its completion progress.</summary>
/// <param name="Id">The advancement id.</param>
/// <param name="ParentId">The parent advancement id, or null for a root.</param>
/// <param name="Title">The advancement title, or null when the advancement is not displayed.</param>
/// <param name="Description">The advancement description, or null when the advancement is not displayed.</param>
/// <param name="Frame">
/// The frame type (task = 0, challenge = 1, goal = 2), or null when the advancement is not displayed.
/// Nullable because the frame rides inside the optional display block, which vanilla omits for every generated recipe advancement, so a null here means "the server sent no frame" and not "task"; see <c>Umpk.Client.Snapshots.AdvancementSnapshot.Frame</c> for the vanilla citations.
/// </param>
/// <param name="CriteriaCount">The total number of criteria.</param>
/// <param name="CriteriaCompleted">The number of criteria obtained so far.</param>
public sealed record AdvancementInfo(
    string Id,
    string? ParentId,
    string? Title,
    string? Description,
    int? Frame,
    int CriteriaCount,
    int CriteriaCompleted);
