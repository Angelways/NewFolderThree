using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace NewFolderThree;

internal sealed unsafe class PotdMachinistActionBlocker : IDisposable
{
    private readonly IFramework _framework;
    private readonly ICondition _condition;
    private readonly IObjectTable _objects;
    private readonly IPluginLog _log;
    private Hook<ActionManager.Delegates.UseAction>? _use;
    private Hook<ActionManager.Delegates.UseActionLocation>? _useLocation;
    private volatile uint _jobId;
    private volatile uint _dungeonId;
    private volatile int _floor;
    private DateTime _lastReadFailureLogAt = DateTime.MinValue;
    private bool _disposed;

    public PotdMachinistActionBlocker(
        IFramework framework,
        ICondition condition,
        IObjectTable objects,
        IGameInteropProvider interop,
        IPluginLog log)
    {
        _framework = framework;
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
            _framework.Update += Update;
        }
        catch (Exception ex)
        {
            DisposeHooks();
            _log.Warning(ex, "机工士死者宫殿 2890 技能拦截 Hook 不可用，拦截保持关闭。");
        }
    }

    private void Update(IFramework _)
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
                _log.Warning(ex, "读取死者宫殿楼层失败，2890 技能拦截保持关闭。");
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
        if (type != ActionType.Action)
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
        _framework.Update -= Update;
        _jobId = 0;
        _dungeonId = 0;
        _floor = 0;
        DisposeHooks();
        _disposed = true;
    }
}
