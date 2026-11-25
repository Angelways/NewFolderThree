using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace NewFolderThree;

internal sealed unsafe class PotdMachinistActionBlocker : IDisposable
{
    private readonly ICommandManager _commands;
    private readonly ICondition _condition;
    private readonly IObjectTable _objects;
    private readonly IPluginLog _log;
    private Hook<ActionManager.Delegates.UseAction>? _use;
    private Hook<ActionManager.Delegates.UseActionLocation>? _useLocation;
    private volatile uint _jobId;
    private volatile uint _dungeonId;
    private volatile int _floor;
    private volatile bool _useNativeBlock;
    private bool _rsrSuppressed;
    private long _nextRsrAttempt;
    private DateTime _lastReadFailureLogAt = DateTime.MinValue;
    private bool _disposed;

    public PotdMachinistActionBlocker(
        ICommandManager commands,
        ICondition condition,
        IObjectTable objects,
        IGameInteropProvider interop,
        IPluginLog log)
    {
        _commands = commands;
        _condition = condition;
        _objects = objects;
        _log = log;

        try
        {
            _use = interop.HookFromAddress<ActionManager.Delegates.UseAction>(
                (nint)ActionManager.MemberFunctionPointers.UseAction, Use);
            _useLocation = interop.HookFromAddress<ActionManager.Delegates.UseActionLocation>(
                (nint)ActionManager.MemberFunctionPointers.UseActionLocation, UseLocation);
            _use.Enable();
            _useLocation.Enable();
        }
        catch (Exception ex)
        {
            DisposeHooks();
            _log.Warning(ex, "機工士深宮技能 2890 攔截 Hook 無法使用，攔截維持關閉。");
        }
    }

    public void Update(FsdRotationProvider provider, bool runActive, bool connected)
    {
        bool useRsr = provider == FsdRotationProvider.RotationSolverReborn;
        _useNativeBlock = !useRsr;
        ReadFloor();

        // RSR resolves the name against its current job's actions.
        if (!connected || _objects.LocalPlayer?.ClassJob.RowId != PotdMachinistActionPolicy.MachinistJobId)
            return;
        bool suppress = useRsr && runActive && _jobId == PotdMachinistActionPolicy.MachinistJobId &&
            PotdMachinistActionPolicy.IsRestrictedFloor(_dungeonId, _floor);
        if (suppress == _rsrSuppressed)
        {
            _nextRsrAttempt = 0;
            return;
        }

        long now = Environment.TickCount64;
        if (now < _nextRsrAttempt) return;
        _nextRsrAttempt = now + 2000;
        SetRsrSuppression(suppress);
    }

    private void SetRsrSuppression(bool suppress)
    {
        var command = suppress ? "/rsr ToggleActions 弹射 false" : "/rsr ToggleActions 弹射 true";
        try
        {
            if (_commands.Commands.ContainsKey("/rsr") && _commands.ProcessCommand(command))
            {
                _rsrSuppressed = suppress;
                _nextRsrAttempt = 0;
                return;
            }
            _log.Warning("無法送出 RSR 彈射指令：{Command}", command);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "無法送出 RSR 彈射指令：{Command}", command);
        }
    }

    private void ReadFloor()
    {
        uint jobId = _objects.LocalPlayer?.ClassJob.RowId ?? 0;
        if (jobId != PotdMachinistActionPolicy.MachinistJobId ||
            !_condition[ConditionFlag.InDeepDungeon])
        {
            _jobId = 0;
            _dungeonId = 0;
            _floor = 0;
            return;
        }

        try
        {
            var eventFramework = EventFramework.Instance();
            var deepDungeon = eventFramework != null
                ? eventFramework->GetInstanceContentDeepDungeon()
                : null;
            if (deepDungeon == null)
            {
                _jobId = jobId;
                _dungeonId = 0;
                _floor = 0;
                return;
            }

            _jobId = jobId;
            _dungeonId = deepDungeon->DeepDungeonId;
            _floor = deepDungeon->Floor;
        }
        catch (Exception ex)
        {
            _jobId = 0;
            _dungeonId = 0;
            _floor = 0;
            if ((DateTime.UtcNow - _lastReadFailureLogAt).TotalSeconds >= 2)
            {
                _lastReadFailureLogAt = DateTime.UtcNow;
                _log.Warning(ex, "讀取深宮樓層失敗，技能 2890 攔截維持關閉。");
            }
        }
    }

    private bool Use(ActionManager* self, ActionType type, uint id, ulong target, uint extra,
        ActionManager.UseActionMode mode, uint route, bool* area)
    {
        if (ShouldBlock(self, type, id))
            return false;

        return _use!.Original(self, type, id, target, extra, mode, route, area);
    }

    private bool UseLocation(ActionManager* self, ActionType type, uint id, ulong target,
        Vector3* location, uint extra, byte a7)
    {
        if (ShouldBlock(self, type, id))
            return false;

        return _useLocation!.Original(self, type, id, target, location, extra, a7);
    }

    private bool ShouldBlock(ActionManager* self, ActionType type, uint id)
    {
        if (!_useNativeBlock || type != ActionType.Action)
            return false;

        uint adjustedId = id;
        if (self != null && id != 0)
        {
            try { adjustedId = self->GetAdjustedActionId(id); }
            catch { }
        }

        return PotdMachinistActionPolicy.ShouldBlock(
            _jobId, _dungeonId, _floor, id, adjustedId);
    }

    private void DisposeHooks()
    {
        _useLocation?.Dispose();
        _useLocation = null;
        _use?.Dispose();
        _use = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_rsrSuppressed)
            SetRsrSuppression(false);
        _useNativeBlock = false;
        _jobId = 0;
        _dungeonId = 0;
        _floor = 0;
        DisposeHooks();
        _disposed = true;
    }
}
