using DeepDungeon.Fsd.Core;

namespace NewFolderThree;

internal sealed class RsrMachinistActionControl(Func<uint, bool, bool> sendToggle)
{
    internal const uint SpreadShot = 2870;
    internal const uint Scattergun = 25786;
    internal const uint AutoCrossbow = 16497;
    internal const uint Bioblaster = 16499;
    private const long RetryMilliseconds = 2000;

    private readonly ActionToggle[] actions =
    [
        new(SpreadShot),
        new(Scattergun),
        new(AutoCrossbow),
        new(Bioblaster),
        new(PotdMachinistActionPolicy.BlockedActionId)
    ];

    internal void Update(bool running, uint adjustedSpreadShot, bool restrictRicochet, long now)
    {
        foreach (var action in actions)
        {
            bool suppress = running && (action.Id switch
            {
                SpreadShot or Scattergun when adjustedSpreadShot == 0 => action.RequestedSuppression,
                SpreadShot or Scattergun => action.Id == adjustedSpreadShot,
                PotdMachinistActionPolicy.BlockedActionId => restrictRicochet,
                _ => true
            });
            Reconcile(action, suppress, now);
        }
    }

    internal void Restore(long now)
    {
        foreach (var action in actions)
        {
            action.NextAttempt = 0;
            Reconcile(action, false, now);
        }
    }

    private void Reconcile(ActionToggle action, bool suppress, long now)
    {
        if (action.RequestedSuppression != suppress)
        {
            action.RequestedSuppression = suppress;
            action.NextAttempt = 0;
        }
        if (action.Suppressed == suppress || now < action.NextAttempt)
            return;

        if (sendToggle(action.Id, !suppress))
            action.Suppressed = suppress;
        else
            action.NextAttempt = now + RetryMilliseconds;
    }

    private sealed class ActionToggle(uint id)
    {
        public uint Id { get; } = id;
        public bool RequestedSuppression { get; set; }
        public bool Suppressed { get; set; }
        public long NextAttempt { get; set; }
    }
}
