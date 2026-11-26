namespace DeepDungeon.Fsd.Runtime;

public static class DeepDungeonMovementPolicy
{
    public static bool IsPlayable(in DeepDungeonStateSnapshot duty) =>
        duty is { IsValid: true, IsInDeepDungeonTerritory: true, IsInDuty: true,
            IsTransitioning: false, DungeonId: > 0, Floor: > 0 };

    public static bool CanPreventKnockback(in DeepDungeonStateSnapshot duty) =>
        IsPlayable(duty) && duty is { DungeonId: 4, Floor: >= 71 and <= 99 };

    public static bool IsWalkingPermission(uint permission) =>
        permission is 96 or 97 or 98 or 99 or 1001 or 1006 or 1007 or 1008;
}
