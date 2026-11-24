using System.Diagnostics;
using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Utilities;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace NewFolderThree;

internal sealed unsafe class DeepDungeonSpeedController : IDisposable
{
    public const string SpeedSignature =
        "40 57 48 83 EC ?? 48 8B F9 48 8B 49 ?? 48 8B 01 FF 90 ?? ?? ?? ?? 48 85 C0 75";

    public const string AccelerationSignature =
        "40 ?? 48 ?? ?? ?? 80 79 ?? ?? 48 ?? ?? 0f 84 ?? ?? ?? ?? 48 89 7c 24 ?? 48 ?? ?? ?? ";

    private static readonly HashSet<uint> MovementSuppressingStatuses =
    [
        14, 67, 181, 240, 436, 484, 502, 623, 674, 709, 1073, 1107, 1114,
        1141, 1147, 1259, 1344, 1394, 1595, 1790, 1796, 1935, 2099, 2158,
        2391, 2551, 2662, 2731, 3167, 3284, 3472, 3473, 3548, 3943, 3948,
        4334, 4341,
    ];

    private delegate float SpeedDelegate(nint a1);
    private delegate void AccelerationDelegate(nint a1);

    private readonly IFramework _framework;
    private readonly ICondition _condition;
    private readonly IObjectTable _objectTable;
    private readonly IGameInteropProvider _interop;
    private readonly ISigScanner _sigScanner;
    private readonly IPluginLog _log;
    private readonly Func<bool> _enabled;
    private readonly Func<float> _speedAddition;
    private readonly Func<bool> _maxAcceleration;
    private Hook<SpeedDelegate>? _speedHook;
    private Hook<AccelerationDelegate>? _accelerationHook;
    private volatile bool _isEligibleFloor;
    private volatile bool _speedAdjustmentAllowed;
    private volatile bool _speedBlockedByStatus;
    private int _currentFloor;
    private uint _currentDungeonId;
    private string _status = "等待速度签名";
    private bool _speedHookEnabled;
    private bool _accelerationHookEnabled;
    private long _nextStatusRefreshTimestamp;
    private int _speedAdditionBits;
    private int _originalSpeedBits;
    private volatile bool _hasOriginalSpeed;
    private bool _disposed;

    private static long MillisecondsToTicks(int milliseconds) =>
        Math.Max(1L, Stopwatch.Frequency * milliseconds / 1000L);

    public DeepDungeonSpeedController(
        IFramework framework,
        ICondition condition,
        IObjectTable objectTable,
        IGameInteropProvider interop,
        ISigScanner sigScanner,
        IPluginLog log,
        Func<bool> enabled,
        Func<float> speedAddition,
        Func<bool> maxAcceleration)
    {
        _framework = framework;
        _condition = condition;
        _objectTable = objectTable;
        _interop = interop;
        _sigScanner = sigScanner;
        _log = log;
        _enabled = enabled;
        _speedAddition = speedAddition;
        _maxAcceleration = maxAcceleration;

        TryConfigureHooks();
        _framework.Update += Update;
    }

    public string Status
    {
        get
        {
            if (!_hasOriginalSpeed)
                return _status;

            var original = BitConverter.Int32BitsToSingle(
                Volatile.Read(ref _originalSpeedBits));
            return $"{_status}；原始移速：{original:F3}";
        }
    }

    public bool IsSpeedHookAvailable => _speedHook is not null;
    public bool IsAccelerationHookAvailable => _accelerationHook is not null;

    public float GetNavigationMovementSpeed()
    {
        float baseSpeed = DirectMovementController.TryReadNativeMotion(out var motion)
            ? motion.TargetSpeed > 0 ? motion.TargetSpeed : motion.BaseSpeed > 0 ? motion.BaseSpeed : 6f
            : 6f;
        return PotdMovementSpeedPolicy.ApplySpeedAddition(
            _currentDungeonId,
            _currentFloor,
            baseSpeed,
            GetFloat(ref _speedAdditionBits),
            _speedAdjustmentAllowed);
    }

    private void Update(IFramework _)
    {
        var now = Stopwatch.GetTimestamp();
        try
        {
            ReadCurrentFloor(out uint dungeonId, out int floor);
            _currentDungeonId = dungeonId;
            _currentFloor = floor;
            _isEligibleFloor = PotdMovementSpeedPolicy.IsEligibleFloor(dungeonId, floor);
        }
        catch (Exception ex)
        {
            _currentDungeonId = 0;
            _currentFloor = 0;
            _isEligibleFloor = false;
            _status = "楼层读取失败，移速修改保持关闭";
            _log.Warning(ex, "读取死者宫殿层数失败，移速修改保持关闭。");
        }

        if (!_isEligibleFloor)
        {
            _speedAdjustmentAllowed = false;
            _speedBlockedByStatus = false;
            _hasOriginalSpeed = false;
            _nextStatusRefreshTimestamp = 0;
            UpdateHookState();
            if (_status != "楼层读取失败，移速修改保持关闭")
                _status = DescribeInactiveFloor();
            return;
        }

        if (now >= _nextStatusRefreshTimestamp)
            RefreshRuntimeCache(now);

        UpdateHookState();

        if (_speedHook is null)
            _status = "速度签名不可用";
        else if (!_enabled())
            _status = "移速调整未启用";
        else if (!float.IsFinite(GetFloat(ref _speedAdditionBits)) || GetFloat(ref _speedAdditionBits) == 0f)
            _status = "移速增加值为 0";
        else if (_speedBlockedByStatus)
            _status = "当前受限制状态影响";
        else
            _status = $"{CurrentDungeonName()} {_currentFloor} 层移速 Hook 已启用";
    }

