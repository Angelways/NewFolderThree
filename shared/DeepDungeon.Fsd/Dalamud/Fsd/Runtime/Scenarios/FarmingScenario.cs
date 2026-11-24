using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

/// <summary>One entry/exit attempt. Session owns continuation, prepared source and climb progress.</summary>
internal sealed class FarmingScenario(FarmingSession session,
    CurrentDutyContinuation? currentDutyContinuation = null) : IScenario
{
    private readonly CurrentDutyContinuation? _currentDutyContinuation = currentDutyContinuation;
    private readonly DungeonData _dungeon = DungeonCatalog.TryGetByDungeonId(session.Plan.DungeonId, out var d)
        ? d
        : DungeonCatalog.PilgrimsTraverse;
    private RunContext? _context;
    private DungeonEntryController? _entry;
    private DungeonEntranceTravel? _entranceTravel;
    private DungeonEntryController? _verification;
    private DungeonExitController? _exit;
    private GenericDeleteSaveFlow? _delete;
    private GenericDeleteSaveFlow? _freeSaveSlot;
    private long _freeSaveSlotStartedAt;
    private bool _creatingSave, _entered, _restDone, _complete, _recoverFailure, _aborted;
    private int _entryFloor;
    internal bool CanRequestPause => _entered && !_complete &&
        session.Plan is { IsProgression: true, HoldAfterClear: false, StopAfterFloor: null } &&
        _context is { Duty.IsInDuty: true, DutyFailureObserved: false, AttemptAborted: false };

    internal void CancelPause()
    {
        if (_complete || session.PersistPause == null) return;
        session.PersistPause = null;
        if (_verification == null) return;
        _verification.Dispose();
        _verification = null;
        if (_context is { } ctx)
            ctx.StatusLine = "已取消暫停，將繼續原流程。";
    }

    public string Name => $"{DungeonShortLabel(session.Plan.DungeonId)} {session.Plan.DisplayName} ({(session.Plan.SaveUse == SaveUse.Prepared ? session.Source.Description : $"{session.NextFloor} 層")})";
    public bool IsComplete => _complete;
    public bool ShouldLoop => !_aborted;
    public bool CountsAsCycle { get; private set; }
    public bool RecoversDutyFailure => _recoverFailure;
    public bool RequiresDutyCompletionEvent => !session.Plan.ReusesSave && !_recoverFailure && !_aborted;

    internal void SuspendForReconnect()
    {
        _entranceTravel?.Dispose();
        _entranceTravel = null;
    }

    // Re-entry keeps the session and verifies the previous save before acting.
    internal bool NeedsReentryAfterReconnect(out string? error)
    {
        error = null;
        if (_context is not { } ctx)
            return false;
        if (ctx.Duty.IsInDuty)
        {
            bool resultRoom = ctx.TerminalRewardsRequired && ctx.Duty.Floor == _entryFloor + 10 &&
                ctx.Duty.CurrentFloorKind == DeepDungeonFloorKind.Result;
            bool sameGroup = !_entered || ctx.Duty.Floor >= _entryFloor && ctx.Duty.Floor < _entryFloor + 10;
            if (ctx.Duty.DungeonId != _dungeon.DutyDungeonId || !sameGroup && !resultRoom)
                error = "重連後所在的副本或層組與中斷時不符，FSD 已停止。";
            return false;
        }
        if (!_entered)
        {
            // Keep any bound slot, refreshing the surrounding UI observations.
            _entry?.Prepare(ctx);
            if (_freeSaveSlot != null)
                _freeSaveSlotStartedAt = Environment.TickCount64;
            return false;
        }
        if (ctx.DutyCompletionObserved || ctx.DutyFailureObserved || ctx.AttemptAborted ||
            session.Plan.ReusesSave && ctx.HarvestComplete)
            return false;
        if (session.OwnedSlot >= 0 || session.Source.Pinned != null)
            return true;
        error = "原副本已結束，無法確認可重新進入的存檔，FSD 已停止。";
        return false;
    }

    public void Initialize(RunContext context)
    {
        _context = context;
        context.ResetAttemptState();
        context.FarmingPlan = session.Plan;
        var plan = session.Plan;
        context.RunOptions.Set(new RunOptions
        {
            OpenGold = session.OpenGold,
            OpenSilver = session.OpenSilver,
            OpenBronze = session.OpenBronze,
            BandedEnabled = session.BandedEnabled,
            HarvestChestsRequired = plan.Mode == FarmingMode.Aetherpool,
            DiscoveryOnly = plan.Mode == FarmingMode.HoardDiscovery,
            LeaveMode = LeaveMode.AfterFinishDungeon,
            RequireValidatedAbandonPrompt = true
        });
        if (_currentDutyContinuation is { } continuation)
        {
            // The player is already inside the duty. Attach to the observed
            // ten-floor block and never open the entry UI or re-enter it.
            _entryFloor = continuation.BlockStartFloor;
            _entered = true;
            context.EntryReadyForDuty = true;
            context.TerminalRewardsRequired =
                _entryFloor == 91 && ResultAltarPolicy.TryGet(_dungeon.DungeonId, 100, out _) ||
                _entryFloor == 191 && Potd200RewardPolicy.IsTerminal(_dungeon.DungeonId, 200);
            context.PreparedSaveDescription = $"現場接續，{continuation.CurrentFloor} 層（{continuation.BlockStartFloor}-{continuation.BlockEndFloor}）";
            context.StatusLine = continuation.IsResultFloor
                ? $"現場接續：第 {continuation.CurrentFloor} 層（終局獎勵流程）。"
                : continuation.IsTenFloorBoundary
                ? $"現場接續：第 {continuation.CurrentFloor} 層（十層首領區塊）。"
                : $"現場接續：第 {continuation.CurrentFloor} 層（{continuation.BlockStartFloor}-{continuation.BlockEndFloor} 層區塊）。";
            Service.Log.Info($"[Farming] Attached to current duty: dungeon={continuation.DungeonId}, floor={continuation.CurrentFloor}, block={continuation.BlockStartFloor}-{continuation.BlockEndFloor}, boundary={continuation.IsTenFloorBoundary}");
        }
        else
        {
            // Entry/attachment is selected from fresh observations in Update.
            // Until then no floor controller may act on this attempt.
            context.EntryReadyForDuty = false;
        }
    }

    public void Update(IFramework framework)
    {
        var ctx = _context;
        if (ctx == null || _complete) return;
        if (ctx.StatusIsError) { _complete = true; return; }
        if (ctx.TerminalReviewRequested)
        {
            _aborted = true;
            _complete = true;
            return;
        }
        if (!_entered && !TryEnterDuty(ctx, framework)) return;
        if (ctx.Duty.StateReadFailed || ctx.Duty.IsTransitioning || DutyState.IsClientTransitioning) return;
        ObserveDutyCompletion(ctx);
        if (session.Plan.HoldAfterClear && ctx.DutyCompletionObserved &&
            !ctx.DutyFailureObserved && !ctx.TerminalRewardsRequired)
        {
            ctx.EntryReadyForDuty = false;
            ctx.Navigator.CancelAll();
            ctx.StatusLine = "王戰已通關；保留副本現場，未退本、未清理存檔。";
            _aborted = true;
            _complete = true;
            Service.Log.Info("[Farming] HoldAfterClear: retaining duty and owned save for exit validation");
            return;
        }
        if (ctx.Duty.IsInDuty)
        {
            if (ctx.AttemptAborted)
            {
                _aborted = true;
                if (ctx.TryHoldFailedAttempt(ctx.AttemptAbortReason)) return;
                RequestLeave();
                return;
            }
            if (ctx.DutyFailureObserved)
            {
                if (ctx.TryHoldFailedAttempt("攻略失敗")) return;
                if (session.Plan.IsProgression)
                {
                    _recoverFailure = true;
                    RequestLeave();
                }
                return;
            }
            if (session.Plan.ReusesSave && (ctx.Duty.IsBossFloor || ctx.Duty.Floor >= 30 || ctx.HarvestComplete))
            {
                if (!ctx.HarvestComplete)
                {
                    _aborted = true;
                    ctx.AttemptAborted = true;
                    ctx.AttemptAbortReason = "已到存檔保護邊界；停止速刷，不計完成輪數。";
                }
                else ctx.HarvestComplete = true;
                RequestLeave();
            }
            return;
        }

        if (ctx.TerminalRewardsRequired && ctx.DutyCompletionObserved && !ctx.DutyFailureObserved)
        {
            ctx.StatusLine = ctx.TerminalRoomObserved
                ? "最終獎勵房尚未完成驗收；FSD 已停止，請檢查終局獎勵紀錄。"
                : "尚未觀測到最終獎勵房；FSD 已停止並保留現場，請檢查終局物件與獎勵。";
            ctx.StatusIsError = true;
            _complete = true;
            return;
        }
        _exit ??= new(returnToEntrance: true, entranceNpcBaseId: _dungeon.NpcDataId);
        if (!_restDone)
        {
            if (!_exit.IsPrepared) _exit.Prepare(ctx);
            if (_exit.Update() != ExitOutcome.Completed) return;
            _restDone = true;
        }
        _aborted |= ctx.AttemptAborted;
        var result = new FarmingAttemptResult(
            _entryFloor, ctx.DutyCompletionObserved, ctx.DutyFailureObserved, ctx.HarvestComplete, _aborted);
        if (FarmingSessionPolicy.ReachedStopBoundary(session.Plan, result))
        {
            _aborted = true; // Stop the host before it can construct the next entry attempt.
            _complete = true;
            ctx.StatusLine = $"已通過 {session.Plan.StopAfterFloor} 層；停止並保留存檔，等待驗收。";
            Service.Log.Info($"[Farming] Progression stop boundary reached: cleared={_entryFloor + 9}, next={_entryFloor + 10}, ownedSlot={session.OwnedSlot}");
            return;
        }
        var decision = FarmingSessionPolicy.Decide(session.Plan, result);
        bool retainedUnidentifiedTerminalSave = _currentDutyContinuation is not null &&
            session.OwnedSlot < 0 && decision.DeleteOwnedSave && result.DutyCompleted &&
            !result.DutyFailed && !result.Aborted;
        if (retainedUnidentifiedTerminalSave)
            decision = decision with { DeleteOwnedSave = false };
        else if (_currentDutyContinuation is not null && session.OwnedSlot < 0 && decision.RecoverFailure)
        {
            decision = new(false, session.NextFloor, false, false,
                "接續區塊未正常通關；沒有可安全驗證的原存檔槽位，FSD 已停止並保留現場。");
        }
        if (session.PersistPause is { } persistPause)
        {
            if (!result.DutyCompleted || result.DutyFailed || result.Aborted || decision.Error.Length > 0)
            {
                FinishPause(ctx, "本組未正常通關，沒有可續跑的存檔。", error: true);
                return;
            }
            if (!decision.CountCycle)
            {
                VerifyPauseSave(ctx, decision.NextFloor, persistPause);
                return;
            }
        }
        if (session.Plan.ReusesSave)
        {
            _verification ??= new(_dungeon, session.Source.ExpectedFloor ?? 0, prepared: session.Source, verifyOnly: true);
            if (!_verification.IsPrepared) _verification.Prepare(ctx);
            if (_verification.Update().Outcome == EntryOutcome.Pending) return;
            if (ctx.StatusIsError) { _complete = true; return; }
        }
        if (decision.DeleteOwnedSave)
        {
            if (session.OwnedSlot < 0 && _currentDutyContinuation is null)
            {
                ctx.StatusLine = "無法識別本任務建立的存檔；停止清理。";
                ctx.StatusIsError = true;
                _complete = true;
                return;
            }
            if (session.OwnedSlot >= 0 && _delete == null)
            {
                _delete = new GenericDeleteSaveFlow(_dungeon,
                    session.OwnedSlot, slots => ValidateOwnedSave(slots));
                _delete.Prepare(ctx);
            }
            if (_delete != null)
            {
                if (!_delete.Update(framework)) return;
                if (ctx.StatusIsError) { _complete = true; return; }
                session.ConfirmOwnedSaveDeleted();
            }
        }
        CountsAsCycle = decision.CountCycle;
        _recoverFailure = decision.RecoverFailure;
        session.CompleteAttempt(decision, ctx.HoardDiscoveries);
        if (session.PersistPause != null)
        {
            FinishPause(ctx, "已完成最終層與收尾，任務已停止。");
            return;
        }
        if (retainedUnidentifiedTerminalSave)
            ctx.StatusLine = "終局已完成；未識別原存檔槽位，因此保留存檔。";
        else if (decision.Error.Length > 0)
        {
            ctx.StatusLine = ctx.AttemptAbortReason + decision.Error + " FSD 已停止。";
            ctx.StatusIsError = true;
        }
        else
            ctx.StatusLine = decision.RecoverFailure ? $"攻略失敗 {session.Failures} 次；從第 {session.NextFloor} 層重開。"
                : decision.CountCycle ? "本輪完成；存檔收尾已驗證。"
                : $"本組通關；將接續 {session.NextFloor} 層。";
        Service.Log.Info($"[Farming] mode={session.Plan.Mode}, entry={_entryFloor}, clear={ctx.DutyCompletionObserved}, failure={ctx.DutyFailureObserved}, harvest={ctx.HarvestComplete}, discoveries={ctx.HoardDiscoveries}, aborted={_aborted}, reason={ctx.AttemptAbortReason}, cycle={CountsAsCycle}, next={session.NextFloor}, deleteOwned={decision.DeleteOwnedSave}, source={session.Source.Description}");
        _complete = true;
    }

    private void VerifyPauseSave(RunContext ctx, int nextFloor, Func<SaveSlotSnapshot, string?> persist)
    {
        if (session.OwnedSlot < 0)
        {
            FinishPause(ctx, "無法辨認本任務的存檔槽位。", error: true);
            return;
        }
        if (_verification == null)
        {
            session.Source.ConfigureForEntry(false, nextFloor, session.OwnedSlot, session.Plan.MaxFloor);
            _verification = new(_dungeon, nextFloor, prepared: session.Source, verifyOnly: true);
            _verification.Prepare(ctx);
        }
        var result = _verification.Update();
        if (result.Outcome == EntryOutcome.Pending) return;
        if (result.Outcome != EntryOutcome.SourceVerified || session.Source.Pinned is not { } slot)
        {
            FinishPause(ctx, result.Reason, error: true);
            return;
        }
        var error = persist(slot);
        FinishPause(ctx, error ?? $"已暫停。繼續時從第 {nextFloor} 層開始。", error != null);
    }

    private void FinishPause(RunContext ctx, string status, bool error = false)
    {
        _aborted = true;
        _complete = true;
        ctx.StatusLine = status;
        ctx.StatusIsError = error;
        ctx.RecordDiagnostic?.Invoke(error ? "farming-pause-failed" : "farming-pause-complete",
            new { floor = _entryFloor, slot = session.OwnedSlot, status });
    }

    private bool TryEnterDuty(RunContext ctx, IFramework framework)
    {
        if (session.ResumeCharacter is { } character && Service.PlayerState.ContentId != character)
        {
            ctx.StatusLine = "繼續期間角色已變更，請使用暫停時的角色重試。";
            ctx.StatusIsError = true;
            return false;
        }

        if (_freeSaveSlot is { } cleanup)
        {
            if (Environment.TickCount64 - _freeSaveSlotStartedAt >= 45_000)
            {
                ctx.StatusLine = "清理存檔逾時，未繼續入場。";
                ctx.StatusIsError = true;
                return false;
            }
            if (!cleanup.Update(framework) || ctx.StatusIsError)
                return false;
            _freeSaveSlot = null;
            _entry = new(_dungeon, session.NextFloor, preferredSlot: 1);
            _entry.Prepare(ctx);
        }

        // An entry controller owns its selected source and UI confirmation.
        // Do not bypass it by attaching when its own entry arrives in-game.
        if (_entry == null)
        {
            var facts = new FarmingEntryFacts(
                !ctx.Duty.StateReadFailed && !ctx.Duty.IsTransitioning && !DutyState.IsClientTransitioning,
                ctx.Duty.IsInDuty, ctx.Duty.DungeonId, ctx.Duty.Floor);
            var decision = FarmingEntryPolicy.Decide(facts, _dungeon.DutyDungeonId, session.NextFloor, session.OwnedSlot);
            if (decision.Action == FarmingEntryAction.Wait)
            {
                _entranceTravel?.Suspend();
                if (_entranceTravel == null) ctx.StatusLine = decision.Reason;
                return false;
            }
            if (decision.Action == FarmingEntryAction.Enter && _dungeon.AetheryteId != 0)
            {
                _entranceTravel ??= new(_dungeon, ctx);
                if (!_entranceTravel.Update()) return false;
                _entranceTravel.Dispose();
                _entranceTravel = null;
            }

            ctx.RecordDiagnostic?.Invoke("farming-entry-observed", new
            {
                action = decision.Action.ToString(), facts, expectedDungeon = _dungeon.DutyDungeonId,
                entryFloor = session.NextFloor, ownedSlot = session.OwnedSlot
            });
            if (decision.Action == FarmingEntryAction.Conflict)
            {
                ctx.StatusLine = decision.Reason;
                ctx.StatusIsError = true;
                return false;
            }
            if (decision.Action == FarmingEntryAction.Attach)
            {
                if (session.ConfirmResume != null)
                {
                    ctx.StatusLine = "繼續前須在副本外驗證原存檔。";
                    ctx.StatusIsError = true;
                    return false;
                }
                BeginDuty(ctx, session.NextFloor);
                ctx.PreparedSaveDescription = $"原副本接續，存檔 {session.OwnedSlot + 1}，起點 {_entryFloor} 層";
                ctx.StatusLine = $"原副本接續：第 {ctx.Duty.Floor} 層。";
                Service.Log.Info($"[Farming] Resumed existing duty: floor={ctx.Duty.Floor}, entryFloor={_entryFloor}, ownedSlot={session.OwnedSlot}, holdOnFailure={session.Plan.HoldOnFailure}");
                return true;
            }
            if (session.Plan.SaveUse == SaveUse.Prepared || session.OwnedSlot >= 0 ||
                session.Plan.ContinuesExistingProgress)
            {
                var source = session.PrepareSource();
                _entry = new(_dungeon, source.ExpectedFloor ?? 0, prepared: source);
            }
            else
            {
                _creatingSave = true;
                _entry = new(_dungeon, session.NextFloor, preferredSlot: 0,
                    allowReplacement: ctx.Configuration.Farming.AutoDeleteSaveWhenFull);
            }
            _entry.Prepare(ctx);
        }
        var entry = _entry.Update();
        if (entry.Outcome == EntryOutcome.SaveSlotsFull && _entry.ReplacementCandidate is { } candidate)
        {
            if (session.IsPausedSlotProtected?.Invoke(candidate.Index) == true)
            {
                ctx.StatusLine = "第 2 槽是可繼續的暫停存檔，無法自動清理。";
                ctx.StatusIsError = true;
                return false;
            }
            _entry.Dispose();
            _entry = null;
            _freeSaveSlotStartedAt = Environment.TickCount64;
            _freeSaveSlot = new(_dungeon, candidate.Index, slots =>
                session.IsPausedSlotProtected?.Invoke(candidate.Index) != true &&
                SaveSlotReplacementPolicy.IsStillEligible(slots, candidate)
                    ? null
                    : "存檔列表已變更，已停止自動清理。");
            _freeSaveSlot.Prepare(ctx);
            return false;
        }
        if (entry.Outcome == EntryOutcome.SourceConflict)
            session.RejectResumeSource?.Invoke(entry.Reason);
        if (entry.Outcome != EntryOutcome.Entered) return false;
        if (ctx.Duty.DungeonId != _dungeon.DutyDungeonId || ctx.Duty.Floor != entry.Floor)
        {
            ctx.StatusLine = "入場地宮／樓層與來源不符；請手動退本並檢查存檔。";
            ctx.StatusIsError = true;
            return false;
        }
        if (_creatingSave && !session.BindCreatedSave(entry.Slot))
        {
            ctx.StatusLine = "入場來源未由本任務建立；停止自動化，請手動退本。";
            ctx.StatusIsError = true;
            return false;
        }
        if (session.Plan.ContinuesExistingProgress && session.OwnedSlot < 0 && !session.BindContinuationSave())
        {
            ctx.StatusLine = "接續存檔未通過槽位驗證；停止自動化，請檢查存檔。";
            ctx.StatusIsError = true;
            return false;
        }
        if (session.ConfirmResume is { } confirmResume)
        {
            if (confirmResume() is { } error)
            {
                ctx.StatusLine = error;
                ctx.StatusIsError = true;
                return false;
            }
            session.MarkResumed();
        }
        BeginDuty(ctx, entry.Floor);
        return true;
    }

    private void BeginDuty(RunContext ctx, int entryFloor)
    {
        _entryFloor = entryFloor;
        _entered = true;
        ctx.TerminalRewardsRequired =
            _entryFloor == 91 && ResultAltarPolicy.TryGet(_dungeon.DungeonId, 100, out _) ||
            _entryFloor == 191 && Potd200RewardPolicy.IsTerminal(_dungeon.DungeonId, 200);
        ctx.EntryReadyForDuty = true;
    }

    private void ObserveDutyCompletion(RunContext ctx)
    {
        // This result can predate FSD attachment. Use the same current evidence
        // for normal entry and attachment, then retain it through exit/cleanup.
        if (ctx.DutyCompletionObserved || ctx.DutyFailureObserved || !ctx.Duty.IsInDuty ||
            ctx.Duty.DungeonId != _dungeon.DutyDungeonId || ctx.Duty.Floor < _entryFloor ||
            ctx.Duty.Floor >= _entryFloor + 10 || !ctx.Duty.IsBossFloor || !ctx.Duty.PassageOpen ||
            Service.DutyState.IsDutyStarted ||
            Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat] ||
            Service.LocalPlayer is not { IsDead: false }) return;
        ctx.MarkDutyCompleted();
        ctx.RecordDiagnostic?.Invoke("observed-completed-duty", new
        {
            floor = ctx.Duty.Floor, passageOpen = ctx.Duty.PassageOpen, ownedSlot = session.OwnedSlot
        });
    }

    internal FarmingSession Session => session;
    private static string DungeonShortLabel(uint dungeonId) => dungeonId switch
    {
        1 => "死宮",
        2 => "天宮",
        _ => "PT"
    };
    private string? ValidateOwnedSave(IReadOnlyList<SaveSlotSnapshot> slots) =>
        OwnedSavePolicy.ValidateForDeletion(slots, session.OwnedSlot, _entryFloor, session.Source.Pinned);
    private void RequestLeave() => _context?.RunOptions.Update(o => o.LeaveMode = LeaveMode.Immediate);
    public void Dispose()
    {
        _entranceTravel?.Dispose();
        _entry?.Dispose();
        _verification?.Dispose();
        _exit?.Dispose();
        _context = null;
    }
}
