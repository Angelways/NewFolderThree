namespace DeepDungeon.Fsd.Core;

public static class PotdMachinistActionPolicy
{
    public const uint MachinistJobId = 31;
    public const uint BlockedActionId = 2890;
    public const int FirstRestrictedFloor = 141;
    public const int MaximumFloor = 200;
    public const uint PalaceOfTheDeadDungeonId = 1;

    public static bool ShouldBlock(
        uint jobId, uint dungeonId, int floor, uint requestedActionId, uint adjustedActionId) =>
        (requestedActionId == BlockedActionId || adjustedActionId == BlockedActionId) &&
        jobId == MachinistJobId &&
        dungeonId == PalaceOfTheDeadDungeonId &&
        floor is >= FirstRestrictedFloor and <= MaximumFloor &&
        floor % 10 != 0;
}
