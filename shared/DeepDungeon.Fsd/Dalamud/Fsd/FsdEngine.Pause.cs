using System.Text.Json;
using Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private sealed record ProgressionCheckpoint(ulong Character, FarmingPlan Plan, SaveSlotSnapshot Slot,
        int CompletedCycles, int Failures, string? InvalidSourceReason = null, DateTimeOffset? SavedAt = null);

    private readonly string _checkpointPath;
    private readonly string _checkpointDirectory;
    private (ProgressionCheckpoint Checkpoint, string Path, DateTimeOffset Timestamp)[] _resumeSaves = [];
    private long _nextResumeRefresh;
    private string? _resumeNotice;

    private bool ProgressionPauseLocked => _ddHost?.ActiveFarming?.Session.PersistPause != null;
    private bool CanRequestProgressionPause => !ProgressionPauseLocked && _ddHost?.ActiveFarming?.CanRequestPause == true;

    private bool HasResumableSave(ulong character, uint dungeonId)
    {
        RefreshResumeSaves();
        return _resumeSaves.Any(save => save.Checkpoint.Character == character &&
            save.Checkpoint.Plan.DungeonId == dungeonId);
    }

    private static ProgressionCheckpoint ReadProgressionCheckpoint(string path)
    {
        var checkpoint = JsonSerializer.Deserialize<ProgressionCheckpoint>(File.ReadAllBytes(path));
        if (checkpoint is not { Character: > 0, CompletedCycles: >= 0, Failures: >= 0,
                Plan: { IsProgression: true, SaveUse: SaveUse.Create, DungeonId: 1 or 2 or 4,
                    TargetCycles: > 0, HoldAfterClear: false, StopAfterFloor: null },
                Slot: { Index: 0 or 1, Empty: false, Enterable: true, RowFingerprint.Length: 64 } } ||
            checkpoint.Slot.StartFloor <= checkpoint.Plan.StartFloor ||
            checkpoint.Slot.StartFloor > checkpoint.Plan.TerminalEntryFloor ||
            checkpoint.Slot.StartFloor % 10 != 1 ||
            !checkpoint.Plan.Infinite && checkpoint.CompletedCycles >= checkpoint.Plan.TargetCycles)
            throw new InvalidDataException("暫停紀錄內容無效。");
        return checkpoint;
    }

    private IEnumerable<string> CheckpointPaths()
    {
        if (File.Exists(_checkpointPath))
            yield return _checkpointPath;
        if (Directory.Exists(_checkpointDirectory))
            foreach (var path in Directory.EnumerateFiles(_checkpointDirectory, "*.json"))
                yield return path;
    }

    private void RefreshResumeSaves()
    {
        var saves = new List<(ProgressionCheckpoint Checkpoint, string Path, DateTimeOffset Timestamp)>();
        foreach (var path in CheckpointPaths())
        {
            try
            {
                var saved = ReadProgressionCheckpoint(path);
                if (saved.InvalidSourceReason == null)
                    saves.Add((saved, path, saved.SavedAt ?? File.GetLastWriteTimeUtc(path)));
            }
            catch (FileNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                _resumeNotice = "有暫停紀錄無法讀取，請檢查 FSD 日誌。";
                Service.Log.Warning(ex, $"[FSD] Could not read progression checkpoint {Path.GetFileName(path)}");
            }
        }
        _resumeSaves = saves.OrderByDescending(save => save.Timestamp)
            .DistinctBy(save => (save.Checkpoint.Character, save.Checkpoint.Plan.DungeonId, save.Checkpoint.Slot.Index))
            .ToArray();
    }

    public void DrawProgressionSaves()
    {
        long now = Environment.TickCount64;
        if (now >= _nextResumeRefresh)
        {
            RefreshResumeSaves();
            _nextResumeRefresh = now + 2000;
        }

        ulong character = Service.PlayerState.ContentId;
        var characterSaves = _resumeSaves.Where(save => save.Checkpoint.Character == character).ToArray();
        if (characterSaves.Length == 0)
            ImGui.TextWrapped("目前角色沒有可自動繼續的存檔。此功能僅支援透過「本組出本後暫停」留下的存檔，可嘗試開啟遊戲內的存檔畫面讀取。");

        bool busy = _currentInDeepDungeon || DutyState.IsClientTransitioning ||
            !GetOutsideDutyOperationDecision(OutsideDutyOperation.StartOrEnter).Allowed;
        foreach (var (checkpoint, path, timestamp) in characterSaves)
        {
            string dungeon = checkpoint.Plan.DungeonId switch
            {
                1 => "死者宮殿",
                2 => "天之御柱",
                _ => "朝聖交錯路"
            };
            string time = timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            ImGui.TextUnformatted($"{dungeon} · {checkpoint.Plan.DisplayName}");
            ImGui.TextUnformatted($"第 {checkpoint.Slot.StartFloor} 層 · 存檔 {checkpoint.Slot.Index + 1} · {time}");
            ImGui.PushID(path);
            ImGui.BeginDisabled(busy);
            if (ImGui.Button("繼續此存檔"))
                ResumeProgression(checkpoint, path);
            ImGui.EndDisabled();
            ImGui.PopID();
            ImGui.Separator();
        }
        if (_resumeNotice is { } notice)
            ImGui.TextWrapped(notice);
    }

    private static void WriteCheckpoint(string path, ProgressionCheckpoint checkpoint, bool overwrite)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(checkpoint));
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private void RequestProgressionPause()
    {
        if (!CanRequestProgressionPause || _ddHost is not { ActiveFarming: { } farming } host) return;
        ulong character = Service.PlayerState.ContentId;
        if (character == 0)
        {
            _farmingError = "無法辨認目前角色，請稍後再要求暫停。";
            return;
        }
        var session = farming.Session;
        var plan = session.Plan with
        {
            TargetCycles = host.TargetLoops, Infinite = host.Infinite,
            BandedEnabled = session.BandedEnabled, OpenGold = session.OpenGold,
            OpenSilver = session.OpenSilver, OpenBronze = session.OpenBronze
        };
        var completedCycles = host.CompletedLoops;
        session.PersistPause = slot => PersistProgressionCheckpoint(
            new(character, plan, slot, completedCycles, session.Failures, SavedAt: DateTimeOffset.UtcNow));
        _farmingError = null;
    }

    private string? PersistProgressionCheckpoint(ProgressionCheckpoint checkpoint)
    {
        if (Service.PlayerState.ContentId != checkpoint.Character || checkpoint.Slot.RowFingerprint is not { Length: 64 })
            return "無法驗證目前角色與存檔資料，暫停紀錄未寫入。";
        try
        {
            Directory.CreateDirectory(_checkpointDirectory);
            string path = Path.Combine(_checkpointDirectory,
                $"{checkpoint.Character}-{checkpoint.Plan.DungeonId}-{checkpoint.Slot.Index}.json");
            WriteCheckpoint(path, checkpoint, overwrite: true);
            if (File.Exists(_checkpointPath))
            {
                try
                {
                    var legacy = ReadProgressionCheckpoint(_checkpointPath);
                    if (legacy.Character == checkpoint.Character && legacy.Plan.DungeonId == checkpoint.Plan.DungeonId &&
                        legacy.Slot.Index == checkpoint.Slot.Index)
                        File.Delete(_checkpointPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    Service.Log.Warning(ex, "[FSD] Could not retire the previous checkpoint");
                }
            }
            _nextResumeRefresh = 0;
            _resumeNotice = null;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Service.Log.Error(ex, "[FSD] Progression checkpoint write failed");
            return "寫入暫停紀錄失敗，遊戲存檔已保留。";
        }
    }

    private void ResumeProgression(ProgressionCheckpoint checkpoint, string path)
    {
        _resumeNotice = TryResumeProgression(checkpoint, path);
        _nextResumeRefresh = 0;
    }

    private string? TryResumeProgression(ProgressionCheckpoint checkpoint, string path)
    {
        if (_currentInDeepDungeon || _dutyState?.IsInDuty == true || DutyState.IsClientTransitioning)
            return "請在副本外、過圖完成後繼續。";
        if (Service.LocalPlayer is not { IsDead: false } player || Service.PlayerState.ContentId != checkpoint.Character)
            return "請使用暫停任務時的角色繼續。";
        if (checkpoint.Plan.Mode == FarmingMode.DeepProgression && checkpoint.Plan.DungeonId is 1 or 2 && player.ClassJob.RowId is not (23 or 31 or 38))
            return "死宮／天宮死靈術士（成就）模式僅支援機工士、吟遊詩人、舞者。";
        try
        {
            if (ReadProgressionCheckpoint(path) != checkpoint)
                return "暫停紀錄已變更，請重新選擇。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return "暫停紀錄目前無法讀取。";
        }

        var session = new FarmingSession(checkpoint.Plan);
        session.IsPausedSlotProtected = slot => IsPausedSlotProtected(checkpoint.Plan.DungeonId, slot);
        session.RestoreCheckpoint(checkpoint.Slot, checkpoint.Failures, checkpoint.Character,
            () => ConfirmProgressionResume(checkpoint, path),
            reason => InvalidateProgressionSource(checkpoint, path, reason));
        if (!TryStartOutsideDutyFsd(() => new FarmingScenario(session), checkpoint.Plan.TargetCycles,
                checkpoint.Plan.Infinite, null, out string error, completedLoops: checkpoint.CompletedCycles,
                useDetailedMap: false))
            return error;

        var settings = _configuration.Farming;
        settings.DungeonId = checkpoint.Plan.DungeonId;
        settings.SetMode(settings.DungeonId, checkpoint.Plan.Mode);
        var preferences = settings.GetPreferences(_configuration, checkpoint.Plan.Mode);
        preferences.Cycles = checkpoint.Plan.TargetCycles;
        preferences.Infinite = checkpoint.Plan.Infinite;
        preferences.BandedEnabled = checkpoint.Plan.BandedEnabled;
        preferences.OpenGold = checkpoint.Plan.OpenGold;
        preferences.OpenSilver = checkpoint.Plan.OpenSilver;
        preferences.OpenBronze = checkpoint.Plan.OpenBronze;
        _configuration.Save();
        return "正在前往入口並確認存檔。";
    }

    private bool IsPausedSlotProtected(uint dungeon, int slot)
    {
        ulong character = Service.PlayerState.ContentId;
        string path = Path.Combine(_checkpointDirectory, $"{character}-{dungeon}-{slot}.json");
        try
        {
            if (File.Exists(path) && ReadProgressionCheckpoint(path).InvalidSourceReason == null)
                return true;
            if (File.Exists(_checkpointPath))
            {
                var legacy = ReadProgressionCheckpoint(_checkpointPath);
                return legacy.InvalidSourceReason == null && legacy.Character == character &&
                    legacy.Plan.DungeonId == dungeon && legacy.Slot.Index == slot;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Service.Log.Warning(ex, "[FSD] Could not verify paused-save protection");
            return true;
        }
    }

    private void InvalidateProgressionSource(ProgressionCheckpoint checkpoint, string path, string reason)
    {
        _resumeNotice = $"原存檔無法繼續：{reason}";
        _nextResumeRefresh = 0;
        try
        {
            if (Service.PlayerState.ContentId == checkpoint.Character && ReadProgressionCheckpoint(path) == checkpoint)
                WriteCheckpoint(path, checkpoint with { InvalidSourceReason = reason }, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Service.Log.Warning(ex, "[FSD] Could not invalidate the paused save");
        }
    }

    private string? ConfirmProgressionResume(ProgressionCheckpoint checkpoint, string path)
    {
        if (Service.PlayerState.ContentId != checkpoint.Character)
            return "入場期間角色已變更，請檢查暫停紀錄。";
        try
        {
            if (ReadProgressionCheckpoint(path) != checkpoint)
                return "入場期間本機暫停紀錄已變更，請檢查後重試。";
            File.Delete(path);
            _nextResumeRefresh = 0;
            _resumeNotice = $"已從第 {checkpoint.Slot.StartFloor} 層繼續。";
            Service.Log.Info($"[FSD] Resumed checkpoint: dungeon={checkpoint.Plan.DungeonId}, mode={checkpoint.Plan.Mode}, floor={checkpoint.Slot.StartFloor}, slot={checkpoint.Slot.Index + 1}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Service.Log.Error(ex, "[FSD] Progression checkpoint could not be consumed");
            return "已入場，但無法清除暫停紀錄。FSD 已停止。";
        }
    }
}
