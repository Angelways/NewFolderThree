using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Runtime;

namespace NewFolderThree;

internal sealed class DeepDungeonMovementController : IDisposable
{
    // Signatures from the local XSZ test projects. The knockback caller passes
    // duration in XMM3 and a one-byte flag as the fifth argument.
    private const string KnockbackSignature =
        "48 8B C4 57 48 81 EC ?? ?? ?? ?? 0F 29 70 ?? 0F 28 C1";
    private const string MovementPermissionSignature =
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 48 89 7C 24 20 41 54 41 56 41 57 48 83 EC 20 33 DB 41 8B E9 48 39 1D ?? ?? ?? ?? 45 8B F0 8B F2 4C 8B E1 74 13 E8 ?? ?? ?? ?? 8B D6 48 8B C8";

    private delegate void KnockbackDelegate(nint actor, float rotation, float distance, float duration,
        byte flags, int kind);
    private delegate byte MovementPermissionDelegate(nint conditions, uint permission, int arg3, int arg4);

    private readonly ICondition _conditions;
    private readonly IObjectTable _objects;
    private readonly IPluginLog _log;
    private readonly Configuration _configuration;
    private readonly Hook<KnockbackDelegate>? _knockback;
    private readonly Hook<MovementPermissionDelegate>? _movementPermission;
    private bool _knockbackFailed;
    private bool _movementPermissionFailed;
    private nint _protectedPlayer;
    private volatile bool _forceMoveActive;

    public DeepDungeonMovementController(ICondition conditions, IObjectTable objects,
        IGameInteropProvider interop, IPluginLog log, Configuration configuration)
    {
        _conditions = conditions;
        _objects = objects;
        _log = log;
        _configuration = configuration;
        _knockback = CreateHook<KnockbackDelegate>(interop, KnockbackSignature, OnKnockback, "防擊退");
        _movementPermission = CreateHook<MovementPermissionDelegate>(interop, MovementPermissionSignature,
            OnMovementPermission, "強制移動");
    }

    public bool KnockbackAvailable => _knockback != null && !_knockbackFailed;
    public bool ForceMoveAvailable => _movementPermission != null && !_movementPermissionFailed;

    public void Update(in DeepDungeonStateSnapshot duty)
    {
        var player = InDeepDungeon ? _objects.LocalPlayer?.Address ?? 0 : 0;
        bool preventKnockback = player != 0 && _configuration.PtKnockbackPreventionEnabled
            && DeepDungeonMovementPolicy.CanPreventKnockback(duty);
        bool forceMove = player != 0 && _configuration.DeepDungeonForceMoveEnabled
            && DeepDungeonMovementPolicy.IsPlayable(duty);

        if (!preventKnockback) Volatile.Write(ref _protectedPlayer, 0);
        if (!forceMove) _forceMoveActive = false;
        bool knockbackActive = SetHookState(_knockback, preventKnockback, "防擊退", ref _knockbackFailed);
        Volatile.Write(ref _protectedPlayer, knockbackActive ? player : 0);
        _forceMoveActive = SetHookState(_movementPermission, forceMove, "強制移動", ref _movementPermissionFailed);
    }

    private bool InDeepDungeon => _conditions[ConditionFlag.InDeepDungeon]
        && !_conditions[ConditionFlag.BetweenAreas] && !_conditions[ConditionFlag.BetweenAreas51];

    private void OnKnockback(nint actor, float rotation, float distance, float duration, byte flags, int kind)
    {
        if (actor != 0 && actor == Volatile.Read(ref _protectedPlayer) && InDeepDungeon)
            distance = 0f;
        _knockback!.Original(actor, rotation, distance, duration, flags, kind);
    }

    private byte OnMovementPermission(nint conditions, uint permission, int arg3, int arg4)
    {
        if (_forceMoveActive && DeepDungeonMovementPolicy.IsWalkingPermission(permission) && InDeepDungeon)
            return 1;
        return _movementPermission!.Original(conditions, permission, arg3, arg4);
    }

    private Hook<T>? CreateHook<T>(IGameInteropProvider interop, string signature, T detour, string name)
        where T : Delegate
    {
        try { return interop.HookFromSignature(signature, detour); }
        catch (Exception ex)
        {
            _log.Warning(ex, "無法初始化深宮移動功能 {Feature}。", name);
            return null;
        }
    }

    private bool SetHookState<T>(Hook<T>? hook, bool enable, string name, ref bool failed) where T : Delegate
    {
        if (hook == null || failed) return false;
        try
        {
            if (enable != hook.IsEnabled)
            {
                if (enable) hook.Enable();
                else hook.Disable();
            }
            return enable;
        }
        catch (Exception ex)
        {
            failed = true;
            _log.Warning(ex, "無法切換深宮移動功能 {Feature}。", name);
            return false;
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref _protectedPlayer, 0);
        _forceMoveActive = false;
        try { _movementPermission?.Dispose(); }
        finally { _knockback?.Dispose(); }
    }
}
