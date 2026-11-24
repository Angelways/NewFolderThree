namespace DeepDungeon.Fsd.Core;

public static class PotdMovementSpeedPolicy
{
    public const uint PalaceOfTheDeadDungeonId = 1;
    public const uint HeavenOnHighDungeonId = 2;
    public const int PalaceOfTheDeadMaximumFloor = 200;
    public const int HeavenOnHighMaximumFloor = 100;

    public static bool IsEligibleFloor(uint dungeonId, int floor) =>
        floor >= 1 && floor <= MaximumFloor(dungeonId) && floor % 10 != 0;

    public static int MaximumFloor(uint dungeonId) => dungeonId switch
    {
        PalaceOfTheDeadDungeonId => PalaceOfTheDeadMaximumFloor,
        HeavenOnHighDungeonId => HeavenOnHighMaximumFloor,
        _ => 0
    };

    public static float ApplySpeedAddition(
        uint dungeonId, int floor, float baseSpeed, float speedAddition, bool adjustmentAllowed)
    {
        if (!adjustmentAllowed || !IsEligibleFloor(dungeonId, floor) ||
            !float.IsFinite(baseSpeed) || !float.IsFinite(speedAddition))
            return baseSpeed;

        return baseSpeed + speedAddition;
    }
}
