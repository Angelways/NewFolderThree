using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public static class Pt99Platform
{
    public const ushort TerritoryId = 1290;
    public const float CenterX = -600f;
    public const float CenterZ = -300f;
    public const float HalfWidth = 20f;
    public const float HalfHeight = 15f;
    public const float PlanningInset = 1f;
    private const float EdgeClearance = .15f;
    private const float MinimumStopLead = .1f;

    public static bool Contains(Vector2 point, float inset = 0) =>
        MathF.Abs(point.X - CenterX) <= HalfWidth - inset &&
        MathF.Abs(point.Y - CenterZ) <= HalfHeight - inset;

    public static bool AllowsWalk(Vector2 start, Vector2 target, float speed, float frameSeconds = 0)
    {
        if (!Contains(start) || !Contains(target, EdgeClearance) || !float.IsFinite(speed) || speed <= 0)
            return false;

        var offset = target - start;
        if (offset.LengthSquared() <= 1e-6f) return true;

        // Input requests full walking speed even when the waypoint is closer than one frame.
        var projected = start + Vector2.Normalize(offset) * speed * MathF.Max(MinimumStopLead, frameSeconds);
        return Contains(projected, EdgeClearance);
    }
}
