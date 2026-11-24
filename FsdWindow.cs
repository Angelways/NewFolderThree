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
    private readonly Configuration _configuration;
    private readonly DeepDungeonSpeedController _potdSpeed;

    public FsdWindow(FsdApplication application, YAxisAdjustController yAxis,
        CombatRecovery combatRecovery, ReconnectController reconnect,
        Configuration configuration, DeepDungeonSpeedController potdSpeed)
        : base($"你是壞孩子###{ProductIdentity.InternalName}")
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _yAxis = yAxis ?? throw new ArgumentNullException(nameof(yAxis));
        _combatRecovery = combatRecovery;
        _reconnect = reconnect;
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _potdSpeed = potdSpeed ?? throw new ArgumentNullException(nameof(potdSpeed));
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

        if (ImGui.BeginTabItem("深宮移速"))
        {
            DrawPotdSpeedSettings();
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

    private void DrawPotdSpeedSettings()
    {
        bool enabled = _configuration.PotdSpeedEnabled;
        bool changed = ImGui.Checkbox("啟用移速調整", ref enabled);

        float addition = _configuration.PotdSpeedAddition;
        changed |= ImGui.SliderFloat("移速倍率增量", ref addition, 0f, 5f, "%.2f");

        bool maxAcceleration = _configuration.PotdMaxAcceleration;
        changed |= ImGui.Checkbox("最大加速度", ref maxAcceleration);

        if (changed)
        {
            _configuration.PotdSpeedEnabled = enabled;
            _configuration.PotdSpeedAddition = float.IsFinite(addition)
                ? Math.Clamp(addition, 0f, 5f)
                : 0f;
            _configuration.PotdMaxAcceleration = maxAcceleration;
            _configuration.SavePluginConfig();
        }

        ImGui.Separator();
        ImGui.TextWrapped("本移速調整適用於死者宮殿及天之御柱，整十層除外。");
        ImGui.Separator();
        ImGui.TextWrapped("同時，本插件會在特定的層數阻止某些危險技能的釋放。");
        if (_potdSpeed.Status is { Length: > 0 } status)
        {
            ImGui.Separator();
            ImGui.TextWrapped(status);
        }
    }

}
