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
    private readonly IDataManager _data;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IObjectTable _objects;
    private readonly IPluginLog _log;
    private Hook<ActionManager.Delegates.UseAction>? _use;
    private Hook<ActionManager.Delegates.UseActionLocation>? _useLocation;
    private volatile uint _jobId;
    private volatile uint _dungeonId;
    private volatile int _floor;
    private volatile bool _useNativeBlock;
    private readonly RsrMachinistActionControl _rsrActions;
    private DateTime _lastReadFailureLogAt = DateTime.MinValue;
    private bool _disposed;

    public PotdMachinistActionBlocker(
        ICommandManager commands,
        IDataManager data,
        IClientState clientState,
        ICondition condition,
        IObjectTable objects,
        IGameInteropProvider interop,
        IPluginLog log)
    {
        _commands = commands;
        _data = data;
        _clientState = clientState;
        _condition = condition;
        _objects = objects;
        _log = log;
        _rsrActions = new(SetRsrActionEnabled);

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
        if (!connected || _clientState.IsPvP ||
            _objects.LocalPlayer?.ClassJob.RowId != PotdMachinistActionPolicy.MachinistJobId)
            return;

        bool running = useRsr && runActive;
        uint spreadShot = 0;
        if (running)
        {
            var manager = ActionManager.Instance();
            if (manager != null)
            {
                uint adjusted = manager->GetAdjustedActionId(RsrMachinistActionControl.SpreadShot);
                if (adjusted is RsrMachinistActionControl.SpreadShot or RsrMachinistActionControl.Scattergun)
                    spreadShot = adjusted;
            }
        }
        _rsrActions.Update(running, spreadShot,
            PotdMachinistActionPolicy.IsRestrictedFloor(_dungeonId, _floor), Environment.TickCount64);
    }

    private bool SetRsrActionEnabled(uint actionId, bool enabled)
    {
        try
        {
            var action = _data.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRowOrDefault(actionId);
            if (action is { } row && _commands.Commands.ContainsKey("/rsr"))
            {
                var name = row.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name) &&
                    _commands.ProcessCommand($"/rsr ToggleActions {name} {(enabled ? "true" : "false")}"))
                    return true;
            }
            _log.Warning("無法送出 RSR 技能切換指令，技能 {ActionId}，啟用 {Enabled}。", actionId, enabled);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "無法送出 RSR 技能切換指令，技能 {ActionId}，啟用 {Enabled}。", actionId, enabled);
        }
        return false;
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
        if (!_clientState.IsPvP)
            _rsrActions.Restore(Environment.TickCount64);
        _useNativeBlock = false;
        _jobId = 0;
        _dungeonId = 0;
        _floor = 0;
        DisposeHooks();
        _disposed = true;
    }
}
