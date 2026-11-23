using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Extensions;
using OmenTools.Interop.Game.AgentEvent;

namespace NewFolderThree;

internal sealed class ReconnectController : IDisposable
{
    private const int DetectionMilliseconds = 8_000;
    private const int RetryMilliseconds = 5_000;
    private const int TimeoutMilliseconds = 15 * 60_000;
    private const int SettleMilliseconds = 1_000;

    private readonly FsdApplication _application;
    private readonly IClientState _clientState;
    private readonly IPlayerState _playerState;
    private readonly IObjectTable _objects;
    private readonly ICondition _conditions;
    private readonly IGameGui _gameGui;
    private readonly IDataManager _gameData;
    private readonly IPluginLog _log;

    private ulong _character;
    private uint _world;
    private int _logoutObserved;
    private int _logoutCode;
    private bool _sawLobby;
    private bool _disconnected;
    private long _started;
    private long _nextAttempt;
    private long _readySince;
    private uint _readyTerritory;
    private string? _queueText;

    public bool IsPending { get; private set; }
    public string? Status { get; private set; }

    public ReconnectController(FsdApplication application, IClientState clientState,
        IPlayerState playerState, IObjectTable objects, ICondition conditions,
        IGameGui gameGui, IDataManager gameData, IPluginLog log)
    {
        _application = application;
        _clientState = clientState;
        _playerState = playerState;
        _objects = objects;
        _conditions = conditions;
        _gameGui = gameGui;
        _gameData = gameData;
        _log = log;
        _clientState.Logout += OnLogout;
    }

    private void OnLogout(int type, int code)
    {
        Volatile.Write(ref _logoutCode, code);
        Volatile.Write(ref _logoutObserved, 1);
        if (_application.IsRunActive)
            _log.Info($"[FSD.Reconnect] Logout received (type={type}, code={code})");
    }

    public bool Update()
    {
        bool loggedIn = _clientState.IsLoggedIn;
        bool logout = Interlocked.Exchange(ref _logoutObserved, 0) != 0;
        if (!_application.IsRunActive)
        {
            _character = 0;
            _world = 0;
            IsPending = false;
            return loggedIn && !logout;
        }

        long now = Environment.TickCount64;
        if (!IsPending && loggedIn && !logout)
        {
            if (_playerState.IsLoaded && _playerState.ContentId != 0)
            {
                _character = _playerState.ContentId;
                _world = _playerState.CurrentWorld.RowId;
            }
            Status = null;
            return true;
        }

        if (!IsPending)
        {
            if (_character == 0)
            {
                Cancel("斷線前未能確認角色，已停止 FSD。");
                return false;
            }
            _application.SuspendForReconnect();
            IsPending = true;
            _started = now;
            _nextAttempt = now;
            _readySince = 0;
            _sawLobby = false;
            _disconnected = logout && Volatile.Read(ref _logoutCode) >= 1000;
            Status = "角色已離線，正在確認連接狀態。";
        }

        if (!loggedIn)
        {
            _sawLobby = true;
            _readySince = 0;
            if (now >= _nextAttempt)
            {
                _nextAttempt = now + (_disconnected ? RetryMilliseconds : 500);
                UpdateLobby();
            }
        }
        else if (_sawLobby)
        {
            if (_playerState.IsLoaded && _playerState.ContentId != 0 && _playerState.ContentId != _character)
            {
                Cancel("登入角色已變更，已停止 FSD。");
                return true;
            }
            if (_disconnected && IsReady(now))
            {
                IsPending = false;
                Status = "角色已重連，正在恢復中斷的流程。";
                _application.ResumeAfterReconnect();
                _log.Info("[FSD.Reconnect] Same character logged in and loaded; restoring FSD");
                return true;
            }
        }

        if (!_disconnected && now - _started >= DetectionMilliseconds)
            Cancel("角色已退出遊戲，已停止 FSD。");
        else if (now - _started >= TimeoutMilliseconds)
            Cancel("自動重連逾時，已停止 FSD。");
        return false;
    }

    private bool IsReady(long now)
    {
        uint territory = _clientState.TerritoryType;
        if (!_playerState.IsLoaded || _playerState.ContentId != _character || _objects.LocalPlayer == null ||
            territory == 0 || _conditions[ConditionFlag.BetweenAreas] || _conditions[ConditionFlag.BetweenAreas51])
        {
            _readySince = 0;
            return false;
        }
        if (_readySince == 0 || _readyTerritory != territory)
        {
            _readySince = now;
            _readyTerritory = territory;
        }
        return now - _readySince >= SettleMilliseconds;
    }

    private unsafe void UpdateLobby()
    {
        try
        {
            var lobby = AgentLobby.Instance();
            if (lobby == null || lobby->IsLoggedIn || lobby->LogoutShouldCloseGame)
                return;

            if (ReadyAddon("SelectOk") is var dialog && dialog != null)
            {
                if (lobby->QueuePosition > 0 || IsQueueDialog(dialog))
                    return;
                if (dialog->Id == lobby->DialogAddonId || dialog->Id == lobby->DialogAddonId2)
                {
                    _disconnected = true;
                    _nextAttempt = Environment.TickCount64 + RetryMilliseconds;
                    Status = "檢測到遊戲連接中斷，正在嘗試重連。";
                    _log.Info("[FSD.Reconnect] Acknowledging lobby error");
                    dialog->Callback(0);
                }
                return;
            }

            if (!_disconnected || lobby->QueuePosition > 0 || ReadyAddon("SelectYesno") != null ||
                ReadyAddon("NowLoading") != null || ReadyAddon("TitleConnect") != null)
                return;
            if (ReadyAddon("_TitleMenu") != null)
            {
                AgentLobbyEvent.OpenCharacterSelect();
                Status = "正在連接角色選擇畫面。";
                return;
            }
            if (ReadyAddon("_CharaSelectListMenu") == null || lobby->TemporaryLocked)
                return;

            ulong character = _character;
            if (AgentLobbyEvent.SelectCharacter(entry => entry.ContentId == character))
            {
                Status = "正在重新登入斷線前的角色。";
                _log.Info("[FSD.Reconnect] Requested login for the interrupted character");
            }
            else if (_world != 0 && lobby->WorldId != _world && ReadyAddon("_CharaSelectWorldServer") != null)
            {
                AgentLobbyEvent.SelectWorldByID(_world);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FSD.Reconnect] Could not update the login lobby");
        }
    }

    private unsafe AtkUnitBase* ReadyAddon(string name)
    {
        var addon = (AtkUnitBase*)_gameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsVisible && addon->IsFullyLoaded() ? addon : null;
    }

    private unsafe bool IsQueueDialog(AtkUnitBase* addon)
    {
        var prompt = ((AddonSelectOk*)addon)->PromptText;
        if (prompt == null) return true;
        _queueText ??= StripWhitespace(_gameData.GetExcelSheet<Lumina.Excel.Sheets.Error>()
            .GetRow(13206).Unknown0.ExtractText());
        var text = StripWhitespace(prompt->NodeText.ToString());
        return _queueText.Length == 0 || text.Length == 0 || text.Contains(_queueText, StringComparison.Ordinal);
    }

    private static string StripWhitespace(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    public void Cancel(string reason = "已取消自動重連並停止 FSD。")
    {
        IsPending = false;
        Status = reason;
        _log.Info($"[FSD.Reconnect] {reason}");
        _application.Stop();
    }

    public void Dispose() => _clientState.Logout -= OnLogout;
}
