using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Dalamud;

namespace NewFolderThree;

internal sealed class FsdWindow : Window
{
    private readonly FsdApplication _application;
    private readonly YAxisAdjustController _yAxis;
    private readonly CombatRecovery _combatRecovery;
    private readonly ReconnectController _reconnect;

    public FsdWindow(FsdApplication application, YAxisAdjustController yAxis,
        CombatRecovery combatRecovery, ReconnectController reconnect)
        : base($"你是壞孩子###{ProductIdentity.InternalName}")
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _yAxis = yAxis ?? throw new ArgumentNullException(nameof(yAxis));
        _combatRecovery = combatRecovery;
        _reconnect = reconnect;
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##FsdHostTabs"))
            return;

        if (ImGui.BeginTabItem("說明"))
        {
            _application.DrawCompanionSettings();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextWrapped("它也許可以幫助你挖到一些寶藏並成為死靈術士");
            ImGui.TextWrapped("請啟用你所用輸出外掛的非戰鬥時主動攻擊能力.\n請關閉任意其他外掛的目標選擇.\n請關閉任意其他外掛的移動速度修正.");
            ImGui.TextWrapped("死靈術士需要強強PC, 請勿制限低幀");
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("FSD"))
        {
            if (_reconnect.Status is { } status)
                ImGui.TextWrapped(status);
            if (_reconnect.IsPending && ImGui.Button("取消重連並停止 FSD"))
                _reconnect.Cancel();
            bool reconnecting = _reconnect.IsPending;
            ImGui.BeginDisabled(reconnecting);
            _application.DrawDeepDungeonFormalPanel();
            ImGui.Separator();
            ImGui.Text("通用輔助");
            _application.DrawGeneralAssistantSettings();
            _yAxis.DrawSettings();
            ImGui.EndDisabled();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("自動嘗試恢復戰鬥循環"))
        {
            _combatRecovery.Draw();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("目前可以自動繼續的存檔"))
        {
            _application.DrawProgressionSaves();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

}
