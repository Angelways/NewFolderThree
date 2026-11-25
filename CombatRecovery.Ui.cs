using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace NewFolderThree;

internal sealed partial class CombatRecovery
{
    public void Draw()
    {
        ImGui.TextWrapped("此功能會在戰鬥中監測角色是否施放 GCD 技能。若有一段時間未施放，會自動施放一次 GCD 技能，嘗試恢復自動循環（僅限機工士）。");
        ImGui.TextWrapped("FSD 在首領樓層（含神聖路 99 層）等待 30 秒仍未開戰時，機工士會對最近可攻擊的敵人使用一次分裂彈或熱分裂彈。");
        ImGui.Spacing();
        if (ImGui.Button(monitoringEnabled ? "停止監測" : "啟動監測"))
            monitoringEnabled = !monitoringEnabled;

        ImGui.SameLine();
        ImGui.BeginDisabled(pendingCast.HasValue || testRequested);
        if (ImGui.Button("測試技能"))
            testRequested = true;
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.TextUnformatted($"狀態  {monitorStatus}");
        if (timer.IsRunning)
        {
            var remaining = timer.RemainingMilliseconds(Environment.TickCount64);
            ImGui.TextUnformatted(remaining > 0
                ? $"距自動嘗試  {Math.Ceiling(remaining / 1000d):0} 秒"
                : "已達到 10 秒閾值");
        }

        ImGui.TextWrapped(castStatus);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted($"外掛成功施放  {castsConfirmed} 次");

        if (history.Count == 0)
        {
            ImGui.TextDisabled("尚無施放紀錄");
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("CastHistory", 3, flags, new Vector2(0, Math.Max(80, ImGui.GetContentRegionAvail().Y))))
            return;

        try
        {
            ImGui.TableSetupColumn("時間");
            ImGui.TableSetupColumn("技能");
            ImGui.TableSetupColumn("來源");
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            for (var index = history.Count - 1; index >= 0; index--)
            {
                var entry = history[index];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(entry.Time.ToString("MM-dd HH:mm:ss"));
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(SkillName(entry.ActionId));
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(entry.IsTest ? "測試" : "自動");
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }
}
