using Umpk.Client.Navigation;
using Umpk.Geometry;
using Umpk.Pathfinding;
using Umpk.Pathfinding.Goals;

namespace DMCBK.Core;

/// <summary>
/// The movement/pathfinding surface: straight-line <see cref="MoveToAsync"/> (Physics feature), planner-driven <see cref="NavigateAsync"/> (Pathfinding feature), current position/rotation, look and rotation setters, sneak/sprint toggles, and arm swing.
/// The move/navigate tasks complete on arrival and fault on failure.
/// Disabled features surface <see cref="MccFeatureDisabledException"/>.
/// </summary>
public sealed class MovementApi
{
    private readonly GameSession _session;

    internal MovementApi(GameSession session) => _session = session;

    /// <summary>Whether pathfinding is available (the Pathfinding feature is enabled).</summary>
    public Task<bool> IsPathfindingAvailableAsync(CancellationToken ct = default)
        => _session.ReadAsync(client => client.State.Features.Pathfinding, ct);

    /// <summary>
    /// Straight-line/step movement to a target under the Physics feature (no planner).
    /// Completes when the player arrives within a block of the target; faults if the move cannot be completed.
    /// </summary>
    public Task MoveToAsync(Vec3d target, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Movement.MoveToAsync(target, ct));

    /// <summary>
    /// <c>MoveToAsync</c>, verified against the player's own position afterwards instead of treating the completed move task as an arrival; see <c>Navigator.MoveToVerifiedAsync</c>.
    /// </summary>
    public Task<MoveResult> MoveToVerifiedAsync(Vec3d target, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Movement.MoveToVerifiedAsync(target, ct));

    /// <summary>
    /// <see cref="MoveToVerifiedAsync(Vec3d, CancellationToken)"/> with the planner's fall-damage guards lifted when <paramref name="allowUnsafe"/> is true, which is what the <c>move</c> command's <c>-f</c> flag asks for.
    /// </summary>
    /// <remarks>
    /// The flag means what it says: a plan the safe limits refused can be one that kills the player on landing.
    /// Nothing here makes a fall survivable, it only stops the planner refusing to consider one.
    /// </remarks>
    public Task<MoveResult> MoveToVerifiedAsync(Vec3d target, bool allowUnsafe, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Movement.MoveToVerifiedAsync(
            target,
            allowUnsafe ? PathfinderOptions.UnsafeFalls : PathfinderOptions.Default,
            ct));

    /// <summary>Pathfinding navigation to a goal (Pathfinding feature). Completes on arrival, faults on failure.</summary>
    public Task NavigateAsync(IGoal goal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(goal);
        return _session.RunAsync(client => client.Actions.Movement.NavigateAsync(goal, ct));
    }

    /// <summary>
    /// Planner navigation to a goal, verified against <paramref name="target"/> afterwards.
    /// With <paramref name="allowUnsafe"/> the planner's fall-damage guards are lifted (see <see cref="PathfinderOptions.UnsafeFalls"/>): a plan the safe limits refused can be one that kills the player on landing.
    /// Nothing makes a fall survivable, it only stops the planner refusing to consider one.
    /// </summary>
    public Task<MoveResult> NavigateVerifiedAsync(
        IGoal goal, Vec3d target, bool allowUnsafe = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(goal);
        return _session.RunAsync(client => client.Actions.Movement.NavigateVerifiedAsync(
            goal,
            target,
            allowUnsafe ? PathfinderOptions.UnsafeFalls : PathfinderOptions.Default,
            ct));
    }

    /// <summary>Pathfinding convenience: navigate to within <paramref name="range"/> blocks of a target position.</summary>
    public Task NavigateToAsync(BlockPos target, int range = 1, CancellationToken ct = default)
        => NavigateAsync(new GoalNear(target.X, target.Y, target.Z, Math.Max(0, range)), ct);

    /// <summary>The player's current position and rotation.</summary>
    public Task<PlayerPose> GetPoseAsync(CancellationToken ct = default)
        => _session.ReadAsync(client =>
        {
            var self = client.State.Self;
            return new PlayerPose(self.Position, self.Yaw, self.Pitch, self.OnGround, self.Sneaking, self.Sprinting);
        }, ct);

    /// <summary>
    /// Leaves the bed the player is sleeping in.
    /// The underlying player-command action is bound on every supported protocol (1.8 through 26.2), so there is no era gate; the server ignores it when the player is not sleeping.
    /// </summary>
    public Task LeaveBedAsync(CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Movement.LeaveBedAsync(ct));

    /// <summary>Rotates the player to face a world position and sends the rotation.</summary>
    public Task LookAtAsync(Vec3d target, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Movement.LookAtAsync(target, ct));

    /// <summary>Sets the player's yaw/pitch and sends a rotation update.</summary>
    public Task SetRotationAsync(float yaw, float pitch, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Movement.SetRotationAsync(yaw, pitch, ct));

    /// <summary>Sets yaw/pitch without sending a standalone packet; the next tick carries it.</summary>
    public Task SetRotation(float yaw, float pitch, CancellationToken ct = default)
        => _session.RunAsync(client =>
        {
            client.Actions.Movement.SetRotation(yaw, pitch);
            return Task.CompletedTask;
        });

    /// <summary>Rotates to face a world position without sending a standalone packet.</summary>
    public Task LookAt(Vec3d target, CancellationToken ct = default)
        => _session.RunAsync(client =>
        {
            Vec3d eye = client.State.Self.Position.Add(0, 1.62, 0);
            Vec3d delta = target.Subtract(eye);
            double h = Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
            float yaw = (float)(Math.Atan2(-delta.X, delta.Z) * 180.0 / Math.PI);
            float pitch = (float)(-Math.Atan2(delta.Y, h) * 180.0 / Math.PI);
            client.Actions.Movement.SetRotation(yaw, pitch);
            return Task.CompletedTask;
        });

    /// <summary>Toggles the sneak input state and notifies the server.</summary>
    public Task SetSneakingAsync(bool sneaking, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Movement.SetSneakingAsync(sneaking, ct));

    /// <summary>Toggles the sprint input state and notifies the server.</summary>
    public Task SetSprintingAsync(bool sprinting, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Movement.SetSprintingAsync(sprinting, ct));

    /// <summary>Swings the main arm.</summary>
    public Task SwingAsync(CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Movement.SwingArmAsync(ct));

    /// <summary>Swings the main or off hand (off-hand routes through the interaction swing action).</summary>
    public Task SwingAsync(bool mainHand, CancellationToken ct = default)
        => _session.RunAsync(client => mainHand
            ? client.Actions.Movement.SwingArmAsync(ct)
            : client.Actions.Interaction.SwingAsync(Umpk.Client.Actions.Hand.Off, ct));
}

/// <summary>The player's current position and rotation.</summary>
/// <param name="Position">The feet position.</param>
/// <param name="Yaw">Yaw in degrees.</param>
/// <param name="Pitch">Pitch in degrees.</param>
/// <param name="OnGround">Whether the player is on the ground.</param>
/// <param name="Sneaking">Whether the sneak input is held.</param>
/// <param name="Sprinting">Whether the sprint input is held.</param>
public sealed record PlayerPose(Vec3d Position, float Yaw, float Pitch, bool OnGround, bool Sneaking, bool Sprinting);
