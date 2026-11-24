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
