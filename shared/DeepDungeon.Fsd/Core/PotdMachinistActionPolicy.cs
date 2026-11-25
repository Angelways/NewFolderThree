namespace DeepDungeon.Fsd.Core;

public static class PotdMachinistActionPolicy
{
    public const uint MachinistJobId = 31;
    public const uint BlockedActionId = 2890;
    public const int FirstRestrictedFloor = 141;
    public const int PalaceOfTheDeadMaximumFloor = 200;
    public const uint HeavenOnHighDungeonId = 2;
    public const int HeavenOnHighFirstRestrictedFloor = 91;
    public const int HeavenOnHighMaximumFloor = 100;
    public const uint PalaceOfTheDeadDungeonId = 1;

    public static bool ShouldBlock(
        uint jobId, uint dungeonId, int floor, uint requestedActionId, uint adjustedActionId) =>
        (requestedActionId == BlockedActionId || adjustedActionId == BlockedActionId) &&
        jobId == MachinistJobId && IsRestrictedFloor(dungeonId, floor);

    public static bool IsRestrictedFloor(uint dungeonId, int floor) =>
        dungeonId switch
        {
            PalaceOfTheDeadDungeonId =>
                floor is >= FirstRestrictedFloor and <= PalaceOfTheDeadMaximumFloor && floor % 10 != 0,
            HeavenOnHighDungeonId =>
                floor is >= HeavenOnHighFirstRestrictedFloor and < HeavenOnHighMaximumFloor && floor % 10 != 0,
            _ => false
        };
}
