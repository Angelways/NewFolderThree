namespace DeepDungeon.Fsd.Core;

public static class PotdMovementSpeedPolicy
{
    public const uint PalaceOfTheDeadDungeonId = 1;
    public const int MaximumFloor = 200;

    public static bool IsEligibleFloor(uint dungeonId, int floor) =>
        dungeonId == PalaceOfTheDeadDungeonId &&
        floor is >= 1 and <= MaximumFloor &&
        floor % 10 != 0;

    public static float ApplySpeedAddition(
        uint dungeonId, int floor, float baseSpeed, float speedAddition, bool adjustmentAllowed)
    {
        if (!adjustmentAllowed || !IsEligibleFloor(dungeonId, floor) ||
            !float.IsFinite(baseSpeed) || !float.IsFinite(speedAddition))
            return baseSpeed;

        return baseSpeed + speedAddition;
    }
}
