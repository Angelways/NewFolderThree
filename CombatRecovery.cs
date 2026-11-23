using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using GameActionType = FFXIVClientStructs.FFXIV.Client.Game.ActionType;

namespace NewFolderThree;

internal sealed partial class CombatRecovery : IDisposable
{
    private const uint MachinistJobId = 31;
    private const uint SplitShotId = 2866;
    private const uint HeatedSplitShotId = 7411;
    // Action sheet cooldown group 58 is zero-based in ActionManager.
    private const int GlobalRecastGroup = 57;
    private const int ConfirmationTimeoutMilliseconds = 5_000;
    private const int HistoryLimit = 100;

    private readonly IClientState clientState;
    private readonly ICondition conditions;
    private readonly IObjectTable objects;
    private readonly ITargetManager targets;
    private readonly Hook<ActionEffectHandler.Delegates.Receive> effectHook;
    private readonly ConcurrentQueue<ObservedAction> effects = new();
    private readonly CombatTimer timer = new();
    private readonly List<CastEntry> history = [];

    private bool monitoringEnabled;
    private bool wasFsdActive;
    private bool testRequested;
    private uint observedPlayerId;
    private uint territoryId;
    private uint? lastGcdSequence;
    private PendingCast? pendingCast;
    private int castsConfirmed;
    private string monitorStatus = "已停止";
    private string castStatus = "尚未嘗試施放";