    private unsafe void ReadCurrentFloor(out uint dungeonId, out int floor)
    {
        dungeonId = 0;
        floor = 0;
        if (!_condition[ConditionFlag.InDeepDungeon])
            return;

        var eventFramework = EventFramework.Instance();
        var deepDungeon = eventFramework != null
            ? eventFramework->GetInstanceContentDeepDungeon()
            : null;
        if (deepDungeon == null)
            return;

        dungeonId = deepDungeon->DeepDungeonId;
        floor = deepDungeon->Floor;
    }

    private string DescribeInactiveFloor()
    {
        string dungeonName = CurrentDungeonName();
        if (dungeonName.Length == 0)
            return "仅限死者宫殿和天之宫殿";
        if (_currentFloor < 1 || _currentFloor > PotdMovementSpeedPolicy.MaximumFloor(_currentDungeonId))
            return $"等待读取{dungeonName}层数";
        return $"{dungeonName} {_currentFloor} 层不启用移速修改";
    }

    private string CurrentDungeonName() => _currentDungeonId switch
    {
        PotdMovementSpeedPolicy.PalaceOfTheDeadDungeonId => "死者宫殿",
        PotdMovementSpeedPolicy.HeavenOnHighDungeonId => "天之宫殿",
        _ => string.Empty
    };

    private void TryConfigureHooks()
    {
        try
        {
            var speedAddress = _sigScanner.ScanText(SpeedSignature);
            if (speedAddress == nint.Zero)
                throw new InvalidOperationException("速度签名未匹配到地址。");

            _speedHook = _interop.HookFromAddress<SpeedDelegate>(speedAddress, OnSpeed);

            var accelerationAddress = _sigScanner.ScanText(AccelerationSignature);
            if (accelerationAddress != nint.Zero)
            {
                _accelerationHook = _interop.HookFromAddress<AccelerationDelegate>(
                    accelerationAddress,
                    OnAcceleration);
            }

            _status = "速度签名已匹配";
        }
        catch (Exception ex)
        {
            DisposeHooks();
            _status = "速度签名不可用，移速 Hook 未启用";
            _log.Warning(ex, "NewFloder 深宫移速签名未匹配，移速调整已禁用。");
        }
    }

    private float OnSpeed(nint a1)
    {
        var original = _speedHook!.Original(a1);
        if (!_speedAdjustmentAllowed)
            return original;

        Interlocked.Exchange(ref _originalSpeedBits, BitConverter.SingleToInt32Bits(original));
        _hasOriginalSpeed = true;
        return original + GetFloat(ref _speedAdditionBits);
    }

    private void OnAcceleration(nint a1)
    {
        if (_accelerationHookEnabled && a1 != nint.Zero)
            *(float*)(a1 + 0x44) = 100f;

        _accelerationHook!.Original(a1);
    }

    private void RefreshRuntimeCache(long now)
    {
        _nextStatusRefreshTimestamp = now + MillisecondsToTicks(100);
        var configuredAddition = _speedAddition();
        var addition = float.IsFinite(configuredAddition)
            ? Math.Clamp(configuredAddition, 0f, 5f)
            : 0f;
        var canAdjust = _speedHook is not null && _enabled() && _isEligibleFloor &&
                        addition != 0f;
        Volatile.Write(ref _speedAdditionBits,
            BitConverter.SingleToInt32Bits(addition));
        _speedBlockedByStatus = false;

        if (canAdjust)
        {
            var localPlayer = _objectTable.LocalPlayer;
            if (localPlayer is null)
            {
                canAdjust = false;
            }
            else
            {
                foreach (var statusEntry in localPlayer.StatusList)
                {
                    if (!MovementSuppressingStatuses.Contains(statusEntry.StatusId))
                        continue;

                    _speedBlockedByStatus = true;
                    canAdjust = false;
                    break;
                }
            }
        }

        _speedAdjustmentAllowed = canAdjust;
    }

    private void UpdateHookState()
    {
        var addition = GetFloat(ref _speedAdditionBits);
        var shouldEnableSpeed = _isEligibleFloor && _enabled() &&
                                float.IsFinite(addition) && addition != 0f;
        var shouldEnableAcceleration = _isEligibleFloor && _enabled() && _maxAcceleration();

        if (_speedHook is not null && shouldEnableSpeed != _speedHookEnabled)
        {
            if (shouldEnableSpeed)
                _speedHook.Enable();
            else
                _speedHook.Disable();
            _speedHookEnabled = shouldEnableSpeed;
        }

        if (_accelerationHook is not null && shouldEnableAcceleration != _accelerationHookEnabled)
        {
            if (shouldEnableAcceleration)
                _accelerationHook.Enable();
            else
                _accelerationHook.Disable();
            _accelerationHookEnabled = shouldEnableAcceleration;
        }
    }

    private static float GetFloat(ref int bits) =>
        BitConverter.Int32BitsToSingle(Volatile.Read(ref bits));

    private void DisposeHooks()
    {
        _speedHook?.Disable();
        _speedHook?.Dispose();
        _speedHook = null;
        _speedHookEnabled = false;
        _accelerationHook?.Disable();
        _accelerationHook?.Dispose();
        _accelerationHook = null;
        _accelerationHookEnabled = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _framework.Update -= Update;
        _isEligibleFloor = false;
        _speedAdjustmentAllowed = false;
        DisposeHooks();
        _disposed = true;
    }
}
