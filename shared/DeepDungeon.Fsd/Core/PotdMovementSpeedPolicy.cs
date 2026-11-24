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

    public static float EstimateNavigationSpeed(
        uint dungeonId, int floor, float targetSpeed, float baseSpeed,
        float multiplierAddition, bool adjustmentAllowed)
    {
        // The native target speed already includes any active multiplier adjustment.
        if (float.IsFinite(targetSpeed) && targetSpeed > 0f)
            return targetSpeed;

        float fallbackSpeed = float.IsFinite(baseSpeed) && baseSpeed > 0f ? baseSpeed : 6f;
        return adjustmentAllowed && IsEligibleFloor(dungeonId, floor) && float.IsFinite(multiplierAddition)
            ? fallbackSpeed * (1f + multiplierAddition)
            : fallbackSpeed;
    }
}
