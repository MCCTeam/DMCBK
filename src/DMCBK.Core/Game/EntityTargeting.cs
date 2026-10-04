using Umpk.Game.Players;
using Umpk.Geometry;

namespace DMCBK.Core;

/// <summary>Shared, pure targeting policy used by commands and host entity browsers.</summary>
public static class EntityTargeting
{
    /// <summary>
    /// Returns true when a target's feet position lies in the player's forward-facing hemisphere.
    /// The caller must separately verify line of sight when an action requires visibility.
    /// </summary>
    public static bool IsInFront(PlayerPose self, Vec3d target)
    {
        Vec3d eye = PlayerReach.EyePosition(self.Position);
        Vec3d delta = target.Subtract(eye);
        if (delta.LengthSqr() < 1e-9)
            return true;

        double yawRad = self.Yaw * Math.PI / 180.0;
        double pitchRad = self.Pitch * Math.PI / 180.0;
        double cosPitch = Math.Cos(pitchRad);
        double lookX = -Math.Sin(yawRad) * cosPitch;
        double lookY = -Math.Sin(pitchRad);
        double lookZ = Math.Cos(yawRad) * cosPitch;

        return delta.X * lookX + delta.Y * lookY + delta.Z * lookZ > 0;
    }
}
