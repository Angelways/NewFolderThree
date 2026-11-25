using System.Numerics;
using global::Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private string? _farmingError;
    private const string SaveConfirmationTitle = "確認啟動 FSD";
    private const string SaveConfirmationPopup = SaveConfirmationTitle + "###fsd_save_confirmation";
    private const string ResumableSaveNotice =
        "當前地宮存在可以自動繼續的存檔，\n請移步「目前可以自動繼續的存檔」標籤頁查看。";
    private const string NewSaveWarning =
        "如果您確定要開啟新的存檔，請長按下方的確認按鈕，\n此操作可能導致你的存檔消失。";
    private const string ConfirmNewSaveLabel = "確認（長按 5 秒）";
    private const long SaveConfirmationHoldMs = 5000;
    private (ulong Character, FsdFarmingRequest Request)? _pendingFarmingStart;
    private long? _saveConfirmationHoldStartedAt;
    private int _saveConfirmationHoldFrame;
    private static readonly string[] FarmingModeLabels =
        ["挖寶藏", "強化／陶片", "寶藏發現成就", "死靈術士（成就）", "普通登頂"];
    private static readonly string[] ProgressionModeLabels = ["死靈術士（成就）", "普通登頂"];
    private static readonly string[] HoardFloorsetLabels = ["21–30 層", "31–40 層"];
    private static readonly string[] FarmingDungeonLabels = ["朝聖交錯路", "死者宮殿", "天之御柱"];
    private static readonly string[] FullSaveDeletionSlotLabels = ["第 1 槽", "第 2 槽"];

    private bool IsFixedFloorsetHoard => _configuration.Farming.DungeonId == 4 &&
        _fsfScenarioIndex is 0 or 1 &&
        _configuration.Farming.Mode == FarmingMode.Hoard &&
        _configuration.Farming.GetPreferences(_configuration).BandedEnabled == true;

    private string? GetSelectedDetailedMapScenarioKey() =>
        IsFixedFloorsetHoard
            ? DetailedMapCatalogManager.GetScenarioKey(_fsfScenarioIndex)
            : null;

    private void DrawFarmingSelection(bool running)
    {
        var settings = _configuration.Farming;
        int dungeon = settings.DungeonId switch
        {
            1 => 1,
            2 => 2,
            _ => 0
        };
        ImGui.BeginDisabled(running);
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("地宮##fsdDungeon", ref dungeon, FarmingDungeonLabels, FarmingDungeonLabels.Length))
        {
            settings.DungeonId = dungeon switch
            {
                1 => 1u,
                2 => 2u,
                _ => 4u
            };
            if (settings.DungeonId != 4)
            {
                _fsfScenarioIndex = 1;
                _configuration.NecromancerFsdScenarioIndex = 1;
            }
            _configuration.Save();
            _farmingError = null;
        }
        ImGui.EndDisabled();

        bool pt = settings.DungeonId == 4;
        var effectiveMode = settings.GetMode(settings.DungeonId);
        int mode = pt ? (int)effectiveMode : (int)effectiveMode - (int)FarmingMode.DeepProgression;
        var modeLabels = pt ? FarmingModeLabels : ProgressionModeLabels;
        ImGui.BeginDisabled(running);
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("模式##fsdMode", ref mode, modeLabels, modeLabels.Length))
        {
            effectiveMode = (FarmingMode)(mode + (pt ? 0 : (int)FarmingMode.DeepProgression));
            settings.SetMode(settings.DungeonId, effectiveMode);
            _configuration.Save();
            _farmingError = null;
        }
        ImGui.EndDisabled();

        ImGui.BeginDisabled(running);
        if (effectiveMode == FarmingMode.Hoard)
        {
            int floor = _fsfScenarioIndex;
            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo("起始層段##fsdFloorset", ref floor, HoardFloorsetLabels, 2))
            {
                _fsfScenarioIndex = floor;
                _configuration.NecromancerFsdScenarioIndex = floor;
                _configuration.Save();
            }
        }
        else if (effectiveMode == FarmingMode.Aetherpool)
            ImGui.TextWrapped("準備1份較多跳層松杜香的任意起點層存檔");
        else if (effectiveMode == FarmingMode.HoardDiscovery)
            ImGui.TextWrapped("準備1份有至少1個感知寶藏的任意起點層存檔");
        else if (effectiveMode == FarmingMode.NormalSummit)
            ImGui.TextUnformatted($"第 {FarmingPlan.SummitStartFloor(settings.DungeonId)} 層開始，直到登頂");
        ImGui.EndDisabled();
    }

    private void DrawFarmingOptions()
    {
        ImGui.Separator();
        ImGui.Text("FSD 選項");
        ImGui.Indent();
        bool running = _ddHost?.FsdActive == true;
        var activePlan = running ? _ddHost?.Context?.FarmingPlan : null;
        var mode = activePlan?.Mode ?? _configuration.Farming.GetMode(_configuration.Farming.DungeonId);
        bool continuing = activePlan?.ContinuesExistingProgress == true;
        var preferences = _configuration.Farming.GetPreferences(_configuration, mode);
        var options = running ? _ddHost?.RunOptionsProvider?.Current : null;
        bool hoard = options?.BandedEnabled ?? (preferences.BandedEnabled == true);
        bool gold = options?.OpenGold ?? preferences.OpenGold;
        bool silver = options?.OpenSilver ?? preferences.OpenSilver;
        bool bronze = options?.OpenBronze ?? preferences.OpenBronze;
        bool changed;
        ImGui.BeginDisabled(ProgressionPauseLocked);
        ImGui.BeginDisabled(running);
        bool autoDeleteSave = _configuration.Farming.AutoDeleteSaveWhenFull;
        if (ImGui.Checkbox("存檔已滿時自動清理", ref autoDeleteSave))
        {
            _configuration.Farming.AutoDeleteSaveWhenFull = autoDeleteSave;
            _configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("新建存檔時，若兩槽皆有存檔，清理所選槽位後繼續。");
        ImGui.SameLine();
        ImGui.BeginDisabled(!autoDeleteSave);
        int deletionSlot = _configuration.Farming.FullSaveDeletionSlot is 0 ? 0 : 1;
        ImGui.SetNextItemWidth(100);
        if (ImGui.Combo("##fullSaveDeletionSlot", ref deletionSlot, FullSaveDeletionSlotLabels, 2))
        {
            _configuration.Farming.FullSaveDeletionSlot = deletionSlot;
            _configuration.Save();
        }
        ImGui.EndDisabled();
        if (mode == FarmingMode.NormalSummit)
        {
            bool restart = activePlan?.RestartSummitAfterFailure ?? _configuration.Farming.RestartSummitAfterFailure;
            if (ImGui.Checkbox("失敗後重新開始", ref restart))
            {
                _configuration.Farming.RestartSummitAfterFailure = restart;
                _configuration.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("角色持續倒下 30 秒後退本，退本後可移動滿 0.5 秒才開始重試。");
        }
        ImGui.EndDisabled();
        ImGui.BeginDisabled(FarmingTargetPolicy.LocksHoard(mode));
        changed = ImGui.Checkbox("探索埋藏的寶藏", ref hoard);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(FarmingTargetPolicy.LocksGold(mode));
        changed |= ImGui.Checkbox("開啟金寶箱", ref gold);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(FarmingTargetPolicy.LocksSilverBronze(mode) ||
            mode == FarmingMode.Aetherpool && silver && !bronze);
        changed |= ImGui.Checkbox("開啟銀寶箱", ref silver);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(FarmingTargetPolicy.LocksSilverBronze(mode) ||
            mode == FarmingMode.Aetherpool && bronze && !silver);
        changed |= ImGui.Checkbox("開啟銅寶箱", ref bronze);
        ImGui.EndDisabled();
        if (changed)
        {
            if (!continuing)
            {
                preferences.BandedEnabled = hoard;
                preferences.OpenGold = gold; preferences.OpenSilver = silver; preferences.OpenBronze = bronze;
                _configuration.Save();
            }
            _ddHost?.SetFarmingTargets(hoard, gold, silver, bronze);
            _farmingError = null;
        }
        ImGui.BeginDisabled(mode == FarmingMode.HoardDiscovery);
        bool aggressiveChestInteraction = _configuration.AggressiveChestInteraction;
        if (ImGui.Checkbox("我開箱會卡住很久##aggressiveChestInteraction", ref aggressiveChestInteraction))
        {
            _configuration.AggressiveChestInteraction = aggressiveChestInteraction;
            _configuration.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32f);
            ImGui.TextUnformatted("啟用該項目會增加開箱的力度, 如幀數較低或戰鬥時很難成功開箱, 請啟用以增加對輸出或其他外掛搶佔成功率. 若沒有遇到這個問題, 請不要啟用");
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        ImGui.EndDisabled();
        ImGui.Spacing();
        ImGui.BeginDisabled(continuing);
        DrawCycleOptions(preferences);
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        if (running)
        {
            if (ProgressionPauseLocked && _ddHost is { } active && !string.IsNullOrEmpty(active.CurrentStatus))
                ImGui.TextWrapped(active.CurrentStatus);
            ImGui.TextDisabled($"完成循環：{_ddHost!.CompletedLoops}/{(_ddHost.Infinite ? "∞" : _ddHost.TargetLoops)}");
            if (FarmingPlan.IsProgressionMode(mode))
                ImGui.TextDisabled($"失敗重試：{_ddHost.FarmingFailures}");
            if (mode == FarmingMode.HoardDiscovery)
                ImGui.TextDisabled($"寶藏發現：{_ddHost.HoardDiscoveries}");
            if (_ddHost.Context?.FarmingPlan?.SaveUse == SaveUse.Prepared)
                ImGui.TextDisabled(_ddHost.PreparedSaveDescription);
        }
        ImGui.Unindent();
        string? error = _farmingError ?? (_ddHost is { FsdActive: false, LastStatusIsError: true }
            ? _ddHost.LastStatus : null);
        if (!string.IsNullOrEmpty(error))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.55f, 0.55f, 1f));
            ImGui.TextWrapped(error);
            ImGui.PopStyleColor();
        }
    }

    private void DrawCycleOptions(FarmingPreferences preferences)
    {
        ImGui.Text("結束 FSD...");
        bool infinite = _ddHost?.FsdActive == true ? _ddHost.Infinite : preferences.Infinite;
        int cycles = _ddHost?.FsdActive == true ? _ddHost.TargetLoops : preferences.Cycles;
        bool changed = ImGui.Checkbox("無限循環##fsf_inf", ref infinite);
        ImGui.SameLine();
        ImGui.BeginDisabled(infinite);
        ImGui.SetNextItemWidth(90);
        changed |= ImGui.InputInt("循環次數##fsf_loops", ref cycles);
        ImGui.EndDisabled();
        if (changed)
        {
            cycles = Math.Max(1, cycles);
            preferences.Cycles = cycles;
            preferences.Infinite = infinite;
            _configuration.Save();
            _ddHost?.SetCycleTargets(cycles, infinite);
        }
    }

    public FsdControlResult StartSelectedFarming(FsdStartRequest request)
    {
        if (request.Scenario == FsdStartScenario.ControlledSurvey)
            return new FsdControlResult(false, "妖宮受控採集已移除。");
        bool started = TryStartFarming(CreateSelectedFarmingRequest(request), out string error);
        return new FsdControlResult(started, error);
    }

    private FsdFarmingRequest CreateSelectedFarmingRequest(FsdStartRequest request)
    {
        var settings = _configuration.Farming;
        uint dungeonId = settings.DungeonId is 1 or 2 ? settings.DungeonId : 4u;
        var effectiveMode = settings.GetMode(dungeonId);
        var p = settings.GetPreferences(_configuration, effectiveMode);
        bool prepared = effectiveMode is FarmingMode.Aetherpool or FarmingMode.HoardDiscovery;
        int startFloor = effectiveMode switch
        {
            FarmingMode.DeepProgression => 1,
            FarmingMode.NormalSummit => FarmingPlan.SummitStartFloor(dungeonId),
            _ => _fsfScenarioIndex == 0 ? 21 : 31
        };
        return new FsdFarmingRequest(effectiveMode, prepared ? SaveUse.Prepared : SaveUse.Create,
            startFloor, p.Cycles, p.Infinite,
            new FarmingTargets(p.BandedEnabled == true, p.OpenGold, p.OpenSilver, p.OpenBronze),
            request, DungeonId: dungeonId);
    }

    private void OnFarmingStartClicked()
    {
        var request = CreateSelectedFarmingRequest(new FsdStartRequest());
        ulong character = Service.PlayerState.ContentId;
        if (character == 0 || !HasResumableSave(character, request.DungeonId))
        {
            StartFarmingFromUi(request);
            return;
        }

        _pendingFarmingStart = (character, request);
        _saveConfirmationHoldStartedAt = null;
        ImGui.OpenPopup(SaveConfirmationPopup);
    }

    private void DrawFarmingSaveConfirmation(bool startDisabled)
    {
        if (!ImGui.IsPopupOpen(SaveConfirmationPopup))
        {
            _pendingFarmingStart = null;
            _saveConfirmationHoldStartedAt = null;
            return;
        }

        float font = ImGui.GetFontSize();
        var padding = new Vector2(font * 1.3f, font);
        var viewport = ImGui.GetMainViewport();
        float width = Math.Clamp(viewport.WorkSize.X - 4 * padding.X, 14 * font, 35 * font);
        var center = viewport.WorkPos + viewport.WorkSize * 0.5f;
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f));
        float windowWidth = width + 2 * padding.X;
        ImGui.SetNextWindowSizeConstraints(new Vector2(windowWidth, 0), new Vector2(windowWidth, float.MaxValue));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, font * 0.7f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, font * 0.7f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, font * 0.4f);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, 0xff382821);
        ImGui.PushStyleColor(ImGuiCol.Border, 0xff82624b);

        bool visible = ImGui.BeginPopupModal(SaveConfirmationPopup,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove);
        try
        {
            if (!visible)
            {
                _saveConfirmationHoldStartedAt = null;
                return;
            }
            if (_pendingFarmingStart is not { } pending || pending.Character != Service.PlayerState.ContentId)
            {
                CloseFarmingSaveConfirmation();
                return;
            }

            DrawSaveConfirmationNotice(width, font);
            ImGui.Spacing();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextUnformatted(NewSaveWarning);
            ImGui.PopTextWrapPos();
            ImGui.Spacing();

            const string cancelLabel = "取消";
            float cancelWidth = ImGui.CalcTextSize(cancelLabel).X + 2.4f * font;
            float confirmWidth = ImGui.CalcTextSize(ConfirmNewSaveLabel).X + 2.4f * font;
            float gap = font * 0.65f;
            float height = ImGui.GetFrameHeight() + font * 0.8f;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, width - cancelWidth - gap - confirmWidth));
            ImGui.PushStyleColor(ImGuiCol.Button, 0xff47352c);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 0xff5b493b);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, 0xff45392e);
            bool cancelled = ImGui.Button(cancelLabel, new Vector2(cancelWidth, height));
            ImGui.PopStyleColor(3);
            if (cancelled)
            {
                CloseFarmingSaveConfirmation();
                return;
            }

            ImGui.SameLine(0, gap);
            ImGui.BeginDisabled(startDisabled);
            ImGui.PushStyleColor(ImGuiCol.Button, 0xff865831);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 0xffa37144);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, 0xff9b6635);
            ImGui.Button(ConfirmNewSaveLabel, new Vector2(confirmWidth, height));
            bool holding = !startDisabled && ImGui.IsItemActive() && ImGui.IsItemHovered() &&
                ImGui.IsMouseDown(ImGuiMouseButton.Left);
            float progress = UpdateSaveConfirmationHold(holding, ImGui.IsMouseClicked(ImGuiMouseButton.Left),
                ImGui.GetFrameCount(), Environment.TickCount64);
            DrawSaveConfirmationProgress(progress, font);
            ImGui.PopStyleColor(3);
            ImGui.EndDisabled();

            if (progress < 1f)
                return;

            CloseFarmingSaveConfirmation();
            StartFarmingFromUi(pending.Request);
        }
        finally
        {
            if (visible) ImGui.EndPopup();
            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar(5);
        }
    }

    private static void DrawSaveConfirmationNotice(float width, float font)
    {
        var draw = ImGui.GetWindowDrawList();
        Vector2 header = ImGui.GetCursorScreenPos();
        float iconSize = 2 * font;
        float iconFont = font * 1.17f;
        draw.AddRectFilled(header, header + new Vector2(iconSize), ImGui.GetColorU32(0xff734e30), font * 0.45f);
        Vector2 iconTextSize = ImGui.CalcTextSize("i") * (iconFont / font);
        draw.AddText(ImGui.GetFont(), iconFont, header + (new Vector2(iconSize) - iconTextSize) * 0.5f,
            ImGui.GetColorU32(0xfffed6bb), "i");
        draw.AddText(ImGui.GetFont(), font * 1.22f, header + new Vector2(iconSize + font * 0.7f, font * 0.39f),
            ImGui.GetColorU32(ImGuiCol.Text), SaveConfirmationTitle);
        ImGui.Dummy(new Vector2(width, iconSize));
        ImGui.Spacing();

        float inset = font * 0.75f;
        float wrapWidth = width - 2 * inset;
        Vector2 card = ImGui.GetCursorScreenPos();
        float cardHeight = ImGui.CalcTextSize(ResumableSaveNotice, false, wrapWidth).Y + 2 * inset;
        draw.AddRectFilled(card, card + new Vector2(width, cardHeight), ImGui.GetColorU32(0xff4e3728), font * 0.45f);
        ImGui.SetCursorScreenPos(card + new Vector2(inset));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
        ImGui.TextUnformatted(ResumableSaveNotice);
        ImGui.PopTextWrapPos();
        ImGui.SetCursorScreenPos(new Vector2(card.X, card.Y + cardHeight));
        ImGui.Dummy(Vector2.Zero);
    }

    private static void DrawSaveConfirmationProgress(float progress, float font)
    {
        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        float inset = font * 0.22f;
        float height = font * 0.24f;
        var left = new Vector2(min.X + inset, max.Y - inset - height);
        var right = new Vector2(max.X - inset, max.Y - inset);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(left, right, ImGui.GetColorU32(0xff4b3322), height * 0.5f);
        if (progress > 0)
        {
            right.X = left.X + (right.X - left.X) * progress;
            draw.AddRectFilled(left, right, ImGui.GetColorU32(0xffffc791), height * 0.5f);
        }
    }

    private float UpdateSaveConfirmationHold(bool holding, bool pressed, int frame, long now)
    {
        if (!holding || _saveConfirmationHoldFrame != frame - 1)
            _saveConfirmationHoldStartedAt = null;
        if (holding && pressed)
            _saveConfirmationHoldStartedAt = now;
        _saveConfirmationHoldFrame = frame;
        return _saveConfirmationHoldStartedAt is { } startedAt
            ? Math.Clamp((now - startedAt) / (float)SaveConfirmationHoldMs, 0f, 1f)
            : 0f;
    }

    private void CloseFarmingSaveConfirmation()
    {
        _pendingFarmingStart = null;
        _saveConfirmationHoldStartedAt = null;
        ImGui.CloseCurrentPopup();
    }

    private void StartFarmingFromUi(FsdFarmingRequest request)
    {
        try
        {
            bool started = TryStartFarming(request, out string error);
            _farmingError = started ? null : error;
            if (!started)
                Service.Log.Warning($"[Necromancer] FSD start rejected: {error}");
        }
        catch (Exception ex)
        {
            _farmingError = "啟動 FSD 時發生錯誤，請檢查日誌。";
            Service.Log.Error($"[Necromancer] FSD start failed: {ex}");
        }
    }

    public bool TryStartFarming(FsdFarmingRequest request, out string error)
    {
        var (mode, saveUse, startFloor, cycles, infinite, targets, checkpointOptions, dungeonId) = request;
        if (mode == FarmingMode.DeepProgression && dungeonId is 1 or 2 &&
            Service.LocalPlayer?.ClassJob.RowId is not (23 or 31 or 38))
        {
            error = "死宮／天宮死靈術士（成就）模式僅支援機工士、吟遊詩人、舞者。請切換職業後再啟動。";
            return false;
        }
        var (hoard, gold, silver, bronze) = targets;
        var (stopAfterFloor, diagnosticStartFloor, resumeSlot, holdOnFailure, _) = checkpointOptions ?? new FsdStartRequest();
        bool holdAfterClear = checkpointOptions?.HoldAfterClear == true;
        int maxFloor = dungeonId == 1 ? 200 : 100;
        int terminalEntry = maxFloor - 9;
        bool hoardReview = holdAfterClear && dungeonId == 4 && mode == FarmingMode.Hoard &&
            diagnosticStartFloor is 21 or 31 && !stopAfterFloor.HasValue;
        if (stopAfterFloor.HasValue && (!FarmingPlan.IsProgressionMode(mode) ||
            stopAfterFloor.Value < startFloor + 9 || stopAfterFloor.Value > maxFloor || stopAfterFloor.Value % 10 != 0))
        {
            error = $"停止層數只支援深層攻略模式，且必須為 10 至 {maxFloor} 的十層邊界。";
            return false;
        }
        if (!FarmingPlan.TryCreate(mode, saveUse, startFloor, cycles, infinite, hoard, gold, silver, bronze,
                out var plan, out error, dungeonId)) return false;
        if (!hoardReview && (holdOnFailure || resumeSlot.HasValue) && (mode != FarmingMode.DeepProgression ||
            (!stopAfterFloor.HasValue && (!diagnosticStartFloor.HasValue || resumeSlot.HasValue && !holdOnFailure)) ||
            (resumeSlot.HasValue && (resumeSlot.Value < 0 || !diagnosticStartFloor.HasValue))))
        {
            error = "保留現場須指定深層測試起點或停止層；續用槽位須指定起點，並設定停止層或保留失敗現場。";
            return false;
        }
        bool validDiagnosticStart = dungeonId switch
        {
            1 => diagnosticStartFloor is 1 or 51,
            2 => diagnosticStartFloor is 1 or 21,
            _ => diagnosticStartFloor is 1 or 21 or 31 or 51 or 71
        };
        if (!hoardReview && diagnosticStartFloor is { } checkpoint && (mode != FarmingMode.DeepProgression ||
            (resumeSlot.HasValue ? checkpoint < 1 || checkpoint > terminalEntry || checkpoint % 10 != 1
                : !validDiagnosticStart) ||
            stopAfterFloor is { } endFloor && endFloor < checkpoint + 9))
        {
            error = "新建測試起點與地宮設定不符；指定續用槽位須為十層組起點，且停止層數如有指定須為組末或之後。";
            return false;
        }
        plan = plan! with { StopAfterFloor = stopAfterFloor, StartFloor = diagnosticStartFloor ?? plan.StartFloor,
            HoldOnFailure = holdOnFailure,
            HoldAfterClear = holdAfterClear,
            RestartSummitAfterFailure = _configuration.Farming.RestartSummitAfterFailure };
        if (_dutyState is { IsInDuty: true } duty &&
            ((!holdOnFailure && !hoardReview) || !resumeSlot.HasValue || resumeSlot is < 0 or > 1 ||
            duty.IsTransitioning || duty.DungeonId != dungeonId ||
            duty.Floor < plan.StartFloor || duty.Floor >= plan.StartFloor + 10))
        {
            error = "原副本接續只支援保留現場的測試，須指定來源槽位與目前所在層段起點。";
            return false;
        }
        var session = new FarmingSession(plan, resumeSlot ?? -1)
        {
            IsPausedSlotProtected = slot => IsPausedSlotProtected(dungeonId, slot)
        };
        string? key = plan!.ShowsDetailedMap ? startFloor == 21
            ? DetailedMapScenarioCatalog.PilgrimsTraverse21To30.Key
            : DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key : null;
        bool started = TryStartOutsideDutyFsd(() => new FarmingScenario(session), cycles, infinite, key, out error);
        if (started)
        {
            _farmingError = null;
            _configuration.Farming.DungeonId = dungeonId;
            _configuration.Farming.SetMode(dungeonId, mode);
            _fsfScenarioIndex = dungeonId == 4 && mode == FarmingMode.Hoard && startFloor == 21 ? 0 : 1;
            _configuration.NecromancerFsdScenarioIndex = _fsfScenarioIndex;
            var p = _configuration.Farming.GetPreferences(_configuration, mode);
            p.BandedEnabled = hoard; p.OpenGold = gold; p.OpenSilver = silver; p.OpenBronze = bronze;
            p.Cycles = cycles; p.Infinite = infinite;
            _configuration.Save();
        }
        return started;
    }
}