    public unsafe CombatRecovery(
        IClientState clientState,
        ICondition conditions,
        IObjectTable objects,
        ITargetManager targets,
        IGameInteropProvider interop)
    {
        this.clientState = clientState;
        this.conditions = conditions;
        this.objects = objects;
        this.targets = targets;

        effectHook = interop.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
            ActionEffectHandler.Addresses.Receive.Value, ReceiveActionEffect);
        try
        {
            effectHook.Enable();
        }
        catch
        {
            effectHook.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref observedPlayerId, 0);
        effectHook.Dispose();
    }

    private unsafe void ReceiveActionEffect(
        uint sourceId,
        Character* source,
        Vector3* position,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* targetEffects,
        GameObjectId* targetIds)
    {
        try
        {
            if (sourceId != 0 && sourceId == Volatile.Read(ref observedPlayerId) &&
                header != null && header->ActionType == (byte)GameActionType.Action)
            {
                effects.Enqueue(new ObservedAction(
                    sourceId, header->ActionId, header->SourceSequence,
                    header->GlobalSequence, Environment.TickCount64));
            }
        }
        finally
        {
            effectHook.Original(sourceId, source, position, header, targetEffects, targetIds);
        }
    }

    public unsafe void Update(bool fsdActive, bool canRecover, bool connected)
    {
        var now = Environment.TickCount64;
        if (fsdActive != wasFsdActive)
            monitoringEnabled = fsdActive;
        wasFsdActive = fsdActive;

        var player = connected && clientState.IsLoggedIn ? objects.LocalPlayer : null;
        var playerId = monitoringEnabled || testRequested || pendingCast.HasValue ? player?.EntityId ?? 0 : 0;
        var currentTerritory = clientState.TerritoryType;
        if (currentTerritory != territoryId || playerId != Volatile.Read(ref observedPlayerId))
        {
            Volatile.Write(ref observedPlayerId, playerId);
            territoryId = currentTerritory;
            timer.UpdateCombat(false, now);
            lastGcdSequence = null;
            pendingCast = null;
            effects.Clear();
        }

        var manager = player != null ? ActionManager.Instance() : null;
        var restriction = GetRestriction(player);
        if (fsdActive && !canRecover && restriction == null)
            restriction = "等待 FSD 允許輸出";
        var inCombat = conditions[ConditionFlag.InCombat];
        timer.UpdateCombat(monitoringEnabled && restriction == null && inCombat, now);

        while (effects.TryDequeue(out var effect))
        {
            if (effect.PlayerId == playerId && manager != null)
                ProcessEffect(manager, effect);
        }

        if (pendingCast is { } pending && now - pending.RequestedAt >= ConfirmationTimeoutMilliseconds)
        {
            pendingCast = null;
            castStatus = $"{CastOrigin(pending.IsTest)}未收到{SkillName(pending.ActionId)}的生效確認";
        }

        monitorStatus = !monitoringEnabled ? "已停止"
            : restriction ?? (inCombat ? "戰鬥中" : "等待進入戰鬥");

        if (testRequested)
        {
            testRequested = false;
            if (pendingCast == null)
            {
                if (timer.CanAttempt(now))
                    timer.Attempted(now);
                TryCast(now, true, player, manager, restriction);
            }
            return;
        }

        if (pendingCast == null && timer.CanAttempt(now))
        {
            timer.Attempted(now);
            TryCast(now, false, player, manager, restriction);
        }
    }

    private string? GetRestriction(IPlayerCharacter? player)
    {
        if (player == null || conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51])
            return "等待角色就緒";
        if (clientState.IsPvP)
            return "僅在 PvE 使用";
        if (player.ClassJob.RowId != MachinistJobId)
            return "請切換至機工士";
        if (player.IsDead)
            return "角色已倒下";
        return null;
    }

    private unsafe void ProcessEffect(ActionManager* manager, ObservedAction effect)
    {
        if (pendingCast is { } pending && effect.ActionId == pending.ActionId &&
            effect.SourceSequence == pending.Sequence && effect.ReceivedAt >= pending.RequestedAt)
        {
            pendingCast = null;
            castsConfirmed++;
            history.Add(new CastEntry(DateTimeOffset.Now, effect.ActionId, pending.IsTest));
            if (history.Count > HistoryLimit)
                history.RemoveAt(0);

            castStatus = $"{CastOrigin(pending.IsTest)}已施放{SkillName(effect.ActionId)}";
            timer.GcdConfirmed(effect.ReceivedAt);
        }

        if (!timer.IsRunning || effect.SourceSequence == 0 || (effect.GlobalSequence != 0 && lastGcdSequence == effect.GlobalSequence))
            return;

        if (manager->GetRecastGroup((int)GameActionType.Action, effect.ActionId) == GlobalRecastGroup ||
            manager->GetAdditionalRecastGroup(GameActionType.Action, effect.ActionId) == GlobalRecastGroup)
        {
            lastGcdSequence = effect.GlobalSequence;
            timer.GcdConfirmed(effect.ReceivedAt);
        }
    }

    private unsafe void TryCast(
        long now, bool isTest, IPlayerCharacter? player, ActionManager* manager, string? restriction)
    {
        var origin = CastOrigin(isTest);
        if (restriction != null || player == null || manager == null)
        {
            castStatus = $"{origin}{restriction ?? "技能系統尚未就緒"}";
            return;
        }

        if (targets.Target is not { IsTargetable: true, IsDead: false } target)
        {
            castStatus = $"{origin}請選擇可攻擊的存活目標";
            return;
        }

        var actionId = manager->GetAdjustedActionId(SplitShotId);
        if (actionId is not (SplitShotId or HeatedSplitShotId))
        {
            castStatus = $"{origin}當前狀態下無法使用分裂彈";
            return;
        }

        var targetObject = (GameObject*)target.Address;
        if (!ActionManager.CanUseActionOnTarget(actionId, targetObject))
        {
            castStatus = $"{origin}當前目標無法使用{SkillName(actionId)}";
            return;
        }

        if (manager->ActionQueued || manager->AnimationLock > 0 || player.IsCasting)
        {
            castStatus = $"{origin}當前正在執行其他技能";
            return;
        }

        var spatialStatus = ActionManager.GetActionInRangeOrLoS(actionId, (GameObject*)player.Address, targetObject);
        if (spatialStatus != 0)
        {
            castStatus = $"{origin}目標距離過遠或視線受阻（{spatialStatus}）";
            return;
        }

        var status = manager->GetActionStatus(GameActionType.Action, actionId, target.GameObjectId);
        if (status != 0)
        {
            castStatus = $"{origin}技能當前不可用（{status}）";
            return;
        }

        // This entry point executes immediately. The regular action queue remains available to the player.
        var position = target.Position;
        var before = manager->LastUsedActionSequence;
        if (!manager->UseActionLocation(GameActionType.Action, actionId, target.GameObjectId, &position) ||
            manager->LastUsedActionSequence == before)
        {
            castStatus = $"{origin}{SkillName(actionId)}未發出";
            return;
        }

        pendingCast = new PendingCast(actionId, manager->LastUsedActionSequence, now, isTest);
        castStatus = $"{origin}已發出{SkillName(actionId)}，等待生效確認";
    }

    private static string SkillName(uint actionId) => actionId == HeatedSplitShotId ? "熱分裂彈" : "分裂彈";
    private static string CastOrigin(bool isTest) => isTest ? "測試 · " : "自動 · ";

    private readonly record struct ObservedAction(
        uint PlayerId, uint ActionId, ushort SourceSequence, uint GlobalSequence, long ReceivedAt);
    private readonly record struct PendingCast(uint ActionId, ushort Sequence, long RequestedAt, bool IsTest);
    private readonly record struct CastEntry(DateTimeOffset Time, uint ActionId, bool IsTest);
}
