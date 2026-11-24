using System.Diagnostics;
using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Utilities;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using OmenTools.Interop.Game.Models.Native;

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
    private string _status = "等待移速簽章";
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
        if (!DirectMovementController.TryReadNativeMotion(out var motion))
            motion = default;
        return PotdMovementSpeedPolicy.EstimateNavigationSpeed(
            _currentDungeonId,
            _currentFloor,
            motion.TargetSpeed,
            motion.BaseSpeed,
            GetFloat(ref _speedAdditionBits),
            _speedHookEnabled && _speedAdjustmentAllowed);
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
            _status = "樓層讀取失敗，移速調整維持關閉";
            _log.Warning(ex, "讀取深宮層數失敗，移速調整維持關閉。");
        }

        if (!_isEligibleFloor)
        {
            _speedAdjustmentAllowed = false;
            _speedBlockedByStatus = false;
            _hasOriginalSpeed = false;
            _nextStatusRefreshTimestamp = 0;
            UpdateHookState();
            if (_status != "樓層讀取失敗，移速調整維持關閉")
                _status = DescribeInactiveFloor();
            return;
        }

        if (now >= _nextStatusRefreshTimestamp)
            RefreshRuntimeCache(now);

        UpdateHookState();

        if (_speedHook is null)
            _status = "移速簽章無法使用";
        else if (!_enabled())
            _status = "移速調整未啟用";
        else if (!float.IsFinite(GetFloat(ref _speedAdditionBits)) || GetFloat(ref _speedAdditionBits) == 0f)
            _status = "移速增量為 0";
        else if (_speedBlockedByStatus)
            _status = "目前受限制狀態影響";
        else
            _status = $"{CurrentDungeonName()} {_currentFloor} 層移速調整已啟用";
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
            return string.Empty;
        if (_currentFloor < 1 || _currentFloor > PotdMovementSpeedPolicy.MaximumFloor(_currentDungeonId))
            return $"等待讀取{dungeonName}層數";
        return $"{dungeonName} {_currentFloor} 層不啟用移速調整";
    }

    private string CurrentDungeonName() => _currentDungeonId switch
    {
        PotdMovementSpeedPolicy.PalaceOfTheDeadDungeonId => "死者宮殿",
        PotdMovementSpeedPolicy.HeavenOnHighDungeonId => "天之御柱",
        _ => string.Empty
    };

    private void TryConfigureHooks()
    {
        try
        {
            var speedAddress = _sigScanner.ScanText(SpeedSignature);
            if (speedAddress == nint.Zero)
                throw new InvalidOperationException("移速簽章未比對到位址。");

            _speedHook = _interop.HookFromAddress<SpeedDelegate>(speedAddress, OnSpeed);

            var accelerationAddress = _sigScanner.ScanText(AccelerationSignature);
            if (accelerationAddress != nint.Zero)
            {
                _accelerationHook = _interop.HookFromAddress<AccelerationDelegate>(
                    accelerationAddress,
                    OnAcceleration);
            }

            _status = "移速簽章已比對";
        }
        catch (Exception ex)
        {
            DisposeHooks();
            _status = "移速簽章無法使用，移速調整未啟用";
            _log.Warning(ex, "NewFolderThree 深宮移速簽章未比對成功，移速調整已停用。");
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
        var original = _accelerationHook!.Original;
        if (!_accelerationHookEnabled || a1 == nint.Zero)
        {
            original(a1);
            return;
        }

        const float seedSpeed = 100f;
        var controller = (PlayerMoveControllerWalk*)a1;
        float previousSpeed = controller->CurrentSpeed;
        controller->CurrentSpeed = seedSpeed;
        try
        {
            original(a1);
        }
        finally
        {
            // The native update can exit without assigning a new speed.
            if (controller->CurrentSpeed == seedSpeed)
                controller->CurrentSpeed = previousSpeed;
        }
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
