namespace DeepDungeon.Fsd.Core;

/// <summary>Describes how a running deep-dungeon duty is split into a ten-floor attempt.</summary>
public readonly record struct CurrentDutyContinuation(
    uint DungeonId,
    int CurrentFloor,
    int BlockStartFloor,
    int MaxFloor,
    bool IsTenFloorBoundary)
{
    public int BlockEndFloor => Math.Min(BlockStartFloor + 9, MaxFloor);
    public bool IsResultFloor => CurrentFloor == MaxFloor;
    public bool RequiresSaveSlot => BlockEndFloor < MaxFloor;
}

public static class CurrentDutyContinuationPolicy
{
    public static bool TryCreate(uint dungeonId, int currentFloor,
        out CurrentDutyContinuation continuation, out string error)
    {
        continuation = default;
        int maxFloor = dungeonId switch
        {
            1 => 200,
            2 => 100,
            _ => 0
        };
        if (maxFloor == 0)
        {
            error = "目前只支援死者宮殿與天之宮殿的現場接續。";
            return false;
        }
        if (currentFloor < 1 || currentFloor > maxFloor)
        {
            error = $"目前層數 {currentFloor} 不在可接續範圍（1-{maxFloor}）。";
            return false;
        }

        int blockStart = ((currentFloor - 1) / 10) * 10 + 1;
        continuation = new(dungeonId, currentFloor, blockStart, maxFloor,
            currentFloor % 10 == 0 && currentFloor != maxFloor);
        error = string.Empty;
        return true;
    }
}
