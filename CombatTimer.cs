using DeepDungeon.Fsd.Runtime;

namespace NewFolderThree;

internal sealed class CombatTimer
{
    internal const long IdleMilliseconds = 10_000;
    private const long RetryMilliseconds = 2_000;

    private long? lastActivity;
    private long nextAttempt;

    internal bool IsRunning => lastActivity.HasValue;

    internal void UpdateCombat(bool inCombat, long now)
    {
        if (!inCombat)
        {
            lastActivity = null;
            nextAttempt = 0;
        }
        else
        {
            lastActivity ??= now;
        }
    }

    internal void GcdConfirmed(long now)
    {
        if (lastActivity.HasValue)
        {
            lastActivity = Math.Max(lastActivity.Value, now);
            nextAttempt = 0;
        }
    }

    internal long RemainingMilliseconds(long now)
        => lastActivity is long start ? Math.Max(0, start + IdleMilliseconds - now) : IdleMilliseconds;

    internal bool CanAttempt(long now)
        => IsRunning && RemainingMilliseconds(now) == 0 && now >= nextAttempt;

    internal void Attempted(long now) => nextAttempt = now + RetryMilliseconds;
}

internal sealed class BossPullTimer
{
    private const long DelayMilliseconds = 30_000;
    private const long RetryMilliseconds = 1_000;

    private (uint Dungeon, byte Floor)? floor;
    private long nextAttempt;
    private bool handled;

    internal void Update(bool active, in DeepDungeonStateSnapshot duty, bool inCombat, long now)
    {
        if (!active || !duty.IsInDeepDungeonTerritory)
        {
            floor = null;
            return;
        }
        if (!duty.IsValid || duty.IsTransitioning)
            return;

        if (!duty.IsInDuty || duty.DungeonId is not (1 or 2 or 4) || duty.FloorKind != DeepDungeonFloorKind.Boss)
        {
            floor = null;
            return;
        }

        var current = (duty.DungeonId, duty.Floor);
        if (floor != current)
        {
            floor = current;
            handled = false;
            nextAttempt = now + DelayMilliseconds;
        }
        handled |= inCombat;
    }

    internal bool CanAttempt(long now) => floor.HasValue && !handled && now >= nextAttempt;

    internal void Attempted(long now, bool dispatched)
    {
        handled = dispatched;
        nextAttempt = now + RetryMilliseconds;
    }
}
