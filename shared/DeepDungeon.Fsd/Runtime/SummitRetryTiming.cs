namespace DeepDungeon.Fsd.Runtime;

internal sealed class SummitRetryTiming
{
    private long? _deadSince;
    private long? _movableSince;

    internal bool ObserveDeath(bool dead, long now) => Elapsed(ref _deadSince, dead, now, 30_000);

    internal bool ObserveMovementReady(bool movable, long now) => Elapsed(ref _movableSince, movable, now, 500);

    internal void Reset()
    {
        _deadSince = null;
        _movableSince = null;
    }

    private static bool Elapsed(ref long? since, bool condition, long now, long delay)
    {
        since = condition ? since ?? now : null;
        return since is { } start && now - start >= delay;
    }
}
