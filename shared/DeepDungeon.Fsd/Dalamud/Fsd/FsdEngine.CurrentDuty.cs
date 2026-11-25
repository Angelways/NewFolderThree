using Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private (ulong Character, uint Dungeon, int BlockStart)? _pendingCurrentDuty;

    private void DrawCurrentDutyContinuation(bool enabled)
    {
        const string slotPopup = "原存檔槽位##fsf_continue_slot";
        CurrentDutyContinuation? current = null;
        ulong character = Service.PlayerState.ContentId;
        if (enabled && character != 0 && _dutyState is { IsInDuty: true, IsTransitioning: false } duty &&
            !DeepDungeon.Fsd.Dalamud.GameState.DutyState.IsClientTransitioning &&
            CurrentDutyContinuationPolicy.TryCreate(duty.DungeonId, duty.Floor, out var observed, out _))
            current = observed;

        ImGui.BeginDisabled(current == null);
        bool clicked = ImGui.Button("繼續探索##fsf_continue_current");
        ImGui.EndDisabled();
        if (clicked && current is { } continuation)
        {
            if (!continuation.RequiresSaveSlot)
            {
                ContinueCurrentDuty(null);
                return;
            }
            _pendingCurrentDuty = (character, continuation.DungeonId, continuation.BlockStartFloor);
            ImGui.OpenPopup(slotPopup);
        }

        if (!ImGui.BeginPopup(slotPopup)) return;
        try
        {
            if (current is not { RequiresSaveSlot: true } live ||
                _pendingCurrentDuty != (character, live.DungeonId, live.BlockStartFloor))
            {
                ImGui.CloseCurrentPopup();
                return;
            }
            ImGui.TextUnformatted($"請選擇本組入場時使用的存檔。通關後將驗證第 {live.BlockEndFloor + 1} 層。");
            bool first = ImGui.Button("存檔 1");
            ImGui.SameLine();
            bool second = ImGui.Button("存檔 2");
            ImGui.SameLine();
            bool cancel = ImGui.Button("取消");
            if (first || second || cancel)
            {
                ImGui.CloseCurrentPopup();
                if (!cancel) ContinueCurrentDuty(first ? 0 : 1);
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private void ContinueCurrentDuty(int? sourceSlot)
    {
        if (TryContinueCurrentDuty(sourceSlot, out var error))
            _farmingError = null;
        else
        {
            _farmingError = error;
            Service.Log.Warning($"[Necromancer] Current-duty continuation rejected: {error}");
        }
    }

    private bool TryContinueCurrentDuty(int? sourceSlot, out string error)
    {
        _runLogExportNotice = null;
        _runLogExportFailed = false;

        if (_tryAuthorizeFsdStart is not null && !_tryAuthorizeFsdStart(out _))
        {
            error = HostDeniedFsdStartError;
            return false;
        }
        if (!TryValidateRotation(out error))
            return false;
        if (Service.LocalPlayer?.ClassJob.RowId is not (23 or 31 or 38))
        {
            error = "死宮／天宮死靈術士（成就）模式僅支援機工士、吟遊詩人、舞者。";
            return false;
        }
        if (_ddHost?.FsdActive == true)
        {
            error = "FSD 已在運作中，不能重複接續。";
            return false;
        }
        if (_bridgeLeaveDutyContext != null || _bridgeDeleteSaveContext != null || _bridgeDeleteSaveFlow != null)
        {
            error = "目前有其他地宮操作正在進行，請稍後再接續。";
            return false;
        }

        EnsureGeneralAssists();
        if (_dutyState is not { IsInDuty: true } duty)
        {
            error = "目前不在可接續的地宮副本內。";
            return false;
        }
        if (duty.IsTransitioning || DeepDungeon.Fsd.Dalamud.GameState.DutyState.IsClientTransitioning)
        {
            error = "目前仍在過圖，請等待層數穩定後再接續。";
            return false;
        }
        if (!CurrentDutyContinuationPolicy.TryCreate(duty.DungeonId, duty.Floor,
                out var continuation, out error))
            return false;
        if (continuation.RequiresSaveSlot && sourceSlot is not (0 or 1))
        {
            error = "請確認本組入場使用的原存檔槽位。";
            return false;
        }

        if (!_detailedMapCatalogManager.TryAcquireRunSnapshot(
                useDetailedMap: false, scenarioKey: null,
                out DetailedMapRunSnapshot detailedMapSnapshot, out error))
            return false;

        var plan = new FarmingPlan(
            FarmingMode.DeepProgression, SaveUse.Create, continuation.BlockStartFloor,
            TargetCycles: 1, Infinite: false,
            BandedEnabled: false, OpenGold: true, OpenSilver: true, OpenBronze: false)
        {
            DungeonId = continuation.DungeonId,
            ContinuesExistingProgress = true
        };
        var session = new FarmingSession(plan, continuationSlot: sourceSlot);
        bool firstScenario = true;

        try
        {
            _executionLease.Acquire();
            _ddHost ??= new RunHost(
                _configuration, _encounters, _dutyState, _logMessageSource,
                _floorEvidenceObserver, _runTelemetryObserver,
                _spinBossMovementFactory, _movementSpeedProvider);
            _ddHost.StartFsd(
                () =>
                {
                    if (firstScenario)
                    {
                        firstScenario = false;
                        return new FarmingScenario(session, continuation);
                    }
                    return new FarmingScenario(session);
                },
                targetLoops: 1, infinite: false, detailedMapRunSnapshot: detailedMapSnapshot);
        }
        catch (Exception ex)
        {
            _detailedMapCatalogManager.ReleaseRunSnapshot();
            if (_executionLease.IsHeld) _executionLease.Release();
            error = $"現場接續啟動失敗：{ex.Message}";
            Service.Log.Error($"[FsdEngine] Current-duty continuation failed: {ex}");
            return false;
        }

        if (_ddHost.FsdActive)
        {
            UpdateCompanionControls();
            error = string.Empty;
            return true;
        }

        _detailedMapCatalogManager.ReleaseRunSnapshot();
        if (_executionLease.IsHeld) _executionLease.Release();
        error = _farmingError ?? "現場接續未能啟動。";
        return false;
    }
}
