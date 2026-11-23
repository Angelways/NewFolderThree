using Dalamud.Game.ClientState.Conditions;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.moveHelper;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using OmenTools.Extensions;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry;

internal sealed class DungeonEntranceTravel(DungeonData dungeon, RunContext context) : IDisposable
{
    private const uint MountRoulette = 9;
    private const long MovementReadyDelayMs = 500;
    private const long TravelTimeoutMs = 180_000;
    private const long TeleportTimeoutMs = 45_000;
    private const long ActionRetryMs = 1_500;

    private readonly NavigationHelper _navigation = new(context.Navigator);
    private readonly long _startedAt = Environment.TickCount64;
    private long? _teleportRequestedAt;
    private long? _movableSince;
    private long _nextActionAt;
    private bool _loggedRoute;

    public bool Update()
    {
        long now = Environment.TickCount64;
        if (now - _startedAt >= TravelTimeoutMs)
            return Fail("前往入口逾時。");

        var condition = Service.Condition;
        if (Service.LocalPlayer is not { } player || DutyState.IsClientTransitioning)
        {
            Suspend();
            return false;
        }

        if (condition.IsBoundByDuty)
            return Fail("其他副本或排隊中，無法前往地宮入口。");

        if (Service.ClientState.TerritoryType != dungeon.TerritoryIdOutside)
        {
            Suspend();
            return UpdateTeleport(now);
        }

        bool mounted = condition[ConditionFlag.Mounted];
        bool atEntrance = NavigationRange.Point.HasArrived(player.Position, dungeon.EntrancePosition)
            && NpcInteractionGuard.FindByBaseId(dungeon.NpcDataId) is { IsTargetable: true } npc
            && NpcInteractionGuard.InteractionRange.HasArrived(player.Position, npc.Position);
        if (atEntrance && !mounted && !condition.IsCasting && !condition[ConditionFlag.Mounting])
        {
            _navigation.Cancel();
            Service.Log.Info($"[DungeonTravel] Arrived: dungeon={dungeon.DungeonId}, territory={dungeon.TerritoryIdOutside}, position={player.Position}");
            return true;
        }

        if (!CanTravel() || (atEntrance || !mounted) && condition[ConditionFlag.Jumping])
        {
            Suspend();
            context.StatusLine = "等待角色可移動。";
            return false;
        }

        _movableSince ??= now;
        if (now - _movableSince.Value < MovementReadyDelayMs)
            return false;

        if (atEntrance)
        {
            _navigation.Cancel();
            context.StatusLine = "已到達入口，正在下坐騎。";
            if (now >= _nextActionAt)
            {
                _nextActionAt = now + ActionRetryMs;
                MountCommand.Dismount();
            }
            return false;
        }

        if (!VNav.NavmeshReady())
        {
            context.StatusLine = "等待地宮入口導航網格。";
            return false;
        }

        bool mountRequired = _teleportRequestedAt.HasValue ||
            NavigationRange.Point.Distance(player.Position, dungeon.EntrancePosition) > NpcInteractionGuard.MaxInteractDistance;
        if (!mounted && mountRequired)
        {
            _navigation.Cancel();
            context.StatusLine = "正在使用隨機坐騎。";
            if (now >= _nextActionAt)
            {
                _nextActionAt = now + ActionRetryMs;
                TryMount();
            }
            return false;
        }

        if (!_loggedRoute)
        {
            _loggedRoute = true;
            Service.Log.Info($"[DungeonTravel] Route: dungeon={dungeon.DungeonId}, territory={dungeon.TerritoryIdOutside}, destination={dungeon.EntrancePosition}, mounted={mounted}, teleported={_teleportRequestedAt.HasValue}");
        }
        var state = _navigation.Navigate(dungeon.EntrancePosition, player.Position);
        context.StatusLine = $"前往地宮入口：{state}";
        if (state == NavigationState.StuckGiveUp)
            return Fail($"無法抵達入口座標 {dungeon.EntrancePosition}。");
        if (state == NavigationState.Arrived)
            context.StatusLine = "已到達入口座標，等待可互動的 NPC。";
        return false;
    }

    private unsafe bool UpdateTeleport(long now)
    {
        context.StatusLine = "等待傳送至地宮入口地圖。";
        if (_teleportRequestedAt is { } requestedAt)
            return now - requestedAt >= TeleportTimeoutMs
                ? Fail($"傳送後未到達地圖 {dungeon.TerritoryIdOutside}。")
                : false;

        if (now < _nextActionAt || !CanTravel())
            return false;
        _nextActionAt = now + ActionRetryMs;
        var telepo = Telepo.Instance();
        if (telepo == null)
            return false;

        telepo->UpdateAetheryteList();
        foreach (var destination in telepo->TeleportList.AsSpan())
        {
            if (destination.AetheryteId != dungeon.AetheryteId || destination.SubIndex != 0)
                continue;
            if (telepo->Teleport(destination.AetheryteId, destination.SubIndex))
            {
                _teleportRequestedAt = now;
                Service.Log.Info($"[DungeonTravel] Teleport requested: dungeon={dungeon.DungeonId}, aetheryte={dungeon.AetheryteId}, territory={dungeon.TerritoryIdOutside}");
            }
            return false;
        }
        return Fail($"尚未解鎖入口以太之光 {dungeon.AetheryteId}。");
    }

    private static bool CanTravel()
    {
        var condition = Service.Condition;
        return !condition.IsOccupiedInEvent && !condition.IsCasting && !condition[ConditionFlag.InCombat]
            && !condition[ConditionFlag.Mounting71];
    }

    private static unsafe void TryMount()
    {
        var actions = ActionManager.Instance();
        if (actions != null && actions->GetActionStatus(ActionType.GeneralAction, MountRoulette) == 0)
            actions->UseAction(ActionType.GeneralAction, MountRoulette);
    }

    private bool Fail(string status)
    {
        _navigation.Cancel();
        context.StatusLine = status;
        context.StatusIsError = true;
        Service.Log.Warning($"[DungeonTravel] {status}");
        return false;
    }

    public void Suspend()
    {
        _movableSince = null;
        _navigation.Cancel();
    }

    public void Dispose() => Suspend();
}
